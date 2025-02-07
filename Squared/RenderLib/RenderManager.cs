﻿#pragma warning disable 0420 // a reference to a volatile field will not be treated as volatile

// Uncomment this to make Frame.Add faster in debug builds
#define PARANOID

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Squared.Util;
using System.Threading;
using Microsoft.Xna.Framework.Graphics;
using Squared.Game;
using Microsoft.Xna.Framework;
using Squared.Render.Internal;
using Squared.Threading;
using System.Collections;
using System.Runtime.CompilerServices;

namespace Squared.Render {
    public static class RenderExtensionMethods {
        public static int ComputePrimitiveCount (this PrimitiveType primitiveType, int vertexCount) {
            switch (primitiveType) {
                case PrimitiveType.LineStrip:
                    return vertexCount - 1;
                case PrimitiveType.LineList:
                    return vertexCount / 2;
                case PrimitiveType.TriangleStrip:
                    return vertexCount - 2;
                case PrimitiveType.TriangleList:
                    return vertexCount / 3;
                default:
                    throw new ArgumentException();
            }
        }

        public static int ComputeVertexCount (this PrimitiveType primitiveType, int primitiveCount) {
            switch (primitiveType) {
                case PrimitiveType.LineStrip:
                    return primitiveCount + 1;
                case PrimitiveType.LineList:
                    return primitiveCount * 2;
                case PrimitiveType.TriangleStrip:
                    return primitiveCount + 2;
                case PrimitiveType.TriangleList:
                    return primitiveCount * 3;
                default:
                    throw new ArgumentException();
            }
        }
    }

    public interface IBatchContainer {
        void Add (Batch batch);
        Frame Frame { get; }
        RenderCoordinator Coordinator { get; }
        RenderManager RenderManager { get; }
        void PrepareChildren (ref Batch.PrepareContext context);
        bool IsEmpty { get; }
        bool IsReleased { get; }
    }

    public sealed class DeviceManager : IDisposable {
        /// <summary>
        /// Set this to true if you insist on using Device.SetRenderTarget(s) 
        ///  instead of using DeviceManager's methods
        /// </summary>
        public static bool ParanoidRenderTargetChecks = false;

        /// <summary>
        /// Set this to true if you wish to record a stack trace on render target transitions.
        /// </summary>
        public static bool CaptureRenderTargetTransitionStacks = false;

        internal int RenderTargetChangeIndex = 0;
        internal RenderManager RenderManager;

        private struct RenderTargetStackEntry {
            public static readonly RenderTargetStackEntry None = new RenderTargetStackEntry((RenderTarget2D)null);

            public readonly RenderTargetBinding[] Multiple;
            public readonly RenderTarget2D Single;
            public readonly StackTrace StackTrace;
            public readonly RenderTargetBatchGroup Batch;

            public RenderTarget2D First {
                get {
                    return (Multiple != null)
                        ? (RenderTarget2D)Multiple[0].RenderTarget
                        : Single;
                }
            }

            public RenderTargetStackEntry (RenderTarget2D target, RenderTargetBatchGroup batch = null, StackTrace stackTrace = null) {
                Multiple = null;
                Single = target;
                if (CaptureRenderTargetTransitionStacks)
                    StackTrace = stackTrace ?? new StackTrace(1, true);
                else
                    StackTrace = stackTrace;
                Batch = batch;
            }

            public RenderTargetStackEntry (RenderTargetBinding[] bindings, RenderTargetBatchGroup batch = null, StackTrace stackTrace = null) {
                Multiple = bindings;
                Single = null;
                if (CaptureRenderTargetTransitionStacks)
                    StackTrace = stackTrace ?? new StackTrace(1, true);
                else
                    StackTrace = stackTrace;
                Batch = batch;
            }

            public int Count {
                get {
                    return Math.Max(
                        (Multiple != null) ? Multiple.Length : 0,
                        (Single != null) ? 1 : 0
                    );
                }
            }

            public override int GetHashCode () {
                if (Multiple != null)
                    return Multiple.GetHashCode();
                else if (Single != null)
                    return Single.GetHashCode();
                else
                    return -1;
            }

            public bool Equals (RenderTargetStackEntry entry) {
                if (entry.Count != Count)
                    return false;

                if (Count == 1)
                    return object.ReferenceEquals(entry.First, First);
                else
                    return object.ReferenceEquals(entry.Single, Single) && 
                        object.ReferenceEquals(entry.Multiple, Multiple);
            }

