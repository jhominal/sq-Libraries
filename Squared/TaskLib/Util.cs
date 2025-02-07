﻿using System;
using System.Collections.Generic;
using System.Threading;
using Squared.Util;
using Squared.Util.Event;
using System.Collections;
using System.Linq;
using System.Linq.Expressions;
using Squared.Threading;

namespace Squared.Task {
    public static class EnumeratorExtensionMethods {
        internal struct Disposer : IDisposable {
            readonly object Obj;

            public Disposer (object obj) {
                Obj = obj;
            }

            public void Dispose () {
                if (Obj is IDisposable)
                    ((IDisposable)Obj).Dispose();
            }
        }

        public static IEnumerator<object> EnumerateViaThreadpool<T> (IEnumerator<T> enumerator, int blockSize) {
            using (new Disposer(enumerator)) {
                var buffer = new List<T>(blockSize);
                var nv = new NextValue(null);

                WaitCallback moveNext = (state) => {
                    var f = (Future<bool>)state;
                    try {
                        while (buffer.Count < blockSize) {
                            if (enumerator.MoveNext()) {
                                buffer.Add(enumerator.Current);
                            } else {
                                f.SetResult(true, null);
                                return;
                            }
                        }

                        f.SetResult(false, null);
                    } catch (Exception e) {
                        f.SetResult(false, e);
                    }
                };

                while (true) {
                    buffer.Clear();
                    var f = new Future<bool>();
                    ThreadPool.QueueUserWorkItem(moveNext, f);
                    yield return f;

                    bool atEnd;
                    Exception error;
                    if (f.GetResult(out atEnd, out error)) {
                        nv.Value = buffer;
                        yield return nv;

                        if (atEnd)
                            yield break;
                    }
                }
            }
        }

        public static RunToCompletion<T> Run<T> (this IEnumerator<object> task, out Future<T> future) {
            var rtc = new RunToCompletion<T>(task, TaskExecutionPolicy.RunWhileFutureLives);
            future = rtc.Future;
            return rtc;
        }

        public static RunToCompletion Run (this IEnumerator<object> task, out IFuture future) {
            var rtc = new RunToCompletion(task, TaskExecutionPolicy.RunWhileFutureLives);
            future = rtc.Future;
            return rtc;
        }

        public static StoreResult<T> Bind<T> (this IEnumerator<object> task, Expression<Func<T>> target) {
            var sr = new StoreResult<T>(task, target);
            return sr;
        }
    }

    /// <summary>
    /// Schedules a task to run to completion and store its result into a target field or property.
    /// </summary>
    public class StoreResult<T> : ISchedulable {
        readonly IEnumerator<object> _Task;
        readonly SchedulableGeneratorThunk _Thunk;
        readonly TaskExecutionPolicy _ExecutionPolicy;
        readonly Future<T> _Future;
        IFuture _CompletionSignal;

        public StoreResult (
            IEnumerator<object> task, Expression<Func<T>> target, 
            TaskExecutionPolicy executionPolicy = TaskExecutionPolicy.RunWhileFutureLives
        ) {
            _Task = task;
            _Thunk = new SchedulableGeneratorThunk(_Task);
            _ExecutionPolicy = executionPolicy;
            _Future = Future.New<T>();
            _Future.Bind(target);
            _Future.RegisterOnComplete(Completed);
        }

        void Completed (IFuture f) {
            if (f.Failed)
                _CompletionSignal.Fail(f.Error);
            else
                _CompletionSignal.Complete();
        }

        void ISchedulable.Schedule (TaskScheduler scheduler, IFuture future) {
            _CompletionSignal = future;
            scheduler.Start(_Future, _Thunk, _ExecutionPolicy);
        }
    }

    /// <summary>
    /// Allows you to emulate a try { } finally block inside of a task, via using () { }.
    /// </summary>
    public struct Finally : IDisposable {
        Action _Action;

        public static Finally Do (Action action) {
            return new Finally { _Action = action };
        }

        public void Dispose () {
            if (_Action != null)
                _Action();
            _Action = null;
        }
    }

    /// <summary>
    /// Schedules your task to continue execution at the end of the current step.
    /// </summary>
    public class Yield : ISchedulable {
        void ISchedulable.Schedule (TaskScheduler scheduler, IFuture future) {
            future.Complete();
        }
    }

    /// <summary>
    /// Allows your task to yield a value as if it were a normal generator, and resume execution.
    /// </summary>
    public class NextValue {
        public object Value;

