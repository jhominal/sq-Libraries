﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Squared.PRGUI.Controls;
using Squared.PRGUI.Decorations;
using Squared.PRGUI.Input;
using Squared.PRGUI.Layout;
using Squared.Render;
using Squared.Render.Convenience;
using Squared.Render.Text;
using Squared.Util;
using Squared.Util.Event;
using Squared.Util.Text;

namespace Squared.PRGUI {
    public sealed partial class UIContext : IDisposable {
        /// <summary>
        /// Configures the size of the rendering canvas
        /// </summary>
        public Vector2 CanvasSize;
        public RectF CanvasRect => new RectF(0, 0, CanvasSize.X, CanvasSize.Y);

        /// <summary>
        /// Control events are broadcast on this bus
        /// </summary>
        public readonly EventBus EventBus;

        /// <summary>
        /// The layout engine used to compute control sizes and positions
        /// </summary>
        public readonly LayoutContext Layout = new LayoutContext();

        public readonly NewEngine.LayoutEngine Engine;

        /// <summary>
        /// The top-level controls managed by the layout engine. Each one gets a separate rendering layer
        /// </summary>
        public ControlCollection Controls { get; private set; }

        internal void ClearKeyboardSelection () {
            SuppressAutoscrollDueToInputScroll = false;
            _KeyboardSelection = null;
            MousePositionWhenKeyboardSelectionWasLastUpdated = LastMousePosition;
        }

        internal void SetKeyboardSelection (Control control, bool forUser) {
            if (forUser) {
                if (control != null)
                    _PreferredTooltipSource = control;
                // FIXME: Do this after the equals check?
                SuppressAutoscrollDueToInputScroll = false;
            }
            if (control == _KeyboardSelection)
                return;
            _KeyboardSelection = control;
            MousePositionWhenKeyboardSelectionWasLastUpdated = LastMousePosition;
        }

        /// <summary>
        /// Indicates that the context is currently being interacted with by the user
        /// </summary>
        public bool IsActive {
            get =>
                (MouseOverLoose != null) ||
                    _LastInput.AreAnyKeysHeld ||
                    (KeyboardSelection != null) ||
                    (MouseCaptured != null) ||
                    AcceleratorOverlayVisible ||
                    (ModalStack.Count > 0);
        }

        /// <summary>
        /// Indicates that input is currently in progress (a key or button is held)
        /// </summary>
        public bool IsInputInProgress {
            get =>
                _LastInput.AreAnyKeysHeld ||
                    (MouseCaptured != null) ||
                    AcceleratorOverlayVisible;
        }

        internal void Log (string text) {
            if (OnLogMessage != null)
                OnLogMessage(text);
            else
                DefaultLogHandler(text);
        }

        [System.Diagnostics.Conditional("DEBUG")]
        internal void DebugLog (string text) {
            if (OnLogMessage != null)
                OnLogMessage(text);
            else
                DefaultLogHandler(text);
        }

        internal void DefaultLogHandler (string text) {
            if (System.Diagnostics.Debugger.IsAttached)
                System.Diagnostics.Debug.WriteLine(text);
            else
                Console.WriteLine(text);
        }

        public UIContext (DefaultMaterialSet materials, IGlyphSource font = null, ITimeProvider timeProvider = null)
            : this (
                materials: materials,
                decorations: new DefaultDecorations(materials) {
                    DefaultFont = font
                },
                timeProvider: timeProvider
            ) {
        }

        public UIContext (
            DefaultMaterialSet materials, IDecorationProvider decorations, 
            IAnimationProvider animations = null, ITimeProvider timeProvider = null
        ) {
            if (UseNewEngine)
                Engine = new NewEngine.LayoutEngine();
            EventBus = new EventBus();
            EventBus.AfterBroadcast += EventBus_AfterBroadcast;
            Controls = new ControlCollection(this);
            Decorations = decorations;
            Animations = animations ?? (decorations as IAnimationProvider);
            TimeProvider = TimeProvider ?? new DotNetTimeProvider();
            Materials = materials;
            TTS = new Accessibility.TTS(this);
            _LastInput = _CurrentInput = new InputState {
                CursorPosition = new Vector2(-99999)
            };
            _LastInput.HeldKeys = _LastHeldKeys;
            _CurrentInput.HeldKeys = _CurrentHeldKeys;
            CreateInputIDs();
        }

        public InputID GetInputID (Keys key, KeyboardModifiers modifiers) {
            foreach (var iid in InputIDs) {
                if ((iid.Key == key) && iid.Modifiers.Equals(modifiers))
                    return iid;
            }
            var result = new InputID { Key = key, Modifiers = modifiers };
            InputIDs.Add(result);
            return result;
        }

        private void EventBus_AfterBroadcast (EventBus sender, object eventSource, string eventName, object eventArgs, bool eventWasHandled) {
            if (eventWasHandled)
                return;

            UnhandledEvents.Add(new UnhandledEvent {
                Source = eventSource as Control,
                Name = eventName
            });
        }

        private Tooltip GetTooltipInstance () {
            if (CachedTooltip == null) {
                CachedTooltip = new Tooltip {
                    Appearance = { Opacity = 0 }
                };
                Controls.Add(CachedTooltip);
            }

            return CachedTooltip;
        }

        public void TryMoveCursor (Vector2 newPosition) {
            foreach (var provider in this.InputSources)
                if (IsPriorityInputSource(provider))
                    provider.TryMoveCursor(newPosition);
        }

