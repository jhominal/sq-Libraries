﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Squared.Game;
using Squared.PRGUI.Decorations;
using Squared.PRGUI.Imperative;
using Squared.PRGUI.Layout;
using Squared.Render.Convenience;
using Squared.Util;

namespace Squared.PRGUI.Controls {
    public class Container : Control, IControlContainer, IScrollableControl, IPostLayoutListener {
        public ControlCollection Children { get; private set; }

        /// <summary>
        /// If set, children will only be rendered within the volume of this container
        /// </summary>
        public bool ClipChildren { get; set; } = false;
        public bool Scrollable { get; set; } = false;
        public bool ShowVerticalScrollbar = true, ShowHorizontalScrollbar = true;

        private Vector2 _ScrollOffset;
        protected Vector2 AbsoluteDisplayOffsetOfChildren;

        protected float MostRecentHeaderHeight = 0;
        protected RectF? MostRecentFullSize = null;
        protected Vector2 MinScrollOffset;
        protected Vector2? MaxScrollOffset;

        bool IScrollableControl.AllowDragToScroll => true;
        Vector2? IScrollableControl.MinScrollOffset => MinScrollOffset;
        Vector2? IScrollableControl.MaxScrollOffset => MaxScrollOffset;

        public ControlFlags ContainerFlags { get; set; } =
            ControlFlags.Container_Align_Start | ControlFlags.Container_Row | 
            ControlFlags.Container_Wrap;

        protected ScrollbarState HScrollbar, VScrollbar;
        protected bool HasContentBounds, CanScrollVertically;
        protected RectF ContentBounds;

        protected ContainerBuilder DynamicBuilder;
        /// <summary>
        /// If set, every update this delegate will be invoked to reconstruct the container's children
        /// </summary>
        public ContainerContentsDelegate DynamicContents;

        protected virtual bool FreezeDynamicContent => false;

        public Container () 
            : base () {
            Children = new ControlCollection(this);
            AcceptsMouseInput = true;

            HScrollbar = new ScrollbarState {
                DragInitialMousePosition = null,
                Horizontal = true
            };
            VScrollbar = new ScrollbarState {
                DragInitialMousePosition = null,
                Horizontal = false
            };
        }

        private Vector2 DesiredScrollOffset;

        public bool TrySetScrollOffset (Vector2 value, bool forUserInput) {
            if (!CanScrollVertically)
                value.Y = 0;
            if (!Scrollable)
                value = Vector2.Zero;
            if (forUserInput && (value != DesiredScrollOffset))
                DesiredScrollOffset = value;
            value = ConstrainNewScrollOffset(value);
            if (value == _ScrollOffset)
                return false;

            // Console.WriteLine("ScrollOffset {0} -> {1}", _ScrollOffset, value);
            _ScrollOffset = value;
            OnDisplayOffsetChanged();
            return true;
        }

        public Vector2 ScrollOffset {
            get {
                return _ScrollOffset;
            }
            set {
                TrySetScrollOffset(value, false);
            }
        }

        private Vector2 ConstrainNewScrollOffset (Vector2 so) {
            if (!Scrollable)
                return so;

            if (MaxScrollOffset.HasValue) {
                so.X = Arithmetic.Clamp(so.X, MinScrollOffset.X, MaxScrollOffset.Value.X);
                so.Y = Arithmetic.Clamp(so.Y, MinScrollOffset.Y, MaxScrollOffset.Value.Y);
            } else {
                so.X = Math.Max(MinScrollOffset.X, so.X);
                so.Y = Math.Max(MinScrollOffset.Y, so.Y);
            }
            return so;
        }

        internal override void InvalidateLayout () {
            base.InvalidateLayout();
            foreach (var ch in Children)
                ch.InvalidateLayout();
        }

        protected bool OnScroll (float delta) {
            if (!Scrollable)
                return false;
            if (!ShowVerticalScrollbar || !CanScrollVertically)
                return false;
            var so = ScrollOffset;
            so.Y = so.Y - delta;
            so = ConstrainNewScrollOffset(so);
            if (so != ScrollOffset) {
                TrySetScrollOffset(so, true);
                return true;
            } else {
                return false;
            }
        }

        protected override bool OnEvent<T> (string name, T args) {
            if (name == UIEvents.Scroll)
                return OnScroll(Convert.ToSingle(args));
            else if (args is MouseEventArgs)
                return OnMouseEvent(name, (MouseEventArgs)(object)args);

            return false;
        }