        public NextValue (object value) {
            Value = value;
        }
    }

    /// <summary>
    /// Schedules your task to continue execution at the beginning of the next step.
    /// </summary>
    public class WaitForNextStep : ISchedulable {
        void ISchedulable.Schedule (TaskScheduler scheduler, IFuture future) {
            scheduler.QueueWorkItemForNextStep(future.Complete);
        }
    }

    /// <summary>
    /// Completes when the specified future completes, or when (timeout) seconds have elapsed, whichever comes first.
    /// </summary>
    public class WaitWithTimeout : ISchedulable {
        readonly IFuture _Future;
        IFuture _TaskFuture, _SleepFuture;
        readonly double _Timeout;

        public WaitWithTimeout (IFuture future, double timeout) {
            _Future = future;
            _Timeout = timeout;
        }

        void ISchedulable.Schedule (TaskScheduler scheduler, IFuture future) {
            _SleepFuture = scheduler.Start(new Sleep(_Timeout));
            _TaskFuture = Future.WaitForFirst(_Future, _SleepFuture);
            _TaskFuture.RegisterOnComplete((f) => {
                if (f.Result == _SleepFuture)
                    future.Fail(new TimeoutException("WaitWithTimeout timed out."));
                else
                    future.Complete();
            });
        }
    }

    public class WaitForAllException : Exception {
        public readonly Exception[] Errors;

        public WaitForAllException (Exception[] errors) 
            : base ("One or more of the tasks being waited for failed.", errors[0]) {

            Errors = errors;
        }
    }

    /// <summary>
    /// Completes when all of the specified schedulable objects have completed.
    /// </summary>
    public class WaitForAll : ISchedulable {
        protected ISchedulable[] _Schedulables;
        protected IFuture[] _Futures;

        protected IFuture _WaitFuture;
        protected IFuture _ResultFuture;

        public WaitForAll (params object[] objects) {
            ISchedulable s;
            IFuture f;
            IEnumerator<object> t;

            _Schedulables = new ISchedulable[objects.Length];
            _Futures = new IFuture[objects.Length];

            for (int i = 0; i < objects.Length; i++) {
                if ((s = objects[i] as ISchedulable) != null)
                    _Schedulables[i] = s;
                else if ((f = objects[i] as IFuture) != null)
                    _Futures[i] = f;
                else if ((t = objects[i] as IEnumerator<object>) != null)
                    _Schedulables[i] = new SchedulableGeneratorThunk(t);
                else
                    throw new ArgumentException("WaitForAll arguments must be ISchedulable, IFuture or IEnumerator<object>", "objects");
            }
        }

        void HandleDisposed (IFuture wait) {
            foreach (var f in _Futures)
                f.Dispose();
        }

        void HandleResult (IFuture wait) {
            var errors = (from f in _Futures where f.Failed select f.Error).ToArray();

            if (errors.Length == 0)
                _ResultFuture.Complete();
            else
                _ResultFuture.Fail(new WaitForAllException(errors));
        }

        void ISchedulable.Schedule (TaskScheduler scheduler, IFuture future) {
            if (_Schedulables == null)
                throw new InvalidOperationException();
            else if (_Schedulables.Length == 0) {
                future.Complete();
                return;
            }

            for (int i = 0; i < _Schedulables.Length; i++) {
                var s = _Schedulables[i];
                if (s != null)
                    _Futures[i] = scheduler.Start(s, TaskExecutionPolicy.RunWhileFutureLives);
            }

            _Schedulables = null;
            _ResultFuture = future;

            _WaitFuture = Future.WaitForAll(_Futures);
            _WaitFuture.RegisterHandlers(HandleResult, HandleDisposed);
        }
    }

    /// <summary>
    /// Starts a task and stores the resulting future.
    /// </summary>
    public class Start : ISchedulable {
        IEnumerator<object> _Task;
        TaskExecutionPolicy _ExecutionPolicy;
        IFuture _Future;

        public Start (IEnumerator<object> task, TaskExecutionPolicy executionPolicy = TaskExecutionPolicy.RunWhileFutureLives) {
            _Task = task;
            _ExecutionPolicy = executionPolicy;
            _Future = null;
        }

        void ISchedulable.Schedule (TaskScheduler scheduler, IFuture future) {
            _Future = scheduler.Start(_Task, _ExecutionPolicy);
            future.Complete(_Future);
        }

        public IFuture Future {
            get {
                return _Future;
            }
        }
    }

