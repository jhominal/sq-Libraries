﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.Remoting.Messaging;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Squared.Util;

namespace Squared.Threading {
    /// <summary>
    /// Responds to one or more work items being processed by the current thread.
    /// </summary>
    public delegate void WorkQueueDrainListener (int itemsRunByThisThread, bool moreWorkRemains);

    public interface IWorkItemQueueTarget {
        /// <summary>Adds a work item to the end of the job queue for the current step.</summary>
        void QueueWorkItem (Action item);
    }

    public static class WorkItemQueueTarget {
        private static IWorkItemQueueTarget Default = null;

        public static IWorkItemQueueTarget Current {
            get {
                var result = (IWorkItemQueueTarget)CallContext.LogicalGetData("TaskScheduler");
                return result ?? Default;
            }
            set {
                CallContext.LogicalSetData("WorkItemQueueTarget", value);
            }
        }

        public static void SetDefaultIfNone (IWorkItemQueueTarget @default) {
            Interlocked.CompareExchange(ref Default, @default, null);
        }
    }

    public interface IWorkQueue {
        /// <param name="exhausted">Is set to true if the Step operation caused the queue to become empty.</param>
        /// <returns>The number of work items handled.</returns>
        int Step (out bool exhausted, int? maximumCount = null);
        /// <summary>
        /// Registers a listener to be invoked any time a group of work items is processed by a thread.
        /// </summary>
        void RegisterDrainListener (WorkQueueDrainListener listener);
        void AssertEmpty ();
        bool IsEmpty { get; }
        int Priority { get; }
    }

    public class WorkItemConfiguration {
        /// <summary>
        /// Work items with higher priority values have their queues stepped before queues
        ///  with lower priority
        /// </summary>
        public int Priority = 0;

        public int? MaxStepCount = null;

        /// <summary>
        /// Configures the number of steps taken each time this queue is visited by a worker thread.
        /// Low values increase the overhead of individual work items.
        /// High values reduce the overhead of work items but increase the odds that all worker threads can get bogged down
        ///  by a single queue.
        /// </summary>
        public int DefaultStepCount = 64;

        public int StochasticNotifyInterval = 32;

        /// <summary>
        /// Configures the maximum number of items that can be processed at once. If a single work
        ///  item is likely to block a thread for a long period of time, you should make this value small.
        /// </summary>
        public int MaxConcurrency = 8;
        /// <summary>
        /// Configures the maximum number of items that can be processed at once. The total number of 
        ///  threads is reduced by this much to compute a limit.
        /// </summary>
        public int ConcurrencyPadding = 1;
    }

    // This job must be run on the main thread
    public interface IMainThreadWorkItem : IWorkItem {
    }

    public interface IWorkItem {
        void Execute ();
    }

    public delegate void OnWorkItemComplete<T> (ref T item)
        where T : IWorkItem;

    internal struct InternalWorkItem<T>
        where T : IWorkItem
    {
        public WorkQueue<T>          Queue;
        public OnWorkItemComplete<T> OnComplete;
        public T                     Data;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal InternalWorkItem (WorkQueue<T> queue, ref T data, OnWorkItemComplete<T> onComplete) {
            Queue = queue;
            Data = data;
            OnComplete = onComplete;
        }
    }

    public enum WorkQueueNotifyMode : int {
        Never = 0,
        Always = 1,
        Stochastically = 2
    }