        private Controls.StaticText GetCompositionPreviewInstance () {
            if (CachedCompositionPreview == null) {
                CachedCompositionPreview = new Controls.StaticText {
                    DisplayOrder = int.MaxValue,
                    Wrap = false,
                    Multiline = false,
                    Intangible = true,
                    LayoutFlags = ControlFlags.Layout_Floating,
                    Appearance = {
                        BackgroundColor = Color.White,
                        TextColor = Color.Black,
                        Decorator = Decorations.CompositionPreview,
                        TextDecorator = Decorations.CompositionPreview,
                    }
                };
                Controls.Add(CachedCompositionPreview);
            }

            return CachedCompositionPreview;
        }

        public bool CaptureMouse (Control target) {
            return CaptureMouse(target, out Control temp);
        }

        public bool CaptureMouse (Control target, out Control previous) {
            previous = Focused;
            if ((MouseCaptured != null) && (MouseCaptured != target))
                RetainCaptureRequested = target;
            // HACK: If we used IsValidFocusTarget here, it would break scenarios where a control is capturing
            //  focus before being shown or being enabled
            AutomaticallyTransferFocusOnTopLevelChange(target);
            if (target.AcceptsFocus)
                TrySetFocus(target, true, true);
            MouseCaptured = target;
            _PreferredTooltipSource = target;
            return (MouseCaptured == target);
        }

        public void RetainCapture (Control target) {
            RetainCaptureRequested = target;
        }

        public void ReleaseCapture (Control target, Control focusDonor, bool isUserInitiated = true) {
            if (Focused == target)
                // Technically capture may be getting released by the user, but focus returning to a donor is not
                //  user-initiated - it happens automatically, may not be what they clicked, and should suppress
                //  animations
                TrySetFocus(focusDonor, isUserInitiated: isUserInitiated, suppressAnimations: true);
            if (Hovering == target)
                Hovering = null;
            if (MouseCaptured == target)
                MouseCaptured = null;
            ReleasedCapture = target;
            if (RetainCaptureRequested == target)
                RetainCaptureRequested = null;
        }

        private void DoUpdateLayoutInternal (ref UIOperationContext context, bool secondTime) {
            Layout.CanvasSize = CanvasSize;
            Layout.SetContainerFlags(Layout.Root, ControlFlags.Container_Row);
            Layout.SetTag(Layout.Root, LayoutTags.Root);

            if (UseNewEngine) {
                ref var root = ref Engine.Root();
                Engine.CanvasSize = CanvasSize;
                root.ContainerFlags = ControlFlags.Container_Row;
                root.Tag = LayoutTags.Root;
            }

            _TopLevelControls.Clear();
            Controls.CopyTo(_TopLevelControls);

            foreach (var control in _TopLevelControls)
                control.GenerateLayoutTree(
                    ref context, Layout.Root, 
                    (secondTime && !control.LayoutKey.IsInvalid) 
                        ? control.LayoutKey 
                        : (ControlKey?)null
                );
        }

        private bool NotifyLayoutListeners (ref UIOperationContext context) {
            bool relayoutRequested = context.RelayoutRequestedForVisibilityChange;
            if (relayoutRequested && LogRelayoutRequests)
                Log($"Relayout requested due to visibility change");

            foreach (var listener in context.PostLayoutListeners) {
                var wasRequested = relayoutRequested;
                listener.OnLayoutComplete(ref context, ref relayoutRequested);
                if (relayoutRequested != wasRequested) {
                    var ctl = (Control)listener;
                    if (LogRelayoutRequests)
                        Log($"Relayout requested by {ctl.DebugLabel ?? listener.GetType().Name}");
                }
            }
            return relayoutRequested;
        }

        private float? NegativeToNull (float value) => (value <= -1) ? (float?)null : value;

        private unsafe void SyncEngines () {
            for (int i = 0; i < Layout.Count; i++) {
                var key = new ControlKey(i);
                var pItem = Layout.LayoutPtr(key, false);
                // FIXME
                if (pItem == null)
                    continue;
                ref var rec = ref Engine.GetOrCreate(key, pItem->Tag, pItem->Flags);
                rec.Margins = pItem->Margins;
                rec.Padding = pItem->Padding;
                rec.FloatingPosition = pItem->FloatingPosition;
                rec.Width = new ControlDimension {
                    Minimum = NegativeToNull(pItem->MinimumSize.X),
                    Maximum = NegativeToNull(pItem->MaximumSize.X),
                    Fixed = NegativeToNull(pItem->FixedSize.X)
                };
                rec.Height = new ControlDimension {
                    Minimum = NegativeToNull(pItem->MinimumSize.Y),
                    Maximum = NegativeToNull(pItem->MaximumSize.Y),
                    Fixed = NegativeToNull(pItem->FixedSize.Y)
                };

                // FIXME: Is this always right? Probably
                rec._FirstChild = pItem->FirstChild;
                rec._LastChild = pItem->LastChild;
                rec._PreviousSibling = pItem->PreviousSibling;
                rec._NextSibling = pItem->NextSibling;
                rec._Parent = pItem->Parent;
            }
        }

        public void Update () {
            FrameIndex++;

            var context = MakeOperationContext(ref _UpdateFree, ref _UpdateInUse);
            var pll = Interlocked.Exchange(ref _PostLayoutListeners, null);
            if (pll == null)
                pll = new UnorderedList<IPostLayoutListener>();
            else
                pll.Clear();
            context.Shared.PostLayoutListeners = pll;

            try {
                Layout.Clear();
                if (UseNewEngine)
                    Engine.Clear();

                DoUpdateLayoutInternal(ref context, false);
                Layout.Update();
                if (UseNewEngine) {
                    SyncEngines();
                    Engine.Update();
                    // TODO: Perform a pass after this that copies the LayoutResult into all of our controls,
                    //  so that they will always have valid data from the most recent update even if we are
                    //  in the middle of a new update
                    // The easiest solution would be to always have the Controls[] array in Engine and copy
                    //  everything into it at the end of Update. This is probably worthwhile since it is cache
                    //  efficient and most controls will have their rect used at least once for rasterization
                    //  or hit testing
                }

                if (NotifyLayoutListeners(ref context)) {
                    DoUpdateLayoutInternal(ref context, true);
                    Layout.Update();
                    if (UseNewEngine) {
                        SyncEngines();
                        Engine.Update();
                    }
                    NotifyLayoutListeners(ref context);
                }

                UpdateAutoscroll();
            } finally {
                Interlocked.CompareExchange(ref _PostLayoutListeners, pll, null);
                context.Shared.InUse = false;
            }
        }