    /// <summary>
    /// Runs as task as a background task so that you can ignore any errors (on the assumption that you have a background task error handler).
    /// </summary>
    public class RunAsBackground : ISchedulable {
        IEnumerator<object> _Task;
        IFuture _Future;

        public RunAsBackground (IEnumerator<object> task) {
            _Task = task;
        }

        void ISchedulable.Schedule (TaskScheduler scheduler, IFuture future) {
            _Future = scheduler.Start(_Task, TaskExecutionPolicy.RunAsBackgroundTask);

            _Future.RegisterOnComplete((f) => {
                object r;
                Exception e;
                f.GetResult(out r, out e);
                future.Complete(r);
            });
            _Future.RegisterOnDispose(future.Dispose);
        }

        public IFuture Future {
            get {
                return _Future;
            }
        }
    }

    public class RunToCompletion : RunToCompletion<object> {
        public RunToCompletion (IEnumerator<object> task)
            : base(task) {
        }

        public RunToCompletion (IEnumerator<object> task, TaskExecutionPolicy executionPolicy)
            : base(task, executionPolicy) {
        }

        new public IFuture Future {
            get {
                return base.Future;
            }
        }
    }

    /// <summary>
    /// Schedules a task to run to completion.
    /// </summary>
    public class RunToCompletion<T> : ISchedulable, IDisposable {
        IEnumerator<object> _Task;
        SchedulableGeneratorThunk _Thunk;
        TaskExecutionPolicy _ExecutionPolicy;
        Future<T> _Future;
        IFuture _CompletionSignal;

        public RunToCompletion (IEnumerator<object> task, TaskExecutionPolicy executionPolicy = TaskExecutionPolicy.RunWhileFutureLives) {
            _Task = task;
            _Thunk = new SchedulableGeneratorThunk(_Task);
            _Future = Threading.Future.New<T>();
            _Future.RegisterOnComplete(Completed);
            _ExecutionPolicy = executionPolicy;
        }

        void Completed (IFuture f) {
            _CompletionSignal.SetResult(_Future.Error == null, null);
        }

        void ISchedulable.Schedule (TaskScheduler scheduler, IFuture future) {
            _CompletionSignal = future;
            future.RegisterOnDispose(_Future.Dispose);
            scheduler.Start(_Future, _Thunk, _ExecutionPolicy);
        }

        public void Dispose () {
            if (_Future != null)
                _Future.Dispose();
            if (_CompletionSignal != null)
                _CompletionSignal.Dispose();
        }

        public void AssertSucceeded () {
            this._Future.AssertSucceeded();
        }

        public Future<T> Future {
            get {
                return _Future;
            }
        }

        public T Result {
            get {
                return _Future.Result;
            }
        }
    }

    /// <summary>
    /// Waits until a specified time (in ticks).
    /// </summary>
    public class SleepUntil : ISchedulable {
        long _EndWhen;

        public SleepUntil (long when) {
            _EndWhen = when;
        }

        void ISchedulable.Schedule (TaskScheduler scheduler, IFuture future) {
            scheduler.QueueSleep(_EndWhen, future);
        }
    }

    /// <summary>
    /// Waits for a specified amount of time (in seconds).
    /// </summary>
    public class Sleep : ISchedulable {
        private readonly ITimeProvider DefaultTimeProvider;

        long? _InitialNow;
        long _Duration;

        public Sleep (double duration) {
            var ts = TaskScheduler.Current;
            if (ts != null)
                DefaultTimeProvider = ts.TimeProvider;
            else
                DefaultTimeProvider = Time.DefaultTimeProvider;

            _InitialNow = DefaultTimeProvider.Ticks;
            _Duration = TimeSpan.FromSeconds(duration).Ticks;
        }

        public double Duration {
            set {
                _Duration = TimeSpan.FromSeconds(value).Ticks;
            }
            get {
                return TimeSpan.FromTicks(_Duration).TotalSeconds;
            }
        }

        void ISchedulable.Schedule (TaskScheduler scheduler, IFuture future) {
            long until;

            var timeProvider = scheduler.TimeProvider;
            if (timeProvider != DefaultTimeProvider)
                _InitialNow = null;

            if (_InitialNow.HasValue) {
                until = _InitialNow.Value + _Duration;
                _InitialNow = null;
            } else {
                until = timeProvider.Ticks + _Duration;
            }

            scheduler.QueueSleep(until, future);
        }
    }