        private bool OnMouseEvent (string name, MouseEventArgs args) {
            var context = Context;
            var scroll = context.Decorations?.Scrollbar;
            if (scroll == null)
                return false;

            var box = GetRect(context.Layout, contentRect: false);
            var contentBox = GetRect(context.Layout, contentRect: true);
            var settings = MakeDecorationSettings(ref box, ref contentBox, default(ControlStates));

            // Ensure the scrollbar state is up-to-date if someone modified our offset
            HScrollbar.Position = ScrollOffset.X;
            VScrollbar.Position = ScrollOffset.Y;

            var hScrollProcessed = ShowHorizontalScrollbar && scroll.OnMouseEvent(settings, ref HScrollbar, name, args);
            var vScrollProcessed = ShowVerticalScrollbar && scroll.OnMouseEvent(settings, ref VScrollbar, name, args);

            TrySetScrollOffset(ConstrainNewScrollOffset(new Vector2(HScrollbar.Position, VScrollbar.Position)), hScrollProcessed || vScrollProcessed);
            // Update the scrollbar state again because we may have clamped the offsets
            HScrollbar.Position = ScrollOffset.X;
            VScrollbar.Position = ScrollOffset.Y;

            return hScrollProcessed || vScrollProcessed;
        }

        protected override void OnDisplayOffsetChanged () {
            AbsoluteDisplayOffsetOfChildren = AbsoluteDisplayOffset - _ScrollOffset.Floor();

            foreach (var child in Children)
                child.AbsoluteDisplayOffset = AbsoluteDisplayOffsetOfChildren;
        }

        protected void GenerateDynamicContent (bool force) {
            if (DynamicContents == null)
                return;

            if (FreezeDynamicContent && !force)
                return;

            if (DynamicBuilder.Container != this)
                DynamicBuilder = new ContainerBuilder(this);
            DynamicBuilder.Reset();
            DynamicContents(ref DynamicBuilder);
            DynamicBuilder.Finish();
        }

        protected override ControlKey OnGenerateLayoutTree (UIOperationContext context, ControlKey parent, ControlKey? existingKey) {
            HasContentBounds = false;
            var result = base.OnGenerateLayoutTree(context, parent, existingKey);
            context.Layout.SetContainerFlags(result, ContainerFlags);

            GenerateDynamicContent(false);

            foreach (var item in Children) {
                item.AbsoluteDisplayOffset = AbsoluteDisplayOffsetOfChildren;

                // If we're performing layout again on an existing layout item, attempt to do the same
                //  for our children
                var childExistingKey = (ControlKey?)null;
                if ((existingKey.HasValue) && !item.LayoutKey.IsInvalid)
                    childExistingKey = item.LayoutKey;

                item.GenerateLayoutTree(ref context, result, childExistingKey);
            }
            return result;
        }

        protected override IDecorator GetDefaultDecorator (IDecorationProvider provider) {
            if (LayoutFlags.IsFlagged(ControlFlags.Layout_Floating))
                return provider?.FloatingContainer ?? provider?.Container;
            else
                return provider?.Container;
        }

        protected override bool ShouldClipContent => ClipChildren && (Children.Count > 0);
        // FIXME: Always true?
        protected override bool HasChildren => (Children.Count > 0);

        protected virtual bool HideChildren => false;

        protected override void OnRasterizeChildren (UIOperationContext context, ref RasterizePassSet passSet, DecorationSettings settings) {
            if (HideChildren)
                return;

            // FIXME
            int layer1 = passSet.Below.Layer,
                layer2 = passSet.Content.Layer,
                layer3 = passSet.Above.Layer,
                maxLayer1 = layer1,
                maxLayer2 = layer2,
                maxLayer3 = layer3;

            var sequence = Children.InPaintOrder();
            foreach (var item in sequence) {
                passSet.Below.Layer = layer1;
                passSet.Content.Layer = layer2;
                passSet.Above.Layer = layer3;

                item.Rasterize(ref context, ref passSet);

                maxLayer1 = Math.Max(maxLayer1, passSet.Below.Layer);
                maxLayer2 = Math.Max(maxLayer2, passSet.Content.Layer);
                maxLayer3 = Math.Max(maxLayer3, passSet.Above.Layer);
            }

            passSet.Below.Layer = maxLayer1;
            passSet.Content.Layer = maxLayer2;
            passSet.Above.Layer = maxLayer3;
        }

        protected override void OnRasterize (UIOperationContext context, ref ImperativeRenderer renderer, DecorationSettings settings, IDecorator decorations) {
            base.OnRasterize(context, ref renderer, settings, decorations);

            if (Scrollable) {
                settings.Box.Top += MostRecentHeaderHeight;
                settings.ContentBox.Top += MostRecentHeaderHeight;
                settings.Box.Height -= MostRecentHeaderHeight;
                settings.ContentBox.Height -= MostRecentHeaderHeight;

                var scrollbar = context.DecorationProvider?.Scrollbar;
                if (ShowHorizontalScrollbar)
                    scrollbar?.Rasterize(context, ref renderer, settings, ref HScrollbar);
                if (ShowVerticalScrollbar)
                    scrollbar?.Rasterize(context, ref renderer, settings, ref VScrollbar);
            }
        }