            public override bool Equals (object obj) {
                if (obj is RenderTargetStackEntry)
                    return Equals((RenderTargetStackEntry)obj);
                else
                    return false;
            }
        }

        public static class ActiveMaterial {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void Set (DeviceManager deviceManager, Material material) {
                try {
                    if (deviceManager.CurrentMaterial != material) {
                        Set_Slow(deviceManager, material);
                    } else {
                        material.Flush(deviceManager);
                    }
                } catch (Exception exc) {
                    Debug.WriteLine($"Exception setting material '{material.Name}', technique: '{material.Effect?.CurrentTechnique?.Name ?? material.Effect?.Name}' {exc}");
                    throw;
                }
            }

            private static void Set_Slow (DeviceManager deviceManager, Material material) {
                if (deviceManager.CurrentMaterial != null)
                    deviceManager.CurrentMaterial.End(deviceManager);
                
                deviceManager.CurrentMaterial = material;
                deviceManager.UpdateTargetInfo(null, false, true);
            
                if (material != null)
                    material.Begin(deviceManager);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void Set (DeviceManager deviceManager, Material material, ref MaterialParameterValues parameters) {
                if (deviceManager.CurrentMaterial != material) {
                    Set_Slow(deviceManager, material, ref parameters);
                } else {
                    material.Flush(deviceManager, ref parameters);
                }
            }

            private static void Set_Slow (DeviceManager deviceManager, Material material, ref MaterialParameterValues parameters) {
                if (deviceManager.CurrentMaterial != null)
                    deviceManager.CurrentMaterial.End(deviceManager);
                
                deviceManager.CurrentMaterial = material;
                deviceManager.UpdateTargetInfo(null, false, true);
            
                if (material != null)
                    material.Begin(deviceManager, ref parameters);
            }
        }

        private readonly Stack<BlendState>             BlendStateStack = new Stack<BlendState>(256);
        private readonly Stack<DepthStencilState>      DepthStencilStateStack = new Stack<DepthStencilState>(256);
        private readonly Stack<RasterizerState>        RasterizerStateStack = new Stack<RasterizerState>(256);
        private readonly Stack<RenderTargetStackEntry> RenderTargetStack = new Stack<RenderTargetStackEntry>(256);
        private readonly Stack<Viewport>               ViewportStack = new Stack<Viewport>(256);

        internal readonly Stack<BatchGroup> BatchGroupStack = new Stack<BatchGroup>(256);

        public readonly GraphicsDevice Device;
        // HACK: This is gross
        public ActiveViewTransformInfo ActiveViewTransform { get; internal set; }
        public Material CurrentMaterial { get; private set; }
        public int FrameIndex { get; internal set; }

        private static volatile int NextDeviceId;
        public int DeviceId { get; private set; }

        private RenderTargetStackEntry CachedCurrentRenderTarget;
        private bool _IsDisposed;

        private bool? _NeedsYFlip;

        public bool NeedsYFlip {
            get {
                if (!_NeedsYFlip.HasValue) {
                    var asm = typeof(GraphicsDevice).Assembly;
                    if (!asm.FullName.Contains("FNA")) {
                        _NeedsYFlip = false;
                    } else {
                        var v = asm.GetName().Version;
                        if ((v.Major >= 20) || ((v.Major == 19) && (v.Minor >= 08)))
                            _NeedsYFlip = false;
                        else
                            _NeedsYFlip = true;
                    }
                }

                return _NeedsYFlip.Value;
            }
        }

        public bool IsDisposed {
            get {
                if (!_IsDisposed && Device.IsDisposed)
                    _IsDisposed = true;
                return _IsDisposed;
            }
        }

        public void Dispose () {
            if (IsDisposed)
                return;

            _IsDisposed = true;
            // FIXME: Dispose device? Probably not
        }

        public DeviceManager (RenderManager renderManager, GraphicsDevice device) {
            RenderManager = renderManager;
            Device = device;
            DeviceId = ++NextDeviceId;
        }