    /// <summary>
    /// Manages iterating over a task that generates a sequence of values of type T. A task-oriented equivalent to IEnumerator.
    /// </summary>
    public class TaskEnumerator<T> : IDisposable, IEnumerable<T> {
        public static int DefaultBufferSize = 256;

        public class FetchThunk : ISchedulable {
            public readonly TaskEnumerator<T> Enumerator;

            public FetchThunk (TaskEnumerator<T> enumerator) {
                Enumerator = enumerator;
            }

            public void Schedule (TaskScheduler scheduler, IFuture future) {
                if (!Enumerator._Initialized)
                    Enumerator.Initialize(scheduler, future);
                else
                    Enumerator.WaitForRows(future);
            }
        }

        public struct Enumerator : IEnumerator<T> {
            public TaskEnumerator<T> Parent;
            public List<T>.Enumerator Inner;

            private bool _Ready;

            public Enumerator (TaskEnumerator<T> parent, List<T>.Enumerator inner) {
                Parent = parent;
                Inner = inner;
                _Ready = false;
            }

            public T Current {
                get {
                    if (_Ready)
                        return Inner.Current;
                    else
                        throw new InvalidOperationException();
                }
            }

            public void Dispose () {
                if (Parent != null) {
                    Inner.Dispose();

                    if (!Parent.Disposed)
                        Parent.ReadyForMore();

                    Parent = null;
                }
            }

            object IEnumerator.Current {
                get { return Inner.Current; }
            }

            public bool MoveNext () {
                if (Parent == null)
                    return _Ready = false;
                else if (Parent.Disposed)
                    return _Ready = false;

                if (Inner.MoveNext())
                    return _Ready = true;
                else
                    return _Ready = false;
            }

            public void Reset () {
                throw new NotImplementedException();
            }
        }

        public readonly int Capacity;
        public Action OnEarlyDispose = null;

        protected bool _Initialized = false;
        protected IEnumerator<object> _Task = null;
        protected TaskScheduler _Scheduler = null;
        protected SchedulableGeneratorThunk _Thunk = null;
        protected IFuture _ResumeFuture = null;
        protected IFuture _SequenceFuture = null;
        protected IFuture _ReadyForMoreFuture = null;
        protected List<T> _Buffer;
        protected FetchThunk _FetchThunk;

        protected TaskEnumerator (int capacity) {
            Capacity = capacity;
            _FetchThunk = new TaskEnumerator<T>.FetchThunk(this);
            _Buffer = new List<T>(capacity);
        }

        public TaskEnumerator (IEnumerator<object> task, int capacity)
            : this (capacity) {
            _Task = task;
        }

        public TaskEnumerator (IEnumerator<object> task)
            : this(task, DefaultBufferSize) {
        }

        protected void Start () {
            _Thunk = new SchedulableGeneratorThunk(_Task);
            _Thunk.OnNextValue = OnNextValue;

            _SequenceFuture = _Scheduler.Start(_Thunk, TaskExecutionPolicy.RunWhileFutureLives);
            _SequenceFuture.RegisterOnDispose((f) => {
                Resume();
                Dispose();
            });
            _SequenceFuture.RegisterOnComplete((f) => {
                if (f.Failed)
                    Fail(f.Error);
                else
                    Resume();
            });
        }

        protected void Initialize (TaskScheduler scheduler, IFuture resumeFuture) {
            _Initialized = true;
            _Scheduler = scheduler;
            
            _ResumeFuture = resumeFuture;

            Start();
        }

        protected void WaitForRows (IFuture resumeFuture) {
            _ResumeFuture = resumeFuture;
        }

        public static TaskEnumerator<T> FromEnumerable (IEnumerable<T> enumerable) {
            return FromEnumerable(enumerable, DefaultBufferSize);
        }

        public static TaskEnumerator<T> FromEnumerable (IEnumerable enumerable) {
            return FromEnumerable(enumerable, DefaultBufferSize);
        }

        public static TaskEnumerator<T> FromEnumerable (IEnumerable<T> enumerable, int capacity) {
            return FromEnumerator(enumerable.GetEnumerator(), capacity);
        }

        public static TaskEnumerator<T> FromEnumerable (IEnumerable enumerable, int capacity) {
            return FromEnumerator(enumerable.GetEnumerator(), capacity);
        }

        public static TaskEnumerator<T> FromEnumerator (IEnumerator<T> enumerator, int capacity) {
            return new TaskEnumerator<T>(
                EnumeratorExtensionMethods.EnumerateViaThreadpool(enumerator, capacity), capacity
            );
        }