        private void UpdateCaptureAndHovering (Vector2 mousePosition, Control exclude = null) {
            // FIXME: This breaks drag-to-scroll
            // MouseOver = HitTest(mousePosition, rejectIntangible: true);
            MouseOver = MouseOverLoose = HitTest(mousePosition);

            if ((MouseOver != MouseCaptured) && (MouseCaptured != null))
                Hovering = null;
            else
                Hovering = MouseOver;
        }

        public void ShowModal (IModal modal, bool topmost) {
            if (ModalStack.Contains(modal))
                throw new InvalidOperationException("Modal already visible");
            var ctl = (Control)modal;
            ctl.DisplayOrder = Controls.PickNewHighestDisplayOrder(ctl, topmost);
            if (!Controls.Contains(ctl))
                Controls.Add(ctl);
            NotifyModalShown(modal);
        }

        public bool CloseActiveModal (bool force = false) {
            if (ModalStack.Count <= 0)
                return false;
            return CloseModal(ModalStack[ModalStack.Count - 1], force);
        }

        public bool CloseModal (IModal modal, bool force = false) {
            if (!ModalStack.Contains(modal))
                return false;
            return modal.Close(force);
        }

        public void NotifyModalShown (IModal modal) {
            var ctl = (Control)modal;
            if (!Controls.Contains(ctl))
                throw new InvalidOperationException("Modal not in top level controls list");
            TopLevelFocusMemory.Remove(ctl);
            // FIXME: Reopening a menu really quick can cause this, it's probably harmless?
            if (false && ModalStack.Contains(modal))
                throw new InvalidOperationException("Modal already visible");
            else
                ModalStack.Add(modal);
            TrySetFocus(ctl, false, false);
            FireEvent(UIEvents.Shown, ctl);
        }

        public void NotifyModalClosed (IModal modal) {
            if (modal == null)
                return;

            var ctl = (Control)modal;
            var newFocusTarget = (TopLevelFocused == ctl)
                ? modal.FocusDonor
                : null;
            // FIXME: Track user initated flag?
            ReleaseCapture(ctl, modal.FocusDonor, false);
            if (!ModalStack.Contains(modal))
                return;
            ModalStack.Remove(modal);
            FireEvent(UIEvents.Closed, ctl);
            if (newFocusTarget != null)
                TrySetFocus(newFocusTarget, false, false);
        }

        public bool IsPriorityInputSource (IInputSource source) {
            if (ScratchInputSources.Count > 0)
                return ScratchInputSources.IndexOf(source) == 0;
            else
                return InputSources.IndexOf(source) == 0;
        }

        public void PromoteInputSource (IInputSource source) {
            var existingIndex = InputSources.IndexOf(source);
            if (existingIndex == 0)
                return;
            else if (existingIndex > 0)
                InputSources.RemoveAt(existingIndex);
            InputSources.Insert(0, source);
        }