        internal void UpdateTargetInfo (RenderTarget2D target, bool knownTarget, bool setParams) {
            if (knownTarget) {
                // HACK: Don't reconstruct to avoid destroying an existing stack trace
                if (CachedCurrentRenderTarget.First != target)
                    CachedCurrentRenderTarget = new RenderTargetStackEntry(target);
            } else if (ParanoidRenderTargetChecks) {
                var rts = Device.GetRenderTargets();
                if ((rts == null) || (rts.Length == 0)) {
                    CachedCurrentRenderTarget = RenderTargetStackEntry.None;
                } else {
                    CachedCurrentRenderTarget = new RenderTargetStackEntry(rts, CachedCurrentRenderTarget.Batch, CachedCurrentRenderTarget.StackTrace);
                }
            }

            var target1 = CachedCurrentRenderTarget.First;
            var targetWidth = target1 != null
                ? target1.Width
                : Device.PresentationParameters.BackBufferWidth;
            var targetHeight = target1 != null
                ? target1.Height
                : Device.PresentationParameters.BackBufferHeight;

            /*
             * Some sources claim in GL it's based on the viewport, but it is not on geforce at least
            var targetWidth = Device.Viewport.Width;
            var targetHeight = Device.Viewport.Height;
            */

            var material = CurrentMaterial;
            if (material == null)
                return;

            var rtd = material.Parameters?.RenderTargetDimensions;

            if (rtd == null)
                return;

            if (!setParams)
                return;

            if (NeedsYFlip)
                if (CachedCurrentRenderTarget.First == null)
                    targetHeight *= -1;

            rtd.SetValue(new Vector2(targetWidth, targetHeight));

            // material.Flush();
        }

        public void PushStates () {
            BlendStateStack.Push(Device.BlendState);
            DepthStencilStateStack.Push(Device.DepthStencilState);
            RasterizerStateStack.Push(Device.RasterizerState);
        }

        public void PopStates () {
            Device.RasterizerState = RasterizerStateStack.Pop();
            Device.DepthStencilState = DepthStencilStateStack.Pop();
            Device.BlendState = BlendStateStack.Pop();
        }

        public void SetViewport (Viewport viewport) {
            Device.Viewport = viewport;
            // UpdateTargetInfo(null, false, true);
        }

        public void SetViewport (Rectangle viewport) {
            Device.Viewport = new Viewport(viewport);
            // UpdateTargetInfo(null, false, true);
        }

        private void ResetDeviceState (RenderTarget2D firstRenderTarget, bool setParams) {
            RenderManager.ResetDeviceState(Device);
            int w, h;
            if (firstRenderTarget != null) {
                w = firstRenderTarget.Width;
                h = firstRenderTarget.Height;
            } else {
                w = Device.PresentationParameters.BackBufferWidth;
                h = Device.PresentationParameters.BackBufferHeight;
            }
            Device.Viewport = new Viewport(0, 0, w, h);
            Device.ScissorRectangle = new Rectangle(0, 0, w, h);
            UpdateTargetInfo(firstRenderTarget, true, setParams);
        }

        public void SetRenderTarget (RenderTarget2D newRenderTarget, bool setParams = true, bool isPushPop = false) {
            unchecked { RenderTargetChangeIndex++; }
            Device.SetRenderTarget(newRenderTarget);
            CachedCurrentRenderTarget = new RenderTargetStackEntry(newRenderTarget);
            ResetDeviceState(newRenderTarget, setParams);
        }

        private void SetRenderTargets (RenderTargetStackEntry entry, bool setParams = true, bool isPushPop = false) {
            unchecked { RenderTargetChangeIndex++; }
            CachedCurrentRenderTarget = entry;
            if (entry.Count <= 1) {
                Device.SetRenderTarget(entry.First);
                CachedCurrentRenderTarget = entry;
            } else {
                Device.SetRenderTargets(entry.Multiple);
            }
            ResetDeviceState(entry.First, setParams);
        }

        public void SetRenderTargets (RenderTargetBinding[] newRenderTargets, bool setParams = true, bool isPushPop = false) {
            SetRenderTargets(new RenderTargetStackEntry(newRenderTargets), setParams, isPushPop);
        }

        public void AssertRenderTarget (RenderTarget2D renderTarget) {
#if DEBUG
            var currentRenderTargets = Device.GetRenderTargets();
            var cachedCurrentRenderTarget = CachedCurrentRenderTarget;
            var actualCurrentRenderTarget = new RenderTargetStackEntry(currentRenderTargets);
            if (!cachedCurrentRenderTarget.Equals(actualCurrentRenderTarget))
                throw new Exception("Mismatch between cached and actual render target(s)");

            foreach (var crt in currentRenderTargets)
                if (crt.RenderTarget == renderTarget)
                    return;
            throw new Exception("Render target was not bound.");
#endif
        }