        public static TaskEnumerator<T> FromEnumerator (IEnumerator enumerator, int capacity) {
            return new TaskEnumerator<T>(
                EnumeratorExtensionMethods.EnumerateViaThreadpool<object>(Wrap(enumerator), capacity), capacity
            );
        }

        static IEnumerator<object> Wrap (IEnumerator inner) {
            using (new Squared.Task.EnumeratorExtensionMethods.Disposer(inner))
            while (inner.MoveNext())
                yield return inner.Current;
        }

        /// <summary>
        /// Fetches more items from the source sequence, if possible.
        /// </summary>
        public FetchThunk Fetch () {
            if (Disposed || (_Initialized && _SequenceFuture.Completed))
                return null;
            else {
                lock (_Buffer)
                    _Buffer.Clear();

                return _FetchThunk;
            }
        }

        protected void Fail (Exception error) {
            OnEarlyDispose = null;

            if (_ResumeFuture != null) {
                _ResumeFuture.Fail(error);
                _ResumeFuture = null;
            }

            if ((_SequenceFuture != null) && (!_SequenceFuture.Completed))
                _SequenceFuture.Fail(error);

            Dispose();
        }

        internal Exception TryConvertValue (object value, out T result) {
            try {
                result = (T)value;
                return null;
            } catch (InvalidCastException) {
                result = default(T);

                string valueString;
                try {
                    valueString = value.ToString();
                } catch {
                    valueString = "<?>";
                }

                string errorString = String.Format(
                    "Unable to convert value '{0}' from {1} to {2}",
                    valueString, value.GetType().Name, typeof(T).Name
                );

                return new InvalidCastException(errorString);
            }
        }

        internal IFuture OnNextValue (object value) {
            OnEarlyDispose = null;

            int count;

            var seq = value as IEnumerable<T>;
            if (seq != null) {
                lock (_Buffer) {
                    _Buffer.AddRange(seq);
                    count = _Buffer.Count;
                }
            } else {
                T convertedValue;
                var e = TryConvertValue(value, out convertedValue);

                if (e != null) {
                    Fail(e);

                    var f = new SignalFuture();
                    f.Fail(e);
                    return f;
                } else {
                    lock (_Buffer) {
                        _Buffer.Add(convertedValue);
                        count = _Buffer.Count;
                    }
                }
            }

            if (count >= Capacity) {
                Resume();
                _ReadyForMoreFuture = new SignalFuture();
                return _ReadyForMoreFuture;
            } else {
                return null;
            }
        }

        IEnumerator IEnumerable.GetEnumerator () {
            return new Enumerator(this, _Buffer.GetEnumerator());
        }

        IEnumerator<T> IEnumerable<T>.GetEnumerator () {
            return new Enumerator(this, _Buffer.GetEnumerator());
        }

        public IEnumerator<T> CurrentItems {
            get {
                return new Enumerator(this, _Buffer.GetEnumerator());
            }
        }

        internal void ReadyForMore () {
            if (_SequenceFuture.Completed) {
                Dispose();
                return;
            }

            lock (_Buffer)
                if (_Buffer.Count == 0)
                    Dispose();
                else
                    _Buffer.Clear();

            if (_ReadyForMoreFuture != null) {
                _ReadyForMoreFuture.Complete();
                _ReadyForMoreFuture = null;
            }
        }

        protected void Resume () {
            OnEarlyDispose = null;

            if (_ResumeFuture != null) {
                int count;
                lock (_Buffer)
                    count = _Buffer.Count;

                _ResumeFuture.SetResult((count > 0), null);
                _ResumeFuture = null;

                if (count == 0)
                    Dispose();
            } else {
                Dispose();
            }
        }

        public IEnumerator<object> GetArray () {
            var temp = new List<T>(Capacity);

            while (!Disposed) {
                yield return this.Fetch();

                temp.AddRange(_Buffer);

                new Enumerator(this, _Buffer.GetEnumerator()).Dispose();
            }

            yield return new Result(temp.ToArray());
        }

        public bool Disposed {
            get {
                return (_Task == null);
            }
        }

        public void Dispose () {
            if (OnEarlyDispose != null) {
                OnEarlyDispose();
                OnEarlyDispose = null;
            }

            if (_Task != null) {
                _Task.Dispose();
            }

            if (_SequenceFuture != null) {
                if (!_SequenceFuture.Completed)
                    _SequenceFuture.Dispose();
            }

            if (_Thunk != null) {
                _Thunk.Dispose();
                _Thunk = null;
            }

            _Task = null;
            _Scheduler = null;
        }
    }

