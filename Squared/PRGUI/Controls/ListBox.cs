﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Squared.PRGUI.Decorations;
using Squared.PRGUI.Layout;
using Squared.Render.Convenience;
using Squared.Threading;
using Squared.Util;
using Squared.Util.Text;

namespace Squared.PRGUI.Controls {
    public interface IListBox {
        int Count { get; }
        int SelectedIndex { get; set; }
        object SelectedItem { get; set; }
        bool Virtual { get; set; }
    }

    public enum ListBoxEventMode {
        /// <summary>
        /// The list will consume all events that would normally go to its children
        /// </summary>
        Consume = 0,
        /// <summary>
        /// The list's currently selected item is allowed to receive events
        /// </summary>
        PassThroughIfSelected = 1,
        /// <summary>
        /// The list's items are allowed to receive events
        /// </summary>
        PassThrough = 2
    }

    public class ListBox<T> : 
        Container, ICustomTooltipTarget, Accessibility.IReadingTarget, Accessibility.IAcceleratorSource, 
        IValueControl<T>, ISelectionBearer, IListBox,
        IPartiallyIntangibleControl, IFuzzyHitTestTarget, IHasDescription
    {
        public ControlAppearance DefaultChildAppearance;

        public ListBoxEventMode EventMode = ListBoxEventMode.Consume;

        public bool DisableItemHitTests = true;
        public bool DefaultToggleOnClick = false;

        public bool SelectOnMouseDown = false;

        public float ItemSpacing = 1;

        public const int MaxItemScrollHeight = 40;
        public const int ControlMinimumHeight = 75, ControlMinimumWidth = 150;

        protected ItemListManager<T> Manager;

        public IEqualityComparer<T> Comparer {
            get => Manager.Comparer;
            set => Manager.Comparer = value;
        }
        public ItemList<T> Items => Manager.Items;

        int IListBox.Count => Items.Count;
        object IListBox.SelectedItem {
            get => SelectedItem;
            set => SelectedItem = (T)value;
        }

        private CreateControlForValueDelegate<T> _CreateControlForValue,
            DefaultCreateControlForValue;
        public Func<T, AbstractString> FormatValue = null;

        public CreateControlForValueDelegate<T> CreateControlForValue {
            get => _CreateControlForValue;
            set {
                if (_CreateControlForValue == value)
                    return;
                _CreateControlForValue = value;
                NeedsUpdate = true;
            }
        }

        new public float ScrollOffset => base.ScrollOffset.Y;
        new public float? MaxScrollOffset => base.MaxScrollOffset?.Y;

        public const float AutoscrollMarginSize = 24f;

        public string Description { get; set; }

        public int SelectedCount => Manager.SelectedItemCount;

        public int SelectedIndex {
            get => Manager.SelectedIndex;
            set => Manager.SelectedIndex = value;
        }

        private bool SelectedItemHasChangedSinceLastUpdate = true,
            SelectionChangeEventPending = false;

        public T SelectedItem {
            get => Manager.SelectedItem;
            set {
                SetSelectedItem(value, false);
            }
        }

        bool ISelectionBearer.HasSelection => Manager.SelectedIndex >= 0;
        // FIXME: We should expand the width here
        RectF? ISelectionBearer.SelectionRect => Manager.SelectedControl?.GetRect();
        Control ISelectionBearer.SelectedControl => Manager.SelectedControl;

        private bool _EnableSelect = true;
        public bool EnableSelect {
            get => _EnableSelect && (MaxSelectedCount > 0);
            set {
                _EnableSelect = value;
                if (!value)
                    Manager.ClearSelection();
                NeedsUpdate = true;
            }
        }

        public int MaxSelectedCount {
            get => Manager.MaxSelectedCount;
            set {
                Manager.MaxSelectedCount = value;
                if (value <= 0)
                    Manager.ClearSelection();
                NeedsUpdate = true;
            }
        }

        private bool _Virtual = false;
        public bool Virtual {
            get => _Virtual;
            set {
                if (_Virtual == value)
                    return;

                Children.Clear();
                NeedsUpdate = true;
                _Virtual = value;
            }
        }

        private bool MouseOverItemNeedsUpdate = false;
        private Vector2 LastMouseOverPosition;
        public Control MouseOverItem { get; private set; }

        public bool GenerateControlsWhenHidden = true;

        private int VirtualItemOffset = 0;
        private float VirtualItemHeight = 1; // HACK, will be adjusted each frame
        private int VirtualViewportItemCount = 2; // HACK, will be adjusted up/down each frame

        private int _Version;
        private bool NeedsUpdate = true;

        protected int PageSize { get; private set; }

        new public int ColumnCount {
            get => base.ColumnCount;
            set {
                if (value < 1)
                    throw new ArgumentOutOfRangeException("value");
                base.ColumnCount = value;
            }
        }

        public ListBox ()
            : this (null) {
        }

        public ListBox (IEqualityComparer<T> comparer = null) 
            : base () {
            PageSize = 1;
            AllowDynamicContent = false;
            AcceptsMouseInput = true;
            AcceptsFocus = true;
            ContainerFlags = ControlFlags.Container_Column | ControlFlags.Container_Align_Start;
            ClipChildren = true;
            ShowHorizontalScrollbar = false;
            // HACK: Most lists will contain enough items to need scrolling, so just always show the bar
            ShowVerticalScrollbar = true;
            Scrollable = true;
            // FIXME
            Manager = new ItemListManager<T>(comparer ?? EqualityComparer<T>.Default);
            DefaultCreateControlForValue = _DefaultCreateControlForValue;
            Manager.SelectionChanged += Manager_SelectionChanged;
        }

        private void Manager_SelectionChanged () {
            OnSelectionChanged(true);
        }

        private void OnSelectionChanged (bool forUserInput) {
            DesiredScrollOffset = null;
            SelectedItemHasChangedSinceLastUpdate = true;
            // FIXME: Should we defer this?
            FireEvent(UIEvents.ValueChanged, SelectedItem);
            FireEvent(UIEvents.SelectionChanged, SelectedItem);
            if (forUserInput)
                FireEvent(UIEvents.ValueChangedByUser, SelectedItem);
        }

        public bool TryGrowSelectedItems (int delta, bool forUserInput) {
            if (!Manager.TryResizeSelection(delta, out T temp, true))
                return false;
            OnSelectionChanged(forUserInput);
            return true;
        }

        public bool TryExpandOrShrinkSelectionToItem (ref T value, bool fireEvent) {
            if (!Manager.TryExpandOrShrinkSelectionToItem(ref value, fireEvent))
                return false;
            OnSelectionChanged(fireEvent);
            return true;
        }

        public bool TryToggleItemSelected (ref T value, bool fireEvent) {
            if (!Manager.TryToggleItemSelected(ref value, fireEvent))
                return false;
            OnSelectionChanged(fireEvent);
            return true;
        }

        public bool SetSelectedIndex (int index, bool fireEvent) {
            if (!Manager.TrySetSelectedIndex(index, fireEvent))
                return false;
            OnSelectionChanged(fireEvent);
            return true;
        }

        public bool SetSelectedItem (T value, bool fireEvent) {
            if (!Manager.TrySetSelectedItem(ref value, fireEvent))
                return false;
            OnSelectionChanged(fireEvent);
            return true;
        }

        private Control _DefaultCreateControlForValue (ref T value, Control existingControl) {
            var st = (existingControl as StaticText) ?? new StaticText();
            var text =
                (FormatValue != null)
                    ? FormatValue(value)
                    : value.ToString();
            st.Appearance = DefaultChildAppearance;
            st.Text = text;
            st.Wrap = false;
            st.Multiline = false;
            st.AutoSizeWidth = false;
            st.Data.Set<T>(ref value);
            return st;
        }

        protected override void ComputeSizeConstraints (ref UIOperationContext context, ref ControlDimension width, ref ControlDimension height, Vector2 sizeScale) {
            base.ComputeSizeConstraints(ref context, ref width, ref height, sizeScale);
            if (width.Minimum.HasValue)
                width.Minimum = Math.Max(width.Minimum.Value, ControlMinimumWidth * sizeScale.X);
            else
                width.Minimum = ControlMinimumWidth * sizeScale.X;
            if (height.Minimum.HasValue)
                height.Minimum = Math.Max(height.Minimum.Value, ControlMinimumHeight * sizeScale.Y);
            else
                height.Minimum = ControlMinimumHeight * sizeScale.Y;
        }

        protected override IDecorator GetDefaultDecorator (IDecorationProvider provider) {
            return provider?.ListBox;
        }

        private int EffectiveCount => ((Items.Count + ColumnCount - 1) / ColumnCount) * ColumnCount;
        private float VirtualYDivider => VirtualItemHeight / ColumnCount;
        private float VirtualYMultiplier => VirtualItemHeight / ColumnCount;
        private int LastColumnCount = 0;

        protected override ControlKey OnGenerateLayoutTree (ref UIOperationContext context, ControlKey parent, ControlKey? existingKey) {
            bool scrollOffsetChanged = false;

            // HACK: Ensure the scroll region is updated immediately if our column count changes,
            //  because otherwise the scroll offset can end up beyond the bottom of our view
            if ((LastColumnCount != ColumnCount) && !existingKey.HasValue) {
                LastColumnCount = ColumnCount;
                NeedsUpdate = true;
            }

            if (Virtual) {
                var selectedIndex = SelectedIndex;

                while (true) {
                    var newItemOffset = (Math.Max((int)(base.ScrollOffset.Y / VirtualYDivider) - 1, 0) / ColumnCount) * ColumnCount;
                    var newEndItemOffset = Math.Min(newItemOffset + VirtualViewportItemCount, Items.Count - 1);

                    int delta = 0;
                    if (selectedIndex >= 0) {
                        // HACK: We need to offset by more than 1 because of the virtual viewport padding
                        if (selectedIndex < newItemOffset)
                            delta = -(newItemOffset - selectedIndex) - 2;
                        else if (selectedIndex >= newEndItemOffset)
                            delta = (selectedIndex - newEndItemOffset) + 4;
                    }

                    if (
                        SelectedItemHasChangedSinceLastUpdate && 
                        (delta != 0) && !scrollOffsetChanged
                    ) {
                        if (delta != 0) {
                            var newOffset = base.ScrollOffset;
                            newOffset.Y += (delta * VirtualYMultiplier);
                            if (TrySetScrollOffset(newOffset, false)) {
                                scrollOffsetChanged = true;
                                continue;
                            }
                        }
                    }

                    if (newItemOffset != VirtualItemOffset) {
                        VirtualItemOffset = newItemOffset;
                        NeedsUpdate = true;
                    }
                    var newScrollOffset = -VirtualItemOffset * VirtualYMultiplier;
                    if (newScrollOffset != VirtualScrollOffset.Y) {
                        VirtualScrollOffset.Y = newScrollOffset;
                        scrollOffsetChanged = true;
                        NeedsUpdate = true;
                    }

                    break;
                }
            } else {
                if (VirtualScrollOffset != Vector2.Zero) {
                    scrollOffsetChanged = true;
                    VirtualScrollOffset = Vector2.Zero;
                }
                VirtualScrollRegion = Vector2.Zero;
                NeedsUpdate |= (Items.Count != Children.Count);
            }

            if (Items.Version != _Version)
                NeedsUpdate = true;

            bool hadKeyboardSelection = false;
            var oldKeyboardSelection = Context.KeyboardSelection;
            var generatingEnabled = GenerateControlsWhenHidden || hadKeyboardSelection || !IsRecursivelyTransparent(this, true);
            if (NeedsUpdate && !existingKey.HasValue) {
                hadKeyboardSelection = Children.Contains(Context.KeyboardSelection);
                var priorControl = Manager.SelectedControl;
                // FIXME: Why do virtual list items flicker for a frame before appearing?
                if (generatingEnabled) {
                    Items.GenerateControls(
                        Children, CreateControlForValue ?? DefaultCreateControlForValue,
                        offset: Virtual ? VirtualItemOffset : 0, count: Virtual ? VirtualViewportItemCount : int.MaxValue
                    );
                    _Version = Items.Version;
                    // HACK: Without doing this, old content bounds can be kept that are too big/too small
                    HasContentBounds = false;
                    NeedsUpdate = false;
                    MouseOverItemNeedsUpdate = true;
                }
            } else if (generatingEnabled) {
                Items.GenerateInvalidatedControls(CreateControlForValue ?? DefaultCreateControlForValue);
                MouseOverItemNeedsUpdate = true;
            }

            if (SelectedItemHasChangedSinceLastUpdate || NeedsUpdate || hadKeyboardSelection)
                SelectionChangeEventPending = true;

            SelectedItemHasChangedSinceLastUpdate = false;

            if (scrollOffsetChanged)
                OnDisplayOffsetChanged();

            var result = base.OnGenerateLayoutTree(ref context, parent, existingKey);
            if (result.IsInvalid)
                return result;

            context.Layout.SetTag(result, LayoutTags.ListBox);

            var hasPushedDecorator = false;
            var children = Children;
            // FIXME: This is really slow
            for (int i = 0, c = children.Count; i < c; i++) {
                var child = children[i];
                var lk = child.LayoutKey;
                var childIndex = Manager.IndexOfControl(child);
                var isSelected = Manager.IsSelectedIndex(childIndex);
                SetTextDecorator(ref context, child, isSelected, ref hasPushedDecorator);
                var m = context.Layout.GetMargins(lk);
                // HACK: Override decorator margins
                m.Top = child.Margins.Top;
                m.Bottom = child.Margins.Bottom + ItemSpacing;
                context.Layout.SetMargins(lk, m);
            }

            if (hasPushedDecorator)
                UIOperationContext.PopTextDecorator(ref context);

            // FIXME: This is gross
            if (!existingKey.HasValue && SelectionChangeEventPending) {
                // FIXME: overlap with OnSelectionChanged
                SelectionChangeEventPending = false;
                var newControl = Manager.SelectedControl;
                if (hadKeyboardSelection && (Context.KeyboardSelection == oldKeyboardSelection) && (Context.Focused == this))
                    Context.OverrideKeyboardSelection(newControl, forUser: false);
            }

            return result;
        }

        private void SetTextDecorator (ref UIOperationContext context, Control child, bool isSelected, ref bool hasPushed) {
            if (hasPushed) {
                UIOperationContext.PopTextDecorator(ref context);
                hasPushed = false;
            }
            if (isSelected && !child.Appearance.HasBackgroundColor) {
                UIOperationContext.PushTextDecorator(ref context, Context?.Decorations.Selection);
                hasPushed = true;
            }
        }

        protected override void OnLayoutComplete (ref UIOperationContext context, ref bool relayoutRequested) {
            base.OnLayoutComplete(ref context, ref relayoutRequested);

            var children = Children;
            if (children.Count > 0) {
                float h = 0;
                // HACK: Measure a few items to produce a better height estimate
                for (int i = 0, c = Math.Min(children.Count, 4); i < c; i++)
                    h = Math.Max(h, children[i].GetRect(applyOffset: false).Height);
                VirtualItemHeight = h;
                // HACK: Traditional listboxes on windows scroll multiple item(s) at a time on mousewheel
                //  instead of scrolling on a per-pixel basis
                ScrollSpeedMultiplier = (Math.Min(VirtualItemHeight, MaxItemScrollHeight) / 14);
            }

            var box = GetRect(applyOffset: false, contentRect: true);
            var newViewportItemCount = (int)Math.Ceiling(box.Height / VirtualYDivider) + 4;
            newViewportItemCount = Math.Max(newViewportItemCount, 8);
            if (newViewportItemCount != VirtualViewportItemCount) {
                VirtualViewportItemCount = newViewportItemCount;
                if (Virtual)
                    NeedsUpdate = true;
                // Doing this can cause nonstop jittering
                // relayoutRequested = true;
            }
            // FIXME: It is beyond me why this is the correct value. What?????
            var partialItemScrollOffset = GetDecorator(context.DecorationProvider, null)?.Margins.Y ?? 0;
            VirtualScrollRegion.Y = (EffectiveCount * VirtualYMultiplier);
            if (Virtual)
                VirtualScrollRegion.Y += partialItemScrollOffset;

            if (MouseOverItemNeedsUpdate) {
                MouseOverItemNeedsUpdate = false;
                MouseOverItem = ChildFromGlobalPosition(context.Layout, LastMouseOverPosition);
            }
        }

        public void Invalidate (T item) {
            if (item == null)
                return;

            NeedsUpdate = true;
            _Version++;
            Items.Invalidate(item);
        }

        public void Invalidate (bool purge = true) {
            NeedsUpdate = true;
            _Version++;
            if (purge)
                Items.Purge();

            foreach (var child in Children)
                child.InvalidateLayout();
        }

        // HACK
        private bool _OverrideHitTestResults = true;

        protected override bool OnHitTest (RectF box, Vector2 position, ref HitTestState state) {
            var temp = state;
            if (_OverrideHitTestResults) {
                temp.Options.AcceptsMouseInput = true;
                temp.Options.AcceptsFocus = true;
                temp.Options.RejectIntangible = true;
            }

            var ok = base.OnHitTest(box, position, ref temp);
            // HACK: Ensure that hit-test does not pass through to our individual items. We want to handle all events for them
            if (ok && _OverrideHitTestResults && DisableItemHitTests)
                state.Result = this;
            else
                state.Result = temp.Result;

            return ok;
        }

        private Control LocateContainingChild (Control control) {
            var current = control;
            while (current != null) {
                if (!current.TryGetParent(out Control parent))
                    return null;
                if (parent == this)
                    return current;
                else
                    current = parent;
            }

            return null;
        }

        private Control ChildFromGlobalPosition (LayoutContext context, Vector2 globalPosition) {
            try {
                // Console.WriteLine($"Hovering = {Context.Hovering}, MouseOver = {Context.MouseOver}");

                _OverrideHitTestResults = false;

                var rect = this.GetRect(contentRect: true);
                if (!rect.Contains(globalPosition))
                    return null;

                var columnWidth = rect.Width / ColumnCount;
                for (int i = 0; i < ColumnCount; i++) {
                    var columnX = (rect.Left + (i * columnWidth));
                    var outOfColumn = (globalPosition.X < columnX) || (globalPosition.X >= (columnX + columnWidth));
                    if (outOfColumn) {
                        // Console.WriteLine($"Mouse position outside of column {i}");
                        continue;
                    }

                    Control child = HitTest(globalPosition);
                    if ((child == this) || (child == null)) {
                        foreach (var c in Children) {
                            if (c.GetRect().Contains(globalPosition)) {
                                child = c;
                                break;
                            }
                        }
                    }

                    if ((child == this) || (child == null))
                        continue;
                    else {
                        var result = LocateContainingChild(child);
                        if (result != null) {
                            // Console.WriteLine($"LocateContainingChild for {child} returned {result}");
                            return result;
                        }
                    }
                }

                // Console.WriteLine($"ChildFromGlobalPosition returning null");
                return null;
            } finally {
                _OverrideHitTestResults = true;
            }
        }

        private bool OnMouseEvent (string name, MouseEventArgs args) {
            if (ProcessMouseEventForScrollbar(name, args))
                return true;

            Items.GetControlForValue(SelectedItem, out Control selectedControl);
            var control = ChildFromGlobalPosition(Context.Layout, args.RelativeGlobalPosition);
            LastMouseOverPosition = args.RelativeGlobalPosition;
            MouseOverItem = control;
            // Console.WriteLine($"ChildFromGlobalPosition == {control}");
            if ((args.Buttons == MouseButtons.Right) || (args.PreviousButtons == MouseButtons.Right)) {
                // HACK: Enable passing right-click events through so items can have context menus
                Context.FireEvent(name, control, args);
            } else if (
                ((name == UIEvents.MouseDown) && SelectOnMouseDown) ||
                ((name == UIEvents.Click) && !args.IsSynthetic)
            ) {
                // FIXME: If we handle Click then drag-to-scroll won't select an item,
                //  but having it not select on mousedown feels bad
                if (
                    args.Box.Contains(args.RelativeGlobalPosition) && 
                    Items.GetValueForControl(control, out T newItem)
                ) {
                    // Console.WriteLine($"Selection valid for item {newItem}");
                    var isClick = (name == UIEvents.Click);
                    if (isClick && (!EnableSelect || (control == Manager.SelectedControl))) {
                        return Context.FireEvent(name, control, args);
                    }

                    if (EnableSelect) {
                        if (args.Modifiers.Shift && (MaxSelectedCount > 1))
                            TryExpandOrShrinkSelectionToItem(ref newItem, true);
                        else if ((args.Modifiers.Control == !DefaultToggleOnClick) && (MaxSelectedCount > 1))
                            TryToggleItemSelected(ref newItem, true);
                        else
                            SetSelectedItem(newItem, true);
                    }

                    if (
                        (EventMode == ListBoxEventMode.PassThrough) ||
                        ((EventMode == ListBoxEventMode.PassThroughIfSelected) && (selectedControl == control))
                    )
                        return Context.FireEvent(name, control, args) || isClick;
                    else
                        return isClick;
                } else {
                    if (
                        (EventMode == ListBoxEventMode.PassThrough) ||
                        ((EventMode == ListBoxEventMode.PassThroughIfSelected) && (selectedControl == control))
                    )
                        return Context.FireEvent(name, control, args);
                    else
                        return false;
                }
            } else {
                if (
                    (EventMode != ListBoxEventMode.Consume)
                )
                    return Context.FireEvent(name, control, args);
                else
                    return false;
            }

            // Console.WriteLine($"Discarding event");
            return false;
        }

        protected override bool OnEvent<TArgs> (string name, TArgs args) {
            if (args is MouseEventArgs ma)
                return OnMouseEvent(name, ma);
            else if (args is KeyEventArgs ka)
                return OnKeyEvent(name, ka);
            else
                return base.OnEvent(name, args);
        }

        private void UpdateKeyboardSelection (T item, bool forUser) {
            // HACK: Tell the context that the current item is the keyboard selection,
            //  so that autoscroll and tooltips will happen for it.
            if (Context.Focused == this)
                Context.OverrideKeyboardSelection(Manager.SelectedControl, forUser);
        }

        private void SelectItemViaKeyboard (T item) {
            if (!EnableSelect)
                return;
            SetSelectedItem(item, true);
            UpdateKeyboardSelection(item, true);
        }

        public bool AdjustSelection (int delta, bool grow, bool shrink, bool forUser) {
            if (delta == 0)
                return false;

            bool result = false, oneItemSelected = (Manager.MinSelectedIndex == Manager.MaxSelectedIndex);
            T newItem = default(T);
            if (grow || (oneItemSelected && shrink)) {
                result = Manager.TryResizeSelection(delta, out newItem, true);
                if (result == false)
                    ;
            } else if (shrink) {
                int min = Manager.MinSelectedIndex, max = Manager.MaxSelectedIndex;
                if (delta > 0) {
                    Manager.ConstrainSelection(min + delta, max);
                    Manager.MostRecentItemInteractedWith = Manager.MaxSelectedIndex;
                    if (Manager.MaxSelectedIndex >= 0)
                        newItem = Manager.Items[Manager.MaxSelectedIndex];
                } else {
                    Manager.ConstrainSelection(min, max + delta);
                    Manager.MostRecentItemInteractedWith = Manager.MinSelectedIndex;
                    if (Manager.MinSelectedIndex >= 0)
                        newItem = Manager.Items[Manager.MinSelectedIndex];
                }
                result = true;
            } else if (Manager.TryMoveSelection(delta, true)) {
                UpdateKeyboardSelection(newItem, forUser);
                newItem = SelectedItem;
                result = true;
            } else {
                ;
            }

            return result;
        }

        private bool OnKeyEvent (string name, KeyEventArgs args) {
            if (args.Key != null && UIContext.ModifierKeys.Contains(args.Key.Value))
                return false;
            if (name != UIEvents.KeyPress)
                return true;

            var upward = (args.Key == Keys.Up) ||
                (args.Key == Keys.PageUp) ||
                (args.Key == Keys.Left) ||
                (args.Key == Keys.Home);
            var indexDirection = upward ? -1 : 1;
            var shifted = args.Modifiers.Shift;
            var growing = 
                (MaxSelectedCount > 1) &&
                shifted && (
                    (Manager.LastGrowDirection != -indexDirection) ||
                    (Manager.MinSelectedIndex == Manager.MaxSelectedIndex)
                );
            var shrinking = shifted && !growing;

            int delta;

            // FIXME: Autoscroll doesn't work anymore?
            switch (args.Key) {
                case Keys.Home:
                case Keys.End:
                    delta = Items.Count * indexDirection;
                    break;
                case Keys.PageUp:
                case Keys.PageDown:
                    delta = PageSize * indexDirection;
                    break;
                case Keys.Left:
                case Keys.Right:
                    delta = indexDirection;
                    break;
                case Keys.Up:
                case Keys.Down:
                    delta = ColumnCount * indexDirection;
                    break;
                case Keys.A:
                    if (args.Modifiers.Control)
                        SelectAll();
                    return true;
                default: {
                    if (
                        Items.GetControlForValue(SelectedItem, out Control selectedControl) &&
                        (EventMode != ListBoxEventMode.Consume)
                    )
                        return Context.FireEvent(name, selectedControl, args);
                    else
                        return false;
                }
            }

            // FIXME: Control-shift
            if (args.Modifiers.Control && (MaxSelectedCount > 1))
                return Manager.TryToggleInDirection(delta, true);
            else
                return AdjustSelection(delta, growing, shrinking, true);
        }

        public void SelectAll () {
            Manager.SelectAll();
        }

        public void SelectNone () {
            SetSelectedIndex(-1, true);
        }

        protected override void OnRasterizeChildren (ref UIOperationContext context, ref RasterizePassSet passSet, DecorationSettings settings) {
            var selectionDecorator = context.DecorationProvider.ListSelection;
            if (selectionDecorator != null) {
                var oldPass = context.Pass;
                context.Pass = RasterizePasses.Content;

                RectF parentBox, selectionBox;
                foreach (var index in Manager._SelectedIndices) {
                    if (index >= Items.Count)
                        continue;

                    var selectedControl = Manager.ControlForIndex(index);

                    // FIXME: Figure out when to break
                    if (selectedControl == null)
                        continue;

                    var parentColumn = context.Layout.GetParent(selectedControl.LayoutKey);
                    context.Layout.TryGetContentRect(parentColumn, out parentBox);
                    selectionBox = selectedControl.GetRect();
                    selectionBox.Top += selectionDecorator.Margins.Top;
                    selectionBox.Left = parentBox.Left + selectionDecorator.Margins.Left;
                    selectionBox.Height -= selectionDecorator.Margins.Y;
                    selectionBox.Width = parentBox.Width - selectionDecorator.Margins.X;

                    // HACK: Selection boxes are normally rasterized on the content layer, but we want to rasterize
                    //  the selection on the Below layer beneath items' decorations and content.
                    var selectionSettings = new DecorationSettings {
                        Box = selectionBox,
                        ContentBox = selectionBox,
                        State = settings.State
                    };
                    // FIXME
                    selectionDecorator.Rasterize(ref context, ref passSet.Below, ref selectionSettings);
                }
                context.Pass = oldPass;
                passSet.Below.Layer += 1;
            }

            base.OnRasterizeChildren(ref context, ref passSet, settings);
        }

        private int lastOffset1 = -1,
            lastOffset2 = -1;

        protected override bool RasterizeChild (
            ref UIOperationContext context, Control control, ref RasterizePassSet passSet
        ) {
            var itemIndex = Manager.IndexOfControl(control);
            var isSelected = Manager.IsSelectedIndex(itemIndex);
            bool hasPushed = false;
            SetTextDecorator(ref context, control, isSelected, ref hasPushed);
            var result = base.RasterizeChild(
                ref context, control, ref passSet
            );
            if (hasPushed)
                UIOperationContext.PopTextDecorator(ref context);
            return result;
        }

        protected override void RasterizeChildrenInOrder (
            ref UIOperationContext context, ref RasterizePassSet passSet
        ) {
            if (Virtual) {
                base.RasterizeChildrenInOrder(
                    ref context, ref passSet
                );
                PageSize = Math.Max(VirtualViewportItemCount - 4, 2);
            } else {
                var selectedControl = Manager.SelectedControl;
                var displayPageSize = RasterizeChildrenFromCenter(
                    ref context, ref passSet, 
                    GetRect(), selectedControl,
                    ref lastOffset1, ref lastOffset2
                );

                // FIXME: If we're partially offscreen this value will be too small
                PageSize = Math.Max(1, displayPageSize / 2);
            }
        }

        private void CalculateScrollable (UIContext context) {
            context.UpdateSubtreeLayout(this);
            if (GetContentBounds(context, out Vector2 contentBounds))
                Scrollable = (contentBounds.Y >= Height.Maximum) || (Height.Maximum == null);
        }

        StringBuilder TextBuilder = new StringBuilder();

        AbstractString Accessibility.IReadingTarget.Text {
            get {
                TextBuilder.Clear();
                if (Description != null)
                    TextBuilder.Append(Description);
                else {
                    var ttc = TooltipContent.GetPlainText(this).ToString();
                    if (ttc != null)
                        TextBuilder.Append(ttc);
                }

                var irt = (Manager.SelectedControl as Accessibility.IReadingTarget) ??
                    (SelectedItem as Accessibility.IReadingTarget);

                if (irt != null) {
                    TextBuilder.Append(": ");
                    var existingLength = TextBuilder.Length;
                    irt.FormatValueInto(TextBuilder);
                    if (TextBuilder.Length == existingLength)
                        TextBuilder.Append(irt.Text.ToString());
                } else {
                    // FIXME: Fallback to something else here?
                }

                return TextBuilder;
            }
        }

        void Accessibility.IReadingTarget.FormatValueInto (StringBuilder sb) {
            if (SelectedItem == null)
                return;

            var irt = (Manager.SelectedControl as Accessibility.IReadingTarget) ??
                (SelectedItem as Accessibility.IReadingTarget);

            if (irt != null) {
                irt.FormatValueInto(sb);
                if (sb.Length == 0)
                    sb.Append(irt.Text);
            } else {
                sb.Append(SelectedItem.ToString());
            }
        }

        private Accessibility.AcceleratorInfo? GetInfoForIndex (int index, Keys key) {
            if ((index < 0) || (index >= Items.Count))
                return null;

            if (!Items.GetControlForValue(Items[index], out Control control))
                return null;
            return new Accessibility.AcceleratorInfo(control, key);
        }

        IEnumerable<Accessibility.AcceleratorInfo> Accessibility.IAcceleratorSource.Accelerators {
            get {
                var si = SelectedIndex;
                var multiColumn = (ColumnCount > 1);
                if (multiColumn) {
                    var a = GetInfoForIndex(si - 1, Keys.Left);
                    if (a != null)
                        yield return a.Value;
                    a = GetInfoForIndex(si + 1, Keys.Right);
                    if (a != null)
                        yield return a.Value;
                    a = GetInfoForIndex(si - ColumnCount, Keys.Up);
                    if (a != null)
                        yield return a.Value;
                    a = GetInfoForIndex(si + ColumnCount, Keys.Down);
                    if (a != null)
                        yield return a.Value;
                } else {
                    var a = GetInfoForIndex(si - 1, Keys.Up);
                    if (a != null)
                        yield return a.Value;
                    a = GetInfoForIndex(si + 1, Keys.Down);
                    if (a != null)
                        yield return a.Value;
                }
            }
        }

        T IValueControl<T>.Value {
            get => SelectedItem;
            set => SelectedItem = value;
        }

        public int Count => Items.Count;

        public T this [int index] {
            get => Items[index];
            set {
                Items[index] = value;
                Invalidate();
            }
        }

        public void RemoveAt (int index) {
            Items.RemoveAt(index);
            Invalidate();
        }

        public void Clear (bool invalidateControls = false) {
            Items.Clear(invalidateControls);
            Invalidate();
        }

        public void Add (T value) {
            Items.Add(value);
            Invalidate();
        }

        public bool TryScrollSelectedItemIntoView (bool forUserInput) {
            var ctl = Manager.SelectedControl;
            if (ctl == null)
                return false;

            var parentRect = GetRect(applyOffset: false);
            var itemRect = ctl.GetRect(applyOffset: false, true);
            itemRect.Position -= parentRect.Position;
            var desiredOffset = itemRect.Center - (parentRect.Size * 0.5f);
            return TrySetScrollOffset(desiredOffset, forUserInput);
        }

        bool IPartiallyIntangibleControl.IsIntangibleAtPosition (Vector2 position) => false;

        int IFuzzyHitTestTarget.WalkTree (
            List<FuzzyHitTest.Result> output, ref FuzzyHitTest.Result thisControl, Vector2 position, Func<Control, bool> predicate, float maxDistanceSquared
        ) => 0;
        bool IFuzzyHitTestTarget.WalkChildren => false;

        protected void DetermineTooltip (out Control target, out AbstractTooltipContent result) {
            // FIXME: Cache this for efficiency
            var deep = MouseOverItem?.HitTest(LastMouseOverPosition, new HitTestOptions { RejectIntangible = true });
            var ttc = deep?.TooltipContent;
            if ((ttc ?? default(AbstractTooltipContent)) != default(AbstractTooltipContent)) {
                target = deep;
                result = ttc.Value;
            } else {
                if ((MouseOverItem != null) && (MouseOverItem.TooltipContent != default(AbstractTooltipContent)))
                    target = MouseOverItem;
                else
                    target = this;
                result = MouseOverItem?.TooltipContent ?? TooltipContent;
            }
            // FIXME: What is this actually doing?
            _TooltipSettings.AppearDelay = TooltipContent.Equals(result) &&
                (MouseOverItem != null) &&
                !MouseOverItem.TooltipContent.Equals(default)
                    ? 0f
                    : (float?)null;
        }

        AbstractTooltipContent ICustomTooltipTarget.GetContent () {
            DetermineTooltip(out Control target, out AbstractTooltipContent content);
            return content;
        }

        TooltipTargetSettings _TooltipSettings = new TooltipTargetSettings {
            ShowWhileFocused = false,
            ShowWhileMouseIsHeld = false,
            ShowWhileMouseIsNotHeld = true,
            ShowWhileKeyboardFocused = true,
            HideOnMousePress = true,
        };

        TooltipTargetSettings ICustomTooltipTarget.TooltipSettings => _TooltipSettings;
        Control ICustomTooltipTarget.Anchor {
            get {
                DetermineTooltip(out Control target, out AbstractTooltipContent content);
                return target ?? MouseOverItem;
            }
        }
    }
}