        public void UpdateInput (bool processEvents = true) {
            Now = (float)TimeProvider.Seconds;
            NowL = TimeProvider.Ticks;
            FocusedAtStartOfUpdate = Focused;

            if ((_CurrentInput.CursorPosition.X < -999) ||
                (_CurrentInput.CursorPosition.Y < -999))
                _CurrentInput.CursorPosition = CanvasSize / 2f;

            _LastInput = _CurrentInput;
            _LastInput.HeldKeys = _LastHeldKeys;
            _LastHeldKeys.Clear();
            foreach (var k in _CurrentHeldKeys)
                _LastHeldKeys.Add(k);

            _CurrentHeldKeys.Clear();
            _CurrentInput = new InputState {
                HeldKeys = _CurrentHeldKeys,
                CursorPosition = _LastInput.CursorPosition,
                WheelValue = _LastInput.WheelValue
            };

            ScratchInputSources.Clear();
            foreach (var src in InputSources)
                ScratchInputSources.Add(src);

            foreach (var src in ScratchInputSources) {
                src.SetContext(this);
                src.Update(ref _LastInput, ref _CurrentInput);
            }

            ScratchInputSources.Clear();

            _CurrentInput.AreAnyKeysHeld = _CurrentInput.HeldKeys.Count > 0;

            if (!processEvents)
                return;

            foreach (var mea in PurgatoryMouseEventArgs) {
                if (SpareMouseEventArgs.Count >= 32)
                    break;
                SpareMouseEventArgs.Add(mea);
            }
            PurgatoryMouseEventArgs.Clear();

            foreach (var mea in UsedMouseEventArgs)
                PurgatoryMouseEventArgs.Add(mea);
            UsedMouseEventArgs.Clear();

            var mousePosition = _CurrentInput.CursorPosition;

            PreviousUnhandledEvents.Clear();
            foreach (var evt in UnhandledEvents)
                PreviousUnhandledEvents.Add(evt);
            UnhandledEvents.Clear();

            var queuedFocus = QueuedFocus;
            var activeModal = ActiveModal;

            if (queuedFocus.value != null) {
                if (
                    (activeModal == null) || 
                    // Attempting to set focus to something outside of a modal can cause it to close
                    Control.IsEqualOrAncestor(queuedFocus.value, (Control)activeModal)
                ) {
                    if (TrySetFocus(queuedFocus.value, queuedFocus.force, queuedFocus.isUserInitiated, queuedFocus.suppressAnimations))
                        QueuedFocus = default;
                }
            }

            if (
                (Focused != null) && 
                (!Focused.IsValidFocusTarget || (FindTopLevelAncestor(Focused) == null))
            ) {
                // If the current focused control is no longer enabled or present, attempt to
                //  focus something else in the current top level control, if possible
                if (Controls.Contains(TopLevelFocused)) {
                    // HACK: Unfortunately, there's probably nothing useful to do here
                    // I suppose a focusable child could appear out of nowhere? But I don't think we'd want to
                    //  suddenly change focus if that happened. If we don't do this we waste a ton of CPU
                    //  doing pointless treewalks and allocating garbage for nothing.
                    if (TopLevelFocused != Focused)
                        Focused = TopLevelFocused;
                    else 
                        ;
                } else
                    NotifyControlBecomingInvalidFocusTarget(Focused, false);
            }

            EnsureValidFocus();

            // Detect that while we successfully applied queued focus, it was reset somehow, and queue it again
            if (
                (queuedFocus.value != null) && (QueuedFocus.value == null) &&
                (Focused != queuedFocus.value)
            ) {
                // FIXME: This shouldn't really happen
                QueuedFocus = queuedFocus;
            }

            // We want to do this check once per frame since during a given frame, the focus
            //  may move multiple times and we don't want to pointlessly start the animation
            //  if focus ends up going back to where it originally was
            if (_PreviouslyFocusedForTimestampUpdate != _Focused) {
                if ((_Focused == _MouseCaptured) || SuppressFocusChangeAnimationsThisStep)
                    LastFocusChange = 0;
                else
                    LastFocusChange = NowL;
            }
            SuppressFocusChangeAnimationsThisStep = false;
            _PreviouslyFocusedForTimestampUpdate = _Focused;

            var previouslyFixated = FixatedControl;
            var previouslyHovering = Hovering;

            UpdateCaptureAndHovering(_CurrentInput.CursorPosition);
            var mouseEventTarget = MouseCaptured ?? Hovering;
            var topLevelTarget = (mouseEventTarget != null)
                ? FindTopLevelAncestor(mouseEventTarget)
                : null;

            var wasInputBlocked = false;
            if (
                (activeModal?.BlockInput == true) && 
                (activeModal != topLevelTarget) &&
                (topLevelTarget?.DisplayOrder <= (activeModal as Control)?.DisplayOrder)
            ) {
                mouseEventTarget = null;
                wasInputBlocked = true;
            }

            // If the mouse moves after the keyboard selection was updated, clear it
            if (KeyboardSelection != null) {
                var movedDistance = mousePosition - MousePositionWhenKeyboardSelectionWasLastUpdated;
                if (movedDistance.Length() > MinimumMouseMovementDistance) {
                    if (_CurrentInput.KeyboardNavigationEnded)
                        ClearKeyboardSelection();
                }
            }

            var mouseOverLoose = MouseOverLoose;

            if (LastMousePosition != mousePosition) {
                if (DragToScrollTarget != null)
                    HandleMouseDrag((Control)DragToScrollTarget, mousePosition);
                else if (CurrentMouseButtons != MouseButtons.None)
                    HandleMouseDrag(mouseEventTarget, mousePosition);
                else
                    HandleMouseMove(mouseEventTarget ?? mouseOverLoose, mousePosition);
            }

            var mouseDownPosition = MouseDownPosition;
            var previouslyCaptured = MouseCaptured;
            var processClick = false;

            if ((LastMouseButtons == MouseButtons.None) && (CurrentMouseButtons != MouseButtons.None)) {
                if (
                    (mouseEventTarget == null) && 
                    (mouseOverLoose != null)
                ) {
                    HandleMouseDownPrologue();
                    HandleMouseDownEpilogue(false, mouseOverLoose, mousePosition, CurrentMouseButtons);
                } else {
                    HandleMouseDown(mouseEventTarget, mousePosition, CurrentMouseButtons);
                }
                mouseDownPosition = mouseDownPosition ?? mousePosition;
            } else if ((LastMouseButtons != MouseButtons.None) && (CurrentMouseButtons == MouseButtons.None)) {
                bool scrolled = false;
                if (Hovering != null)
                    scrolled = HandleMouseUp(mouseEventTarget, mousePosition, mouseDownPosition, LastMouseButtons);
                else if (DragToScrollTarget != null)
                    scrolled = TeardownDragToScroll(mouseEventTarget, mousePosition);
                else /* if (MouseCaptured != null) */
                    scrolled = HandleMouseUp(mouseEventTarget, mousePosition, mouseDownPosition, LastMouseButtons);

                // if (MouseCaptured != null) {
                var movedDistance = mousePosition - mouseDownPosition;
                var hasMoved = movedDistance.HasValue &&
                        (movedDistance.Value.Length() >= MinimumMouseMovementDistance);
                if (
                    !hasMoved &&
                    (!scrolled || !SuppressSingleClickOnMovementWhenAppropriate)
                )
                    processClick = true;
                // }

                if (MouseCaptured != null) {
                    if (RetainCaptureRequested == MouseCaptured) {
                        RetainCaptureRequested = null;
                    } else {
                        MouseCaptured = null;
                    }
                }

                // FIXME: Clear LastMouseDownTime?
            } else if (LastMouseButtons != CurrentMouseButtons) {
                FireEvent(UIEvents.MouseButtonsChanged, mouseEventTarget, MakeMouseEventArgs(mouseEventTarget, mousePosition, mouseDownPosition));
            }

            if (processClick && !wasInputBlocked) {
                // FIXME: if a menu is opened by a mousedown event, this will
                //  fire a click on the menu in response to its mouseup
                if (
                    ((Hovering == previouslyCaptured) && (previouslyCaptured != null)) ||
                    ((previouslyCaptured == null) && (Hovering == PreviousMouseDownTarget))
                ) {
                    // FIXME: Is this ?? right
                    var clickTarget = previouslyCaptured ?? PreviousMouseDownTarget;
                    if (
                        (clickTarget?.AcceptsNonLeftClicks == true) || 
                        ((LastMouseButtons & MouseButtons.Left) == MouseButtons.Left)
                    )
                        HandleClick(clickTarget, mousePosition, mouseDownPosition ?? mousePosition);
                    else
                        ; // FIXME: Fire another event here?
                } else
                    HandleDrag(previouslyCaptured, Hovering);
            }

            var mouseWheelDelta = _CurrentInput.WheelValue - _LastInput.WheelValue;

            if (mouseWheelDelta != 0)
                HandleScroll(MouseOverLoose ?? previouslyCaptured, mouseWheelDelta);

            TickControl(KeyboardSelection, mousePosition, mouseDownPosition);
            if (Hovering != KeyboardSelection)
                TickControl(Hovering, mousePosition, mouseDownPosition);
            if ((MouseCaptured != KeyboardSelection) && (MouseCaptured != Hovering))
                TickControl(MouseCaptured, mousePosition, mouseDownPosition);

            UpdateTooltip((CurrentMouseButtons != MouseButtons.None));

            if (CurrentInputState.ScrollDistance.Length() >= 0.5f) {
                var implicitScrollTarget = CurrentImplicitScrollTarget;
                if ((implicitScrollTarget == null) || Control.IsRecursivelyTransparent(implicitScrollTarget, true))
                    implicitScrollTarget = KeyboardSelection ?? Hovering ?? MouseOverLoose ?? Focused;

                if (implicitScrollTarget != null) {
                    if (AttemptTargetedScroll(implicitScrollTarget, CurrentInputState.ScrollDistance, recursive: false))
                        CurrentImplicitScrollTarget = implicitScrollTarget;
                }
            } else {
                CurrentImplicitScrollTarget = null;
            }

            EnsureValidFocus();

            if (FixatedControl != previouslyFixated)
                HandleFixationChange(previouslyFixated, FixatedControl);
        }