    public class EventSink<T> : BlockingQueue<T>, IDisposable
        where T : EventArgs {

        public event Action OnDispose;

        public void OnEvent (object sender, T eventArgs) {
            this.Enqueue(eventArgs);
        }

        public static implicit operator EventHandler<T> (EventSink<T> sink) {
            return sink.OnEvent;
        }

        public void Dispose () {
            if (OnDispose != null)
                OnDispose();
        }
    }

    public class Signal<T> : IDisposable {
        private Future<T> _Current = new Future<T>();

        public bool Set (T value, Exception exception) {
            Future<T> currentFuture;
            lock (this)
                currentFuture = Interlocked.Exchange(ref _Current, new Future<T>());

            if (currentFuture == null)
                throw new ObjectDisposedException("Signal");

            try {
                currentFuture.SetResult(value, null);
                return true;
            } catch {
                return false;
            }
        }

        public Future<T> Wait () {
            lock (this)
            if (_Current != null)
                return _Current;
            else
                throw new ObjectDisposedException("Signal");
        }

        public void Dispose () {
            lock (this)
            if (_Current != null) {
                _Current.Dispose();
                _Current = null;
            }
        }
    }

    public class Signal : Signal<NoneType> {
        public bool Set () {
            return base.Set(NoneType.None, null);
        }
    }

    public static class TaskEventSubscriber {
        public static EventSubscriber New (TaskScheduler scheduler, Func<IEventInfo, IEnumerator<object>> task) {
            return (e) =>
                scheduler.Start(task(e), TaskExecutionPolicy.RunAsBackgroundTask);
        }

        public static TypedEventSubscriber<T> New<T> (TaskScheduler scheduler, Func<IEventInfo<T>, T, IEnumerator<object>> task)
            where T : class {
            return (e, args) =>
                scheduler.Start(task(e, args), TaskExecutionPolicy.RunAsBackgroundTask);
        }

        public static EventSubscription Subscribe (this EventBus eventBus, object source, string type, TaskScheduler scheduler, Func<IEventInfo, IEnumerator<object>> task) {
            return eventBus.Subscribe(source, type, TaskEventSubscriber.New(scheduler, task));
        }

        public static EventSubscription Subscribe<T> (this EventBus eventBus, object source, string type, TaskScheduler scheduler, Func<IEventInfo<T>, T, IEnumerator<object>> task)
            where T : class {
            return eventBus.Subscribe<T>(source, type, TaskEventSubscriber.New<T>(scheduler, task));
        }
    }

    public class OwnedFutureSet : IEnumerable<IFuture>, IDisposable {
        private sealed class FutureComparer : IEqualityComparer<IFuture> {
            bool IEqualityComparer<IFuture>.Equals (IFuture x, IFuture y) {
                return x == y;
            }

            int IEqualityComparer<IFuture>.GetHashCode (IFuture obj) {
                return obj.GetHashCode();
            }
        }

        private HashSet<IFuture> _OwnedFutures = new HashSet<IFuture>(new FutureComparer());
        private OnFutureResolved _OnComplete;
        private OnFutureResolved _OnDispose;

        public OwnedFutureSet () {
            _OnComplete = OnComplete;
            _OnDispose = OnDispose;
        }

        private void OnComplete (IFuture f) {
            if (_OwnedFutures != null)
                _OwnedFutures.Remove(f);
        }

        private void OnDispose (IFuture f) {
            if (_OwnedFutures != null)
                _OwnedFutures.Remove(f);
        }

        public void Add (IFuture future) {
            var of = _OwnedFutures;
            if (of == null)
                return;

            of.Add(future);
            future.RegisterHandlers(_OnComplete, _OnDispose);
        }

        public int Count {
            get {
                return _OwnedFutures.Count;
            }
        }

        public void Dispose () {
            var of = Interlocked.Exchange(ref _OwnedFutures, null);

            if (of == null)
                return;

            foreach (var f in of)
                f.Dispose();

            of.Clear();
        }

        IEnumerator<IFuture> IEnumerable<IFuture>.GetEnumerator () {
            return _OwnedFutures.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator () {
            return _OwnedFutures.GetEnumerator();
        }
    }
    
    public static class FutureExtensionMethods {
        public static WaitWithTimeout WaitWithTimeout (this IFuture future, double timeout) {
            return new WaitWithTimeout(future, timeout);
        }
    }
}