        public void PushRenderTarget (RenderTarget2D newRenderTarget) {
            PushStates();
            RenderTargetStack.Push(CachedCurrentRenderTarget);
            ViewportStack.Push(Device.Viewport);

            SetRenderTarget(newRenderTarget, isPushPop: true);
        }

        public void PushRenderTargets (RenderTargetBinding[] newRenderTargets) {
            PushStates();
            RenderTargetStack.Push(CachedCurrentRenderTarget);
            ViewportStack.Push(Device.Viewport);

            SetRenderTargets(newRenderTargets, isPushPop: true);
        }

        public void PopRenderTarget () {
            PopStates();
            var newRenderTargets = RenderTargetStack.Pop();

            SetRenderTargets(newRenderTargets, isPushPop: true);

            Device.Viewport = ViewportStack.Pop();

            UpdateTargetInfo(newRenderTargets.First, true, false);
        }

        public MaterialEffectParameters CurrentParameters {
            get {
                return CurrentMaterial.Parameters;
            }
        }

        public RenderTarget2D CurrentRenderTarget {
            get {
                return CachedCurrentRenderTarget.First;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ApplyMaterial (Material material) {
            ActiveMaterial.Set(this, material);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ApplyMaterial (Material material, ref MaterialParameterValues parameters) {
            ActiveMaterial.Set(this, material, ref parameters);
        }

        public void Begin (bool changeRenderTargets) {
            if (CurrentMaterial != null)
                CurrentMaterial = null;

            RenderManager.ResetDeviceState(Device);
            if (changeRenderTargets)
                SetRenderTargets(null, isPushPop: true);
            else
                UpdateTargetInfo(null, false, true);

            Device.SetVertexBuffer(null);
        }

        public void Finish () {
            if (CurrentMaterial != null) {
                CurrentMaterial.End(this);
                CurrentMaterial = null;
            }

            RenderManager.ResetDeviceState(Device);

            SetRenderTargets(null, isPushPop: true);
            Device.SetVertexBuffer(null);

#if DEBUG
            int threshold = 2;
            if (BatchGroupStack.Count >= threshold)
                throw new Exception("Unbalanced batch group stack");
            if (RenderTargetStack.Count >= threshold)
                throw new Exception("Unbalanced render target stack");
            if (BlendStateStack.Count >= threshold)
                throw new Exception("Unbalanced blend state stack");
            if (RasterizerStateStack.Count >= threshold)
                throw new Exception("Unbalanced rasterizer state stack");
            if (DepthStencilStateStack.Count >= threshold)
                throw new Exception("Unbalanced depth/stencil state stack");
#endif

            BatchGroupStack.Clear();
            RenderTargetStack.Clear();
            BlendStateStack.Clear();
            RasterizerStateStack.Clear();
            DepthStencilStateStack.Clear();
        }

        public void DisposeResource (IDisposable resource) {
            RenderManager.DisposeResource(resource);
        }
    }

    // Thread-safe
    public sealed class RenderManager {
        public struct MemoryStatistics {
            public long ManagedVertexBytes, ManagedIndexBytes;
            public long UnmanagedVertexBytes, UnmanagedIndexBytes;
        }

        public readonly PrepareManager PrepareManager;
        public readonly ThreadGroup    ThreadGroup;
        public          DeviceManager  DeviceManager { get; private set; }
        public readonly Thread         MainThread;

        // FIXME: ????
        private volatile int _AllowCreatingNewGenerators = 1;

        private int _FrameCount = 0;
        private object _FrameLock = new object();
        private Frame _Frame;
        private bool _FrameInUse = false;

        private Frame _PreviousFrame, _ReusableFrame;

        internal long TotalVertexBytes, TotalIndexBytes;

        private readonly Dictionary<Type, IBufferGenerator> _AllBufferGenerators =
            new Dictionary<Type, IBufferGenerator>(new ReferenceComparer<Type>());

        private readonly Dictionary<Type, int> _PreferredPoolCapacities =
            new Dictionary<Type, int>(new ReferenceComparer<Type>());
        private readonly Dictionary<Type, IArrayPoolAllocator> _ArrayAllocators = 
            new Dictionary<Type, IArrayPoolAllocator>(new ReferenceComparer<Type>());
        private readonly Dictionary<Type, IBatchPool> _BatchAllocators =
            new Dictionary<Type, IBatchPool>(new ReferenceComparer<Type>());
        private readonly List<IDisposable> _PendingDisposes = new List<IDisposable>();

        /// <summary>
        /// You must acquire this lock before applying changes to the device, creating objects, or loading content.
        /// </summary>
        public readonly object CreateResourceLock = new object();
        /// <summary>
        /// You must acquire this lock before rendering or resetting the device.
        /// </summary>
        public readonly object UseResourceLock = new object();

        private readonly HashSet<IDrainable> RequiredDrainListPools = new HashSet<IDrainable>();
        private static readonly object ListPoolLock = new object();

        public event EventHandler<DeviceManager> DeviceChanged;

        private int IsDisposingResources;

        public RenderManager (GraphicsDevice device, Thread mainThread, ThreadGroup threadGroup) {
            if (mainThread == null)
                throw new ArgumentNullException("mainThread");

            MainThread = mainThread;
            DeviceManager = new DeviceManager(this, device);
            ThreadGroup = threadGroup;
            PrepareManager = new PrepareManager(ThreadGroup);

            CreateNewBufferGenerators();

            _Frame = null;
        }

        internal void CreateNewBufferGenerators () {
            lock (_AllBufferGenerators) {
                foreach (var kvp in _AllBufferGenerators) {
                    try {
                        kvp.Value.Dispose();
                    } catch (Exception) {
                    }
                }

                _AllBufferGenerators.Clear();
            }
        }

        internal void OnDeviceResetOrLost () {
            if (false)
            lock (_FrameLock) {
                _FrameInUse = false;
                // FIXME: Leak
                _Frame = null;
            }
        }

        internal void ChangeDevice (GraphicsDevice device) {
            OnDeviceResetOrLost();

            if (DeviceManager != null)
                DeviceManager.Dispose();
            DeviceManager = new DeviceManager(this, device);
            CreateNewBufferGenerators();
            if (DeviceChanged != null)
                DeviceChanged(this, DeviceManager);
        }

        public static readonly BlendState ResetBlendState = new BlendState {
            AlphaDestinationBlend = Blend.Zero,
            ColorDestinationBlend = Blend.Zero,
            Name = "ResetBlendState"
        };

        public static readonly SamplerState ResetSamplerState = new SamplerState {
            Filter = TextureFilter.Point,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            Name = "ResetSamplerState"
        };

        /// <summary>
        /// A device reset can leave a device in an intermediate state that will cause drawing operations to fail later.
        /// We address this by resetting pieces of device state to known-good values at the beginning of every frame,
        ///  and you can call this method to do so manually at any time.
        /// </summary>
        public static void ResetDeviceState (GraphicsDevice device) {
            const int numStages = 16;
            const int numVertexStages = 4;

            for (int i = 0; i < numStages; i++) {
#if FNA
                if (device.Textures[i] != null)
#endif
                device.Textures[i] = null;
                device.SamplerStates[i] = ResetSamplerState;
            }

            for (int i = 0; i < numVertexStages; i++) {
#if FNA
                if (device.VertexTextures[i] != null)
#endif
                device.VertexTextures[i] = null;
                device.VertexSamplerStates[i] = ResetSamplerState;
            }

            device.BlendState = ResetBlendState;
            device.DepthStencilState = DepthStencilState.None;
            device.RasterizerState = RasterizerState.CullNone;
        }

        public MemoryStatistics GetMemoryStatistics () {
            var result = new MemoryStatistics {
                UnmanagedVertexBytes = TotalVertexBytes,
                UnmanagedIndexBytes = TotalIndexBytes,
                ManagedVertexBytes = 0,
                ManagedIndexBytes = 0
            };

            lock (_AllBufferGenerators) {
                foreach (var kvp in _AllBufferGenerators) {
                    var generator = kvp.Value;
                    result.ManagedVertexBytes += generator.ManagedVertexBytes;
                    result.ManagedIndexBytes += generator.ManagedIndexBytes;
                }
            }

            return result;
        }

        internal void SynchronousPrepareBatches (Frame frame) {
            var context = new Batch.PrepareContext(PrepareManager, false);
            context.PrepareMany(ref frame.Batches);

            ThreadGroup.NotifyQueuesChanged();
            PrepareManager.AssertEmpty();
            frame.BatchesToRelease.AddRange(ref context.BatchesToRelease);
        }

        internal void ParallelPrepareBatches (Frame frame) {
            var context = new Batch.PrepareContext(PrepareManager, true);
            context.PrepareMany(ref frame.Batches);

            PrepareManager.Wait();
            PrepareManager.AssertEmpty();
            frame.BatchesToRelease.AddRange(ref context.BatchesToRelease);
        }

        internal int PickFrameIndex () {
            return Interlocked.Increment(ref _FrameCount);
        }

        internal void ReleaseFrame (Frame frame) {
            lock (_FrameLock) {
                if (_Frame == frame)
                    _FrameInUse = false;
                if (_PreviousFrame != frame) {
                    _ReusableFrame = _PreviousFrame;
                    _PreviousFrame = _Frame;
                }
            }
        }

        public Frame CreateFrame (RenderCoordinator coordinator) {
            lock (_FrameLock) {
                /*
                if (_FrameInUse)
                    throw new InvalidOperationException("A frame is already in use");

                _FrameInUse = true;
                CollectAllocators();
                */

                // UGH
                if (!_FrameInUse)
                    CollectAllocators();
                _FrameInUse = true;
                _Frame = _ReusableFrame ?? new Frame();
                _Frame.Initialize(coordinator, this, PickFrameIndex());
                return _Frame;
            }
        }

        private IDrainable[] CollectAllocatorsBuffer = new IDrainable[256];
        private Stopwatch CollectAllocatorsTimer = new Stopwatch();

        public void CollectAllocators () {
            lock (_ArrayAllocators)
                foreach (var allocator in _ArrayAllocators.Values)
                    allocator.Step();

            // Ensure that all the list pools used by the previous frame
            //  have finished their async clears. This ensures that we don't
            //  end up leaking lots of lists

            lock (ListPoolLock) {
                Array.Clear(CollectAllocatorsBuffer, 0, CollectAllocatorsBuffer.Length);
                // FIXME: Grow it
                RequiredDrainListPools.CopyTo(CollectAllocatorsBuffer, 0, Math.Min(RequiredDrainListPools.Count, CollectAllocatorsBuffer.Length));
            }

            const int minWaitTimeMs = 0, maxWaitTimeMs = 3;

            var elapsedTimeMs = 0.0;
            foreach (var lp in CollectAllocatorsBuffer) {
                if (lp == null)
                    break;
                var waitTime = Math.Max(minWaitTimeMs, (int)(maxWaitTimeMs - elapsedTimeMs));
                if (waitTime <= 0)
                    break;

                CollectAllocatorsTimer.Restart();
                lp.WaitForWorkItems(waitTime);
                CollectAllocatorsTimer.Stop();
                elapsedTimeMs += CollectAllocatorsTimer.Elapsed.TotalMilliseconds;
            }
            if (elapsedTimeMs >= 5)
                Debug.Write($"Collecting allocators took {elapsedTimeMs}ms\r\n");
        }

        public bool TrySetPoolCapacity<T> (int newCapacity)
            where T : Batch {

            var t = typeof(T);
            IBatchPool p;
            lock (_BatchAllocators) {
                _PreferredPoolCapacities[t] = newCapacity;

                if (_BatchAllocators.TryGetValue(t, out p)) {
                    p.SetCapacity(newCapacity);
                    return true;
                }
            }

            return false;
        }

        public T GetBufferGenerator<T> ()
            where T : IBufferGenerator {
            var t = typeof(T);

            IBufferGenerator result = null;
            lock (_AllBufferGenerators) {
                if (_AllBufferGenerators.TryGetValue(t, out result))
                    return (T)result;
            }

            if (_AllowCreatingNewGenerators != 1)
                throw new InvalidOperationException("Cannot create a buffer generator after the flush operation has occurred");

            result = (IBufferGenerator)Activator.CreateInstance(
                t, this
            );

            lock (_AllBufferGenerators) {
                if (_AllBufferGenerators.TryGetValue(t, out IBufferGenerator lostRace)) {
                    result.Dispose();
                    return (T)lostRace;
                } else
                    _AllBufferGenerators.Add(t, result);
            }

            return (T)result;
        }

        public ArrayPoolAllocator<T> GetArrayAllocator<T> () {
            var t = typeof(T);
            IArrayPoolAllocator o;
            lock (_ArrayAllocators)
                _ArrayAllocators.TryGetValue(t, out o);

            ArrayPoolAllocator<T> result;
            if (o == null) {
                result = new ArrayPoolAllocator<T>();
                lock (_ArrayAllocators) {
                    if (!_ArrayAllocators.TryGetValue(t, out o))
                        _ArrayAllocators[t] = result;
                    else
                        result = (ArrayPoolAllocator<T>)o;
                }
            } else {
                result = (ArrayPoolAllocator<T>)o;
            }

            return result;
        }

        public T AllocateBatch<T> () 
            where T : Batch, new() {

            var t = typeof(T);
            IBatchPool p;
            lock (_BatchAllocators)
                _BatchAllocators.TryGetValue(t, out p);

            BatchPool<T> allocator;
            if (p == null) {
                Func<IBatchPool, T> constructFn = (pool) =>
                    new T() {
                        Pool = pool
                    };

                allocator = new BatchPool<T>(constructFn);

                lock (_BatchAllocators) {
                    if (!_BatchAllocators.TryGetValue(t, out p))
                        _BatchAllocators[t] = allocator;
                    else
                        allocator = (BatchPool<T>)p;

                    int desiredCapacity;
                    if (_PreferredPoolCapacities.TryGetValue(t, out desiredCapacity))
                        allocator.SetCapacity(desiredCapacity);
                }
            } else {
                allocator = (BatchPool<T>)p;
            }

            return allocator.Allocate();
        }

        internal void AddDrainRequiredListPool (IDrainable listPool) {
            lock (ListPoolLock)
                RequiredDrainListPools.Add(listPool);
        }

        internal void ResetBufferGenerators (int frameIndex) {
            _AllowCreatingNewGenerators = 1;

            lock (_AllBufferGenerators)
            foreach (var kvp in _AllBufferGenerators)
                kvp.Value.Reset(frameIndex);
        }

        internal void FlushBufferGenerators (int frameIndex) {
            PrepareManager.Wait();

            _AllowCreatingNewGenerators = 0;

            lock (_AllBufferGenerators)
            foreach (var kvp in _AllBufferGenerators)
                kvp.Value.Flush(frameIndex);
        }

        internal void FlushPendingDisposes () {
            RenderCoordinator.FlushDisposeList(_PendingDisposes, ref IsDisposingResources);
        }

        public void DisposeResource (IDisposable resource) {
            if (resource == null)
                return;

            var tcd = resource as ITraceCapturingDisposable;
            tcd?.AutoCaptureTraceback();

            if (IsDisposingResources > 0) {
                resource.Dispose();
                return;
            }

            lock (_PendingDisposes)
                _PendingDisposes.Add(resource);
        }
    }

    public sealed class BatchComparer : IRefComparer<Batch>, IComparer<Batch> {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Compare (ref Batch x, ref Batch y) {
            if (x == null) {
                if (y == null)
                    return 0;
                else
                    return 1;
            } else if (y == null) {
                return -1;
            }

            int result = x.Layer.CompareTo(y.Layer);
            if (result == 0) {
                result = x.InternalSortOrdering.CompareTo(y.InternalSortOrdering);

                if (result == 0) {
                    int mx = 0, my = 0;

                    if (x.Material != null)
                        mx = x.Material.MaterialID;
                    if (y.Material != null)
                        my = y.Material.MaterialID;

                    result = mx.CompareTo(my);
                }
            }

            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Compare (Batch x, Batch y) {
            return Compare(ref x, ref y);
        }
    }

    public sealed class PrepareManager {
        public struct Task : IWorkItem {
            public static WorkItemConfiguration Configuration =>
                new WorkItemConfiguration {
                    Priority = 1,
                    ConcurrencyPadding = 0
                };

            public Batch Batch;
            public Batch.PrepareContext Context;

            public Task (Batch batch, ref Batch.PrepareContext context) {
                Batch = batch;
                Context = context;
            }

            internal static void Execute (Batch batch, ref Batch.PrepareContext context) {
                var isCombined = false;
                batch.GetState(out bool temp, out isCombined, out bool temp2, out bool temp3, out bool temp4);

                if (!isCombined)
                    batch.Prepare(context);

                batch.SetPrepareQueued(false);
            }

            public void Execute () {
                Execute(Batch, ref Context);
            }
        };

        public  readonly ThreadGroup         Group;
        private readonly WorkQueue<Task>     Queue;

        public PrepareManager (ThreadGroup threadGroup) {
            Group = threadGroup;
            Queue = threadGroup.GetQueueForType<Task>();
        }

        public void AssertEmpty () {
            Queue.AssertEmpty();
        }

        public void Wait () {
            Group.NotifyQueuesChanged();
            if (!Queue.WaitUntilDrained())
#if DEBUG
                throw new TimeoutException("Failed to drain the prepare queue");
#else
                ;
#endif
        }
        
        public void PrepareMany (ref DenseList<Batch> batches, ref Batch.PrepareContext context) {
            if (batches.Count < 2) {
                if (batches.Count == 1)
                    Prepare(batches[0], ref context);

                return;
            }

            var task = default(Task);
            task.Context = context;
            foreach (var b in batches) {
                ValidateBatch(b, true);

                if (b is IBatchContainer container)
                    container.PrepareChildren(ref context);

                task.Batch = b;
                if (context.Async)
                    Queue.Enqueue(ref task, false);
                else
                    task.Execute();
                task = new Task(b, ref context);
            }
        }

        internal void ValidateBatch (IBatch batch, bool enqueuing) {
            var b = (Batch)batch;
            b.GetState(out bool isInitialized, out bool isCombined, out bool isPrepareQueued, out bool isPrepared, out bool isIssued);
            Thread.MemoryBarrier();

            if (!isInitialized)
                throw new Exception("Uninitialized batch");
            /*
            else if (state.IsCombined)
                throw new Exception("Batch combined");
            */
            else if (isPrepared)
                throw new Exception("Batch already prepared");
            else if (isIssued)
                throw new Exception("Batch already issued");

            Thread.MemoryBarrier();
            if (enqueuing) {
                if (isPrepareQueued)
                    throw new Exception("Batch already queued for prepare");

                Thread.MemoryBarrier();

                b.SetPrepareQueued(true);
            }
        }

        public void Prepare (Batch batch, ref Batch.PrepareContext context) {
            if (batch == null)
                return;

            ValidateBatch(batch, true);
            if (batch is IBatchContainer container)
                container.PrepareChildren(ref context);

            var task = new Task(batch, ref context);

            if (context.Async) {
                Queue.Enqueue(task, false);
            } else {
                task.Execute();
            }
        }
    }

    public class BatchIssueFailedException : Exception {
        public readonly Batch Batch;

        public BatchIssueFailedException (Batch batch, Exception innerException) 
            : base (FormatMessage(batch, innerException), innerException) {

            Batch = batch;
        }

        public static string FormatMessage (Batch batch, Exception innerException) {
            if (batch.StackTrace != null)
                return String.Format(
                    "Failed to issue batch of type '{0}'. Stack trace at time of batch creation:{1}{2}", 
                    batch.GetType().Name,
                    Environment.NewLine, 
                    batch.StackTrace
                );
            else
                return String.Format(
                    "Failed to issue batch of type '{0}'. Set Batch.CaptureStackTraces to true to enable stack trace captures.",
                    batch.GetType().Name
                );
        }
    }

    public static class BatchExtensions {
        public static void IssueAndWrapExceptions (this Batch batch, DeviceManager manager) {
            if (manager.IsDisposed)
                return;

#if DEBUG
            if (batch.TimesIssued > 0)
                throw new InvalidOperationException("Batch was issued multiple times");

            if (Debugger.IsAttached) {
                batch.Issue(manager);
                batch.TimesIssued++;
            } else {
#endif

            batch.GetState(out bool isInitialized, out bool isCombined, out bool isPrepareQueued, out bool isPrepared, out bool temp1);
            if (!isInitialized)
                throw new BatchIssueFailedException(batch, new Exception("Batch not initialized"));
            else if (isCombined)
                // HACK
                return;
            else if (isPrepareQueued)
                throw new BatchIssueFailedException(batch, new Exception("Batch in prepare queue"));
            else if (!isPrepared)
                throw new BatchIssueFailedException(batch, new Exception("Batch not prepared"));

            try {
                batch.Issue(manager);
            } catch (Exception exc) {                
                throw new BatchIssueFailedException(batch, exc);
            } finally {
                batch.TimesIssued++;
            }
#if DEBUG
            }
#endif
        }
    }
}