        private void TickControl (Control control, Vector2 globalPosition, Vector2? mouseDownPosition) {
            if (control == null)
                return;
            control.Tick(MakeMouseEventArgs(control, globalPosition, mouseDownPosition));
        }

        private bool IsTooltipActive {
            get {
                return CachedTooltip?.Visible == true;
            }
        }

        private void ResetTooltipShowTimer () {
            FirstTooltipHoverTime = null;
        }

        private bool IsTooltipPriority (Control control) {
            var ictt = control as ICustomTooltipTarget;
            if (ictt == null)
                return false;
            var tts = ictt.TooltipSettings;
            if (tts == null)
                return false;

            return (tts.ShowWhileFocused || tts.ShowWhileKeyboardFocused) && (control == Focused);
        }

        private Control PickTooltipTarget (bool leftButtonPressed) {
            // Fixes case where a menu is closed while it's hosting a tooltip
            if (_PreferredTooltipSource?.IsValidFocusTarget == false)
                _PreferredTooltipSource = null;

            var fixated = FixatedControl;
            if (
                ((Focused as ICustomTooltipTarget)?.TooltipSettings?.HostsChildTooltips == true) &&
                ((fixated == null) || Control.IsEqualOrAncestor(fixated, Focused))
            ) {
                if (IsTooltipPriority(_PreferredTooltipSource))
                    return _PreferredTooltipSource;
                else
                    return Focused;
            } else {
                if ((_PreferredTooltipSource != Focused) && (_PreferredTooltipSource != null) && _PreferredTooltipSource.AcceptsFocus)
                    return fixated;

                if (!IsTooltipPriority(fixated) && IsTooltipPriority(_PreferredTooltipSource))
                    return _PreferredTooltipSource;
                else
                    return fixated ?? _PreferredTooltipSource;
            }
        }

        private bool IsTooltipAllowedToAppear (Control target, bool leftButtonPressed) {
            var tts = (target as ICustomTooltipTarget)?.TooltipSettings;
            if (tts == null)
                return !leftButtonPressed;

            var result = leftButtonPressed
                ? tts.ShowWhileMouseIsHeld
                : tts.ShowWhileMouseIsNotHeld;
            if (target == KeyboardSelection)
                result |= tts.ShowWhileKeyboardFocused;
            if (target == Focused)
                result |= tts.ShowWhileFocused;
            return result;
        }

        public void HideTooltip (Control control) {
            if (PreviousTooltipAnchor != control)
                return;
            HideTooltip(true);
        }

        public void HideTooltip () {
            HideTooltip(true);
        }

        private AbstractString GetTooltipTextForControl (Control target, bool leftButtonPressed, out AbstractTooltipContent content) {
            content = default;
            if (!IsTooltipAllowedToAppear(target, leftButtonPressed))
                return default;

            var cttt = target as ICustomTooltipTarget;
            var tts = cttt?.TooltipSettings;

            if (target != null) {
                if (cttt != null)
                    content = cttt.GetContent();
                else
                    content = target.TooltipContent;
            }
            return content.Get(target);
        }