        protected override Margins ComputePadding (UIOperationContext context, IDecorator decorations) {
            var result = base.ComputePadding(context, decorations);
            if (!Scrollable)
                return result;
            var scrollbar = context.DecorationProvider?.Scrollbar;
            if (scrollbar == null)
                return result;
            if (ShowVerticalScrollbar)
                result.Right += scrollbar.MinimumSize.X;
            if (ShowHorizontalScrollbar)
                result.Bottom += scrollbar.MinimumSize.Y;
            return result;
        }

        protected bool GetContentBounds (UIContext context, out RectF contentBounds) {
            if (LayoutKey.IsInvalid) {
                contentBounds = default(RectF);
                return false;
            }

            var ok = context.Layout.TryMeasureContent(LayoutKey, out contentBounds);
            if (ok)
                ContentBounds = contentBounds;
            HasContentBounds = ok;
            return ok;
        }

        protected override void ApplyClipMargins (UIOperationContext context, ref RectF box) {
            // Scrollbars are already part of computed padding, so just clip out the header (if any)
            box.Top += MostRecentHeaderHeight;
            box.Height -= MostRecentHeaderHeight;
        }

        protected override bool OnHitTest (LayoutContext context, RectF box, Vector2 position, bool acceptsMouseInputOnly, bool acceptsFocusOnly, ref Control result) {
            if (!base.OnHitTest(context, box, position, false, false, ref result))
                return false;

            bool success = AcceptsMouseInput || !acceptsMouseInputOnly;
            // Don't perform child hit-tests if the mouse is over the header
            if (position.Y <= (box.Top + MostRecentHeaderHeight))
                return success;

            // FIXME: Should we only perform the hit test if the position is within our boundaries?
            // This doesn't produce the right outcome when a container's computed size is zero
            var sorted = Children.InPaintOrder();
            for (int i = sorted.Count - 1; i >= 0; i--) {
                var item = sorted[i];
                var newResult = item.HitTest(context, position, acceptsMouseInputOnly, acceptsFocusOnly);
                if (newResult != null) {
                    result = item;
                    success = true;
                }
            }

            return success;
        }

        public T Child<T> (Func<T, bool> predicate)
            where T : Control {

            foreach (var child in Children) {
                if (!(child is T))
                    continue;
                var t = (T)child;
                if (predicate(t))
                    return t;
            }

            return null;
        }

        protected virtual void OnLayoutComplete (UIOperationContext context, ref bool relayoutRequested) {
            // FIXME: This should be done somewhere else
            if (Scrollable) {
                var contentBox = context.Layout.GetContentRect(LayoutKey);
                var scrollbar = context.DecorationProvider?.Scrollbar;
                float viewportWidth = contentBox.Width,
                    viewportHeight = contentBox.Height;

                GetContentBounds(context.UIContext, out RectF contentBounds);

                if (HasContentBounds) {
                    float maxScrollX = ContentBounds.Width - viewportWidth, maxScrollY = ContentBounds.Height - viewportHeight;
                    maxScrollX = Math.Max(0, maxScrollX);
                    maxScrollY = Math.Max(0, maxScrollY);

                    // HACK: Suppress flickering during size transitions
                    if (maxScrollX <= 1)
                        maxScrollX = 0;
                    if (maxScrollY <= 1)
                        maxScrollY = 0;

                    MinScrollOffset = Vector2.Zero;
                    MaxScrollOffset = new Vector2(maxScrollX, maxScrollY);
                    TrySetScrollOffset(DesiredScrollOffset, false);

                    CanScrollVertically = maxScrollY > 0;
                }

                HScrollbar.ContentSize = ContentBounds.Width;
                HScrollbar.ViewportSize = contentBox.Width;
                HScrollbar.Position = ScrollOffset.X;
                VScrollbar.ContentSize = CanScrollVertically ? ContentBounds.Height : contentBox.Height;
                VScrollbar.ViewportSize = contentBox.Height;
                VScrollbar.Position = ScrollOffset.Y;

                HScrollbar.HasCounterpart = VScrollbar.HasCounterpart = (ShowHorizontalScrollbar && ShowVerticalScrollbar);
            } else {
                TrySetScrollOffset(Vector2.Zero, false);
            }
        }

        protected virtual void OnDescendantReceivedFocus (Control control, bool isUserInitiated) {
        }

        void IControlContainer.DescendantReceivedFocus (Control descendant, bool isUserInitiated) {
            OnDescendantReceivedFocus(descendant, isUserInitiated);
        }

        void IPostLayoutListener.OnLayoutComplete (UIOperationContext context, ref bool relayoutRequested) {
            OnLayoutComplete(context, ref relayoutRequested);
        }
    }
}