    public class WorkQueue<T> : IWorkQueue
        where T : IWorkItem 
    {
        // For debugging
        public static bool BlockEnqueuesWhileDraining = false;

        // For debugging
        internal bool IsMainThreadQueue = false;

        private volatile int ItemsQueued = 0, ItemsProcessed = 0;
        private volatile int HasAnyListeners = 0;
        private readonly List<WorkQueueDrainListener> DrainListeners = new List<WorkQueueDrainListener>();
        private volatile int NumWaitingForDrain = 0;
        private readonly ManualResetEventSlim DrainedSignal = new ManualResetEventSlim(false);

        private readonly object ItemsLock = new object();
        private readonly Queue<InternalWorkItem<T>> Items = new Queue<InternalWorkItem<T>>();

        private int NumProcessing = 0;
        private ExceptionDispatchInfo UnhandledException;
        private readonly bool IsMainThreadWorkItem;

        public readonly WorkItemConfiguration Configuration;
        public readonly ThreadGroup Owner;

        public WorkQueue (ThreadGroup owner) {
            Owner = owner;
            IsMainThreadWorkItem = typeof(IMainThreadWorkItem).IsAssignableFrom(typeof(T));
            Configuration = (WorkItemConfiguration)typeof(T).GetProperty("Configuration", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(null)
                ?? new WorkItemConfiguration();
        }

        private void NotifyDrained () {
            // FIXME: Should we do this first? Assumption is that in a very bad case, the future's
            //  complete handler might begin waiting
            DrainedSignal.Set();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void AssertCanEnqueue () {
            if (BlockEnqueuesWhileDraining && (NumWaitingForDrain > 0))
                throw new Exception("Cannot enqueue items while the queue is draining");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int AddInternal (ref InternalWorkItem<T> item) {
            AssertCanEnqueue();
            var result = Interlocked.Increment(ref ItemsQueued);
            lock (ItemsLock)
                Items.Enqueue(item);
            return result;
        }

        public void RegisterDrainListener (WorkQueueDrainListener listener) {
            if (listener == null)
                throw new ArgumentNullException("listener");

            // FIXME: This will deadlock if we are currently draining... don't do that

            HasAnyListeners = 1;
            lock (DrainListeners)
                DrainListeners.Add(listener);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void NotifyChanged () {
            Owner.NotifyQueuesChanged();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void NotifyChanged (WorkQueueNotifyMode mode, int newCount) {
            switch (mode) {
                case WorkQueueNotifyMode.Never:
                    return;
                default:
                case WorkQueueNotifyMode.Always:
                    Owner.NotifyQueuesChanged();
                    return;
                case WorkQueueNotifyMode.Stochastically:
                    if ((newCount % Configuration.StochasticNotifyInterval) == 0)
                        Owner.NotifyQueuesChanged();
                    return;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Enqueue (T data, bool notifyChanged) {
            Enqueue(ref data, null, notifyChanged ? WorkQueueNotifyMode.Always : WorkQueueNotifyMode.Never);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Enqueue (ref T data, bool notifyChanged) {
            Enqueue(ref data, null, notifyChanged ? WorkQueueNotifyMode.Always : WorkQueueNotifyMode.Never);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Enqueue (T data, OnWorkItemComplete<T> onComplete, bool notifyChanged) {
            Enqueue(ref data, onComplete, notifyChanged ? WorkQueueNotifyMode.Always : WorkQueueNotifyMode.Never);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Enqueue (ref T data, OnWorkItemComplete<T> onComplete, bool notifyChanged) {
            Enqueue(ref data, onComplete, notifyChanged ? WorkQueueNotifyMode.Always : WorkQueueNotifyMode.Never);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Enqueue (T data, OnWorkItemComplete<T> onComplete = null, WorkQueueNotifyMode notifyChanged = WorkQueueNotifyMode.Always) {
#if DEBUG
            if (IsMainThreadWorkItem && !IsMainThreadQueue)
                throw new InvalidOperationException("This work item must be queued on the main thread");
#endif

            var wi = new InternalWorkItem<T>(this, ref data, onComplete);
            var newCount = AddInternal(ref wi);
            NotifyChanged(notifyChanged, newCount);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Enqueue (ref T data, OnWorkItemComplete<T> onComplete = null, WorkQueueNotifyMode notifyChanged = WorkQueueNotifyMode.Always) {
#if DEBUG
            if (IsMainThreadWorkItem && !IsMainThreadQueue)
                throw new InvalidOperationException("This work item must be queued on the main thread");
#endif

            var wi = new InternalWorkItem<T>(this, ref data, onComplete);
            var newCount = AddInternal(ref wi);
            NotifyChanged(notifyChanged, newCount);
        }

        public void EnqueueMany (ArraySegment<T> data, bool notifyChanged = true) {
#if DEBUG
            if (IsMainThreadWorkItem && !IsMainThreadQueue)
                throw new InvalidOperationException("This work item must be queued on the main thread");
#endif

            lock (ItemsLock) {
                AssertCanEnqueue();
                Interlocked.Add(ref ItemsQueued, data.Count);

                var wi = new InternalWorkItem<T>();
                wi.Queue = this;
                wi.OnComplete = null;
                for (var i = 0; i < data.Count; i++) {
                    wi.Data = data.Array[data.Offset + i];
                    Items.Enqueue(wi);
                }
            }

            if (notifyChanged)
                NotifyChanged();
        }

        private void StepInternal (ref int result, out bool exhausted, int actualMaximumCount) {
            InternalWorkItem<T> item = default(InternalWorkItem<T>);
            int count = 0, numProcessed = 0;
            bool running = true, inLock = false, signalDrained = false, setProcessingFlag = false;

            var padded = Owner.Count - Configuration.ConcurrencyPadding;
            var lesser = Math.Min(Configuration.MaxConcurrency, padded);
            var maxConcurrency = Math.Max(lesser, 1);

            if (Interlocked.Increment(ref NumProcessing) > maxConcurrency) {
                Interlocked.Decrement(ref NumProcessing);
                exhausted = false;
                return;
            }

            do {
                try {
                    // FIXME: Find a way to not acquire this every step
                    if (!inLock) {
                        Monitor.Enter(ItemsLock);
                        inLock = true;
                    }

                    running = ((count = Items.Count) > 0) &&
                        (actualMaximumCount > 0);

                    if (running) {
                        item = Items.Dequeue();
                        if (Items.Count == 0)
                            signalDrained = true;
                    } else if (count == 0) {
                        signalDrained = true;
                    }

                    if (inLock) {
                        Monitor.Exit(ItemsLock);
                        inLock = false;
                    }

                    if (running) {
                        try {
                            numProcessed++;
                            item.Data.Execute();
                            if (item.OnComplete != null)
                                item.OnComplete(ref item.Data);

                            result++;
                        } finally {
                            Interlocked.Increment(ref ItemsProcessed);
                        }
                    }
                } catch (Exception exc) {
                    UnhandledException = ExceptionDispatchInfo.Capture(exc);
                    signalDrained = true;
                    break;
                }
            } while (running);

            actualMaximumCount -= numProcessed;
            Interlocked.Decrement(ref NumProcessing);

            if (!inLock) {
                Monitor.Enter(ItemsLock);
                inLock = true;
            }

            exhausted = Items.Count <= 0;

            if (inLock)
                Monitor.Exit(ItemsLock);

            if (signalDrained)
                NotifyDrained();
        }

        public int Step (out bool exhausted, int? maximumCount = null) {
            int result = 0;
            int actualMaximumCount = Math.Min(
                maximumCount ?? Configuration.DefaultStepCount, 
                Configuration.MaxStepCount ?? Configuration.DefaultStepCount
            );
            exhausted = true;

            StepInternal(ref result, out exhausted, actualMaximumCount);

            if ((result > 0) && (HasAnyListeners != 0)) {
                lock (DrainListeners)
                    foreach (var listener in DrainListeners)
                        listener(result, !exhausted);
            }

            return result;
        }

        int IWorkQueue.Priority => Configuration.Priority;

        public bool IsEmpty {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get {
                lock (ItemsLock)
                    return (Items.Count == 0) && (ItemsProcessed >= ItemsQueued);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AssertEmpty () {
            if (IsEmpty)
                return;
#if DEBUG
            throw new Exception("Queue is not fully drained");
#else
            Console.Error.WriteLine("WorkQueue of type {0} was not fully drained", typeof(T).FullName);
#endif
        }

        public bool WaitUntilDrained (int timeoutMs = -1) {
            if (IsEmpty)
                return true;

            var resultCount = Interlocked.Increment(ref NumWaitingForDrain);
            long endWhen =
                timeoutMs >= 0
                    ? Time.Ticks + (Time.MillisecondInTicks * timeoutMs)
                    : long.MaxValue - 1;

            bool doWait = false;
            var waterMark = ItemsQueued;
            try {
                do {
                    lock (ItemsLock)
                        doWait = (Items.Count > 0) || (ItemsProcessed < ItemsQueued);
                    if (doWait) {
                        var now = Time.Ticks;
                        if (now > endWhen)
                            break;
                        var maxWait = (timeoutMs <= 0)
                            ? timeoutMs
                            : (int)(Math.Max(0, endWhen - now) / Time.MillisecondInTicks);
                        NotifyChanged();
                        if (!DrainedSignal.Wait(maxWait))
                            ;
                        else
                            ;
                    } else {
                        if (!IsEmpty)
                            throw new Exception();
                        Thread.Yield();
                        if (!IsEmpty)
                            throw new Exception();
#if DEBUG
                        if (ItemsProcessed < waterMark)
                            throw new Exception("AssertDrained returned before reaching watermark");
#endif
                        return (ItemsProcessed >= waterMark);
                    }
                } while (true);
            } finally {
                Interlocked.Decrement(ref NumWaitingForDrain);

                var uhe = Interlocked.Exchange(ref UnhandledException, null);
                if (uhe != null)
                    uhe.Throw();
            }

            return false;
        }
    }
}