        private void UpdateTooltip (bool leftButtonPressed) {
            var target = PickTooltipTarget(leftButtonPressed);
            var tooltipText = GetTooltipTextForControl(target, leftButtonPressed, out AbstractTooltipContent tooltipContent);
            if (tooltipText.IsNull) {
                // HACK: If the focused control explicitly requests to have its tooltip visible while it's focused,
                //  make sure we fall back to showing its tooltip if we didn't pick a better target
                if (
                    // FIXME: This sucks
                    false &&
                    (Focused is ICustomTooltipTarget ictt) && 
                    ictt.TooltipSettings.ShowWhileFocused &&
                    (Hovering?.AcceptsMouseInput != true) &&
                    (MouseOverLoose?.AcceptsMouseInput != true)
                ) {
                    target = Focused;
                    tooltipText = GetTooltipTextForControl(target, leftButtonPressed, out tooltipContent);
                }
            }

            var cttt = target as ICustomTooltipTarget;
            var tts = cttt?.TooltipSettings;

            var now = Now;
            var disappearDelay = (tts?.DisappearDelay ?? TooltipDisappearDelay);

            // FIXME: When a menu appears, its tooltip will appear in the wrong spot for a frame or two
            //  until the menu shows up. Right now menus hack around this by disabling their tooltips,
            //  but it would be better to have a robust general solution for that problem

            if (
                !tooltipText.IsNull && 
                // HACK: Setting .Visible = false on the current tooltip target or one of its
                //  parents will normally leave the tooltip open unless we do this
                !Control.IsRecursivelyTransparent(target, true)
            ) {
                if (!FirstTooltipHoverTime.HasValue)
                    FirstTooltipHoverTime = now;

                if (IsTooltipActive)
                    LastTooltipHoverTime = now;

                var hoveringFor = now - FirstTooltipHoverTime;
                var disappearTimeout = now - LastTooltipHoverTime;
                var version = target.TooltipContentVersion + target.TooltipContent.Version;

                if (
                    (hoveringFor >= (tts?.AppearDelay ?? TooltipAppearanceDelay)) || 
                    (disappearTimeout < disappearDelay)
                ) {
                    ShowTooltip(
                        target, cttt, tooltipText, 
                        tooltipContent, CurrentTooltipContentVersion != version
                    );
                    CurrentTooltipContentVersion = version;
                }
            } else {
                var shouldDismissInstantly = (target != null) && IsTooltipActive && 
                    GetTooltipInstance().GetRect(context: this).Contains(LastMousePosition);

                // TODO: Instead of instantly hiding, maybe just fade the tooltip out partially?
                HideTooltip(shouldDismissInstantly, disappearDelay);

                var elapsed = now - LastTooltipHoverTime;
                if (elapsed >= disappearDelay)
                    ResetTooltipShowTimer();
            }
        }

        /// <summary>
        /// Updates key/click repeat state for the current timestamp and returns true if a click should be generated
        /// </summary>
        public bool UpdateRepeat (double now, double firstTime, ref double mostRecentTime, double speedMultiplier = 1, double accelerationMultiplier = 1) {
            // HACK: Handle cases where mostRecentTime has not been initialized by the initial press
            if (mostRecentTime < firstTime)
                mostRecentTime = firstTime;

            double repeatSpeed = Arithmetic.Lerp(KeyRepeatIntervalSlow, KeyRepeatIntervalFast, (float)((now - firstTime) / KeyRepeatAccelerationDelay * accelerationMultiplier)) / speedMultiplier;
            if (
                ((now - firstTime) >= FirstKeyRepeatDelay) &&
                ((now - mostRecentTime) >= repeatSpeed)
            ) {
                mostRecentTime = now;
                return true;
            }

            return false;
        }

        private void HideTooltipForMouseInput (bool isMouseDown) {
            var cttt = PickTooltipTarget(isMouseDown) as ICustomTooltipTarget;
            var tts = cttt?.TooltipSettings;
            if (tts != null) {
                if (!tts.HideOnMousePress)
                    return;
            }

            ResetTooltipShowTimer();
            HideTooltip(true);
            FirstTooltipHoverTime = null;
            LastTooltipHoverTime = 0;
        }

        private void HideTooltip (bool instant, float disappearDelay = 0f) {
            if (CachedTooltip == null)
                return;

            if (instant)
                CachedTooltip.Appearance.Opacity = 0;
            else if (IsTooltipVisible)
                CachedTooltip.Appearance.Opacity = Tween.StartNow(
                    CachedTooltip.Appearance.Opacity.Get(Now), 0, now: NowL, delay: disappearDelay,
                    seconds: TooltipFadeDuration * (Animations?.AnimationDurationMultiplier ?? 1)
                );
            IsTooltipVisible = false;
        }

        /// <summary>
        /// Use at your own risk! Performs immediate layout of a control and its children.
        /// The results of this are not necessarily accurate, but can be used to infer its ideal size for positioning.
        /// </summary>
        public void UpdateSubtreeLayout (Control subtreeRoot) {
            var tempCtx = MakeOperationContext();

            var pll = Interlocked.Exchange(ref _PostLayoutListeners, null);
            if (pll == null)
                pll = new UnorderedList<IPostLayoutListener>();
            else
                pll.Clear();
            tempCtx.Shared.PostLayoutListeners = pll;

            var wasUpdatingSubtreeLayout = IsUpdatingSubtreeLayout;
            try {
                IsUpdatingSubtreeLayout = true;
                UpdateSubtreeLayout(ref tempCtx, subtreeRoot);

                if (NotifyLayoutListeners(ref tempCtx)) {
                    DoUpdateLayoutInternal(ref tempCtx, true);
                    UpdateSubtreeLayout(ref tempCtx, subtreeRoot);
                    NotifyLayoutListeners(ref tempCtx);
                }
            } finally {
                IsUpdatingSubtreeLayout = wasUpdatingSubtreeLayout;
                Interlocked.CompareExchange(ref _PostLayoutListeners, pll, null);
            }
        }

        private void UpdateSubtreeLayout (ref UIOperationContext context, Control subtreeRoot) {
            ControlKey parentKey;
            Control parent;
            if (!subtreeRoot.TryGetParent(out parent))
                parentKey = Layout.Root;
            else if (!parent.LayoutKey.IsInvalid)
                parentKey = parent.LayoutKey;
            else {
                // Just in case for some reason the control's parent also hasn't had layout happen...
                UpdateSubtreeLayout(ref context, parent);
                return;
            }

            subtreeRoot.GenerateLayoutTree(
                ref context, parentKey, 
                subtreeRoot.LayoutKey.IsInvalid 
                    ? (ControlKey?)null 
                    : subtreeRoot.LayoutKey
            );
            if (UseNewEngine)
                Engine.UpdateSubtree(subtreeRoot.LayoutKey);
            else
                Layout.UpdateSubtree(subtreeRoot.LayoutKey);
        }

        private void ShowTooltip (
            Control target, ICustomTooltipTarget cttt, AbstractString text, 
            AbstractTooltipContent content, bool textIsInvalidated
        ) {
            var instance = GetTooltipInstance();

            var textChanged = !instance.Text.TextEquals(text, StringComparison.Ordinal) || 
                textIsInvalidated;

            var tts = cttt?.TooltipSettings;
            var anchor = cttt?.Anchor ?? target;
            // HACK: Copy the target's decoration provider so the tooltip matches
            instance.Appearance.DecorationProvider = (
                anchor.Appearance.DecorationProvider ?? 
                target.Appearance.DecorationProvider ?? 
                Decorations
            );
            var fireEvent = (anchor != PreviousTooltipAnchor) || !IsTooltipVisible;

            // FIXME: For menus and perhaps list boxes, keyboard navigation sets the tooltip target
            //  to be the selected item instead of the list/menu and this ignores the container's settings
            instance.Move(
                anchor, 
                tts?.AnchorPoint ?? content.Settings.AnchorPoint, 
                tts?.ControlAlignmentPoint ?? content.Settings.ControlAlignmentPoint
            );

            instance.Visible = true;
            instance.DisplayOrder = int.MaxValue;

            // HACK: TextLayoutIsIncomplete == true indicates that an image embedded in the tooltip content is
            //  still loading. We need to keep recalculating our size until all the images have loaded, since
            //  the images can change the size of our tooltip content
            if (textChanged || !IsTooltipVisible || instance.TextLayoutIsIncomplete) {
                /*
                if (instance.TextLayoutIsIncomplete)
                    System.Diagnostics.Debug.WriteLine($"TextLayoutIsIncomplete {FrameIndex}");
                */

                var idealMaxSize = CanvasSize * (content.Settings.MaxSize ?? tts?.MaxSize ?? MaxTooltipSize);

                instance.Text = text;
                instance.ApplySettings(content.Settings);
                // FIXME: Shift it around if it's already too close to the right side
                instance.Width.Maximum = idealMaxSize.X;
                instance.Height.Maximum = idealMaxSize.Y;
                instance.Invalidate();

                // FIXME: Sometimes this keeps happening every frame
                UpdateSubtreeLayout(instance);

                /*
                if (instance.TextLayoutIsIncomplete)
                    System.Diagnostics.Debug.WriteLine($"TextLayoutStillIncomplete {FrameIndex}");
                */
            }

            var currentOpacity = instance.Appearance.Opacity.Get(Now);
            if (!IsTooltipVisible)
                instance.Appearance.Opacity = Tween.StartNow(
                    currentOpacity, 1f, 
                    seconds: (currentOpacity > 0.1 ? TooltipFadeDurationFast : TooltipFadeDuration) * (Animations?.AnimationDurationMultiplier ?? 1), 
                    now: NowL
                );
            if ((anchor != PreviousTooltipAnchor) && (currentOpacity > 0))
                instance.Appearance.Opacity = 1f;

            PreviousTooltipAnchor = anchor;
            IsTooltipVisible = true;
            UpdateSubtreeLayout(instance);

            if (fireEvent)
                FireEvent(UIEvents.TooltipShown, anchor);
        }

        public Control HitTest (Vector2 position) {
            return HitTest(position, default, out _);
        }

        public Control HitTest (Vector2 position, in HitTestOptions options) {
            return HitTest(position, in options, out _);
        }

        // Position is relative to the top-left corner of the canvas
        public Control HitTest (Vector2 position, in HitTestOptions options, out Vector2 localPosition) {
            localPosition = default;

            var areHitTestsBlocked = false;
            foreach (var m in ModalStack)
                if (m.BlockHitTests)
                    areHitTestsBlocked = true;

            var sorted = Controls.InDisplayOrder(FrameIndex);
            for (var i = sorted.Count - 1; i >= 0; i--) {
                var control = sorted[i];
                if (
                    areHitTestsBlocked && 
                    !ModalStack.Contains(control as IModal) &&
                    // HACK to allow floating controls over the modal stack
                    (control.DisplayOrder <= (ActiveModal as Control)?.DisplayOrder)
                )
                    continue;
                var result = control.HitTest(position, in options, out localPosition);
                if (result != null)
                    return result;
            }

            return null;
        }

        private volatile UIOperationContextShared _RasterizeFree, _RasterizeInUse, _UpdateFree, _UpdateInUse;

        internal UIOperationContext MakeOperationContext (ref UIOperationContextShared _free, ref UIOperationContextShared _inUse) {
            var free = Interlocked.Exchange(ref _free, null);
            if (free?.InUse != false)
                free = new UIOperationContextShared();

            var inUse = Interlocked.Exchange(ref _inUse, null);
            if (inUse?.InUse == false)
                Interlocked.CompareExchange(ref _free, inUse, null);

            Interlocked.CompareExchange(ref _inUse, free, null);

            InitializeOperationContextShared(free);
            return MakeOperationContextFromInitializedShared(free);
        }

        private void InitializeOperationContextShared (UIOperationContextShared shared) {
            shared.InUse = true;
            shared.Context = this;
            shared.Now = Now;
            shared.NowL = NowL;
            shared.Modifiers = CurrentModifiers;
            shared.ActivateKeyHeld = _LastInput.ActivateKeyHeld;
            shared.MouseButtonHeld = (LastMouseButtons != MouseButtons.None);
            shared.MousePosition = LastMousePosition;
            shared.PostLayoutListeners = null;
        }

        internal UIOperationContext MakeOperationContext () {
            var shared = new UIOperationContextShared();
            InitializeOperationContextShared(shared);
            return MakeOperationContextFromInitializedShared(shared);
        }

        private UIOperationContext MakeOperationContextFromInitializedShared (UIOperationContextShared shared) {
            if (!shared.InUse)
                throw new Exception("Not initialized");

            return new UIOperationContext {
                Shared = shared,
                Opacity = 1,
                VisibleRegion = new RectF(-VisibilityPadding, -VisibilityPadding, CanvasSize.X + (VisibilityPadding * 2), CanvasSize.Y + (VisibilityPadding * 2))
            };
        }

        public void Dispose () {
            Layout.Dispose();

            foreach (var rt in ScratchRenderTargets)
                rt.Dispose();

            ScratchRenderTargets.Clear();
        }
    }

    internal class UIOperationContextShared {
        public UIContext Context;
        public float Now;
        public long NowL;
        public KeyboardModifiers Modifiers;
        public bool ActivateKeyHeld;
        public bool MouseButtonHeld;
        public Vector2 MousePosition;
        internal volatile bool InUse;
        internal UnorderedList<IPostLayoutListener> PostLayoutListeners;
    }

    public struct UIOperationContext {
        public static UIOperationContext Default = default(UIOperationContext);

        internal UIOperationContextShared Shared;

        public UIContext UIContext => Shared?.Context;
        public DefaultMaterialSet Materials => UIContext?.Materials;
        public LayoutContext Layout => UIContext?.Layout;
        public NewEngine.LayoutEngine Engine => UIContext?.Engine;

        public float Now => Shared?.Now ?? 0f;
        public long NowL => Shared?.NowL ?? 0;
        public KeyboardModifiers Modifiers => Shared?.Modifiers ?? default;
        public bool ActivateKeyHeld => Shared?.ActivateKeyHeld ?? false;
        public bool MouseButtonHeld => Shared?.MouseButtonHeld ?? false;
        public ref readonly Vector2 MousePosition => ref Shared.MousePosition;
        internal UnorderedList<IPostLayoutListener> PostLayoutListeners => Shared?.PostLayoutListeners;

        public RasterizePasses Pass;
        public float Opacity { get; internal set; }
        public RectF VisibleRegion { get; internal set; }
        public BatchGroup Prepass;
        private DenseList<IDecorator> DecoratorStack, TextDecoratorStack;
        private DenseList<IDecorationProvider> DecorationProviderStack;
        internal DenseList<UIContext.ScratchRenderTarget> RenderTargetStack;
        internal short HiddenCount, Depth;
        internal bool RelayoutRequestedForVisibilityChange, TransformActive;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private T GetStackTop<T> (in DenseList<T> stack) {
            var index = stack.Count - 1;
            if (index < 0)
                return default;
            else
                return stack[index];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void StackPush<T> (ref DenseList<T> stack, T value) {
            stack.Add(value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void StackPop<T> (ref DenseList<T> stack) {
            var index = stack.Count - 1;
            if (index < 0)
                throw new InvalidOperationException("Stack empty");
            stack.RemoveAt(index);
        }

        public IDecorationProvider DecorationProvider => GetStackTop(in DecorationProviderStack) ?? UIContext?.Decorations;
        public static void PushDecorationProvider (ref UIOperationContext context, IDecorationProvider value) => 
            StackPush(ref context.DecorationProviderStack, value);
        public static void PopDecorationProvider (ref UIOperationContext context) => 
            StackPop(ref context.DecorationProviderStack);
        public IDecorator DefaultDecorator => GetStackTop(in DecoratorStack);
        public static void PushDecorator (ref UIOperationContext context, IDecorator value) => 
            StackPush(ref context.DecoratorStack, value);
        public static void PopDecorator (ref UIOperationContext context) => 
            StackPop(ref context.DecoratorStack);
        public IDecorator DefaultTextDecorator => GetStackTop(in TextDecoratorStack);
        public static void PushTextDecorator (ref UIOperationContext context, IDecorator value) => 
            StackPush(ref context.TextDecoratorStack, value);
        public static void PopTextDecorator (ref UIOperationContext context) => 
            StackPop(ref context.TextDecoratorStack);

        public void Log (string text) {
            UIContext.Log(text);
        }

        [System.Diagnostics.Conditional("DEBUG")]
        public void DebugLog (string text) {
            UIContext.DebugLog(text);
        }

        public void Clone (out UIOperationContext result) {
            result = new UIOperationContext {
                Shared = Shared,
                Pass = Pass,
                VisibleRegion = VisibleRegion,
                Depth = (short)(Depth + 1),
                HiddenCount = HiddenCount,
                Opacity = Opacity,
                Prepass = Prepass,
                RelayoutRequestedForVisibilityChange = RelayoutRequestedForVisibilityChange
            };
            RenderTargetStack.Clone(ref result.RenderTargetStack, true);
            DecoratorStack.Clone(ref result.DecoratorStack, true);
            TextDecoratorStack.Clone(ref result.TextDecoratorStack, true);
            DecorationProviderStack.Clone(ref result.DecorationProviderStack, true);
        }
    }
}
