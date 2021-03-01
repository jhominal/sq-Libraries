﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Squared.Util;
using Squared.Util.Text;

namespace Squared.PRGUI.Controls {
    public class ItemListManager<T> {
        private class IndexComparer : IRefComparer<int>, IEqualityComparer<int> {
            public static IndexComparer Instance = new IndexComparer();

            public int Compare (ref int lhs, ref int rhs) {
                return lhs.CompareTo(rhs);
            }

            public bool Equals (int lhs, int rhs) {
                return lhs == rhs;
            }

            public int GetHashCode (int value) {
                return value.GetHashCode();
            }
        }

        public event Action SelectionChanged;

        public ItemList<T> Items;
        // FIXME: Handle set
        public IEqualityComparer<T> Comparer;

        private int _SelectionVersion;
        /// <summary>
        /// Don't mutate this! If you do you deserve the problems that will result!
        /// </summary>
        internal DenseList<int> _SelectedIndices;

        private int _MaxSelectedCount = 1;
        public int MaxSelectedCount {
            get => _MaxSelectedCount;
            set {
                if (_MaxSelectedCount == value)
                    return;
                if (value < 0)
                    value = 0;

                _MaxSelectedCount = value;
                // FIXME: Prune extra selected items on change
            }
        }

        public ItemListManager (IEqualityComparer<T> comparer) {
            Comparer = comparer;
            Items = new ItemList<T>(comparer);
            _SelectedIndices.EnsureList();
        }

        public bool HasSelectedItem { get; private set; }

        public int SelectedItemCount => _SelectedIndices.Count;

        public bool IsSelectedIndex (int index) {
            return _SelectedIndices.IndexOf(index, IndexComparer.Instance) >= 0;
        }

        public int IndexOfControl (Control child) {
            if (!Items.GetValueForControl(child, out T value))
                return -1;
            return Items.IndexOf(ref value, Comparer);
        }

        public void Clear () {
            Items.Clear();
            _SelectedIndices.Clear();
            OnSelectionChanged();
            HasSelectedItem = false;
        }

        private void OnSelectionChanged () {
            _SelectedIndices.Sort(IndexComparer.Instance);
            if (SelectionChanged != null)
                SelectionChanged();
        }

        public T SelectedItem {
            get => _SelectedIndices.Count > 0 ? Items[_SelectedIndices[0]] : default(T);
            set {
                if ((_SelectedIndices.Count == 1) && Comparer.Equals(Items[_SelectedIndices[0]], value))
                    return;
                var newIndex = Items.IndexOf(ref value, Comparer);
                if (newIndex < 0)
                    throw new ArgumentException("Item not found in collection", "value");
                _SelectedIndices.Clear();
                _LastGrowDirection = 0;
                _SelectionVersion++;
                if (newIndex >= 0) {
                    HasSelectedItem = true;
                    _SelectedIndices.Add(newIndex);
                }
            }
        }

        public int SelectedIndex {
            get => _SelectedIndices.Count > 0 ? _SelectedIndices[0] : -1;
            set {
                if ((value < 0) && (_SelectedIndices.Count == 0))
                    return;
                if ((_SelectedIndices.Count == 1) && (_SelectedIndices[0] == value))
                    return;
                _SelectedIndices.Clear();
                _SelectionVersion++;
                _LastGrowDirection = 0;
                if (value >= 0) {
                    HasSelectedItem = true;
                    _SelectedIndices.Add(value);
                }
            }
        }

        public Control ControlForIndex (int index) {
            if ((index < 0) || (index >= Items.Count))
                throw new IndexOutOfRangeException();
            var item = Items[index];
            Items.GetControlForValue(ref item, out Control result);
            return result;
        }

        public Control SelectedControl =>
            Items.GetControlForValue(SelectedItem, out Control result)
                ? result
                : null;

        private int _LastGrowDirection = 0;
        public int LastGrowDirection => _LastGrowDirection;

        public int MinSelectedIndex => _SelectedIndices.FirstOrDefault(-1);
        public int MaxSelectedIndex => _SelectedIndices.LastOrDefault(-1);

        public bool TryExpandOrShrinkSelectionToItem (ref T item) {
            var newIndex = Items.IndexOf(ref item, Comparer);
            if (newIndex < 0)
                return false;
            T temp;
            var minIndex = _SelectedIndices.FirstOrDefault();
            var maxIndex = _SelectedIndices.LastOrDefault();
            var insideSelection = (newIndex >= minIndex) && (newIndex <= maxIndex);
            var shrinkDirection = -_LastGrowDirection;
            var shrinking = (_LastGrowDirection != 0) && (
                insideSelection || (shrinkDirection != _LastGrowDirection)
            );

            if (shrinking) {
                if (shrinkDirection == 0)
                    return false;
                else if (shrinkDirection < 0)
                    ConstrainSelection(minIndex, newIndex);
                else
                    ConstrainSelection(newIndex, maxIndex);
            }

            if (!insideSelection) {
                var deltaSign = newIndex < minIndex
                    ? -1
                    : 1;
                int delta = deltaSign < 0
                    ? newIndex - minIndex
                    : newIndex - maxIndex;

                return TryAdjustSelection(delta, out temp, true);
            } else {
                return true;
            }
        }

        public void ConstrainSelection (int minIndex, int maxIndex) {
            for (int i = _SelectedIndices.Count - 1; i >= 0; i--) {
                // FIXME: This should be impossible
                if (i >= _SelectedIndices.Count)
                    break;
                var index = _SelectedIndices[i];
                if ((index >= minIndex) && (index <= maxIndex))
                    continue;
                _SelectedIndices.RemoveAt(i);
            }
        }

        public bool TryAdjustSelection (int delta, out T lastNewItem, bool grow = false) {
            if (delta == 0) {
                lastNewItem = SelectedItem;
                return false;
            }

            if (Items.Count == 0) {
                lastNewItem = default(T);
                SelectedIndex = -1;
                return false;
            }

            var deltaSign = Math.Sign(delta);

            _SelectionVersion++;
            if (_SelectedIndices.Count > 0) {
                int pos = (delta > 0)
                    ? _SelectedIndices.LastOrDefault()
                    : _SelectedIndices.FirstOrDefault(),
                    neg = (delta > 0)
                        ? _SelectedIndices.FirstOrDefault()
                        : _SelectedIndices.LastOrDefault();
                int leadingEdge, newLeadingEdge;

                if (grow) {
                    leadingEdge = pos;
                    newLeadingEdge = pos + delta;
                } else {
                    leadingEdge = neg;
                    newLeadingEdge = neg + delta;
                }
                leadingEdge = Arithmetic.Clamp(leadingEdge, 0, Items.Count - 1);
                newLeadingEdge = Arithmetic.Clamp(newLeadingEdge, 0, Items.Count - 1);

                for (int i = leadingEdge + deltaSign, c = Items.Count; (i != newLeadingEdge) && (i >= 0) && (i < c); i += deltaSign) {
                    if (grow)
                        _SelectedIndices.Add(i);
                    else {
                        var indexOf = _SelectedIndices.IndexOf(i, IndexComparer.Instance);
                        if (indexOf < 0)
                            break;
                        _SelectedIndices.RemoveAt(indexOf);
                    }
                }

                if (grow)
                    _SelectedIndices.Add(newLeadingEdge);
                else {
                    var indexOf = _SelectedIndices.IndexOf(newLeadingEdge, IndexComparer.Instance);
                    if (indexOf >= 0)
                        _SelectedIndices.RemoveAt(indexOf);
                    else
                        ;
                }

                if ((_LastGrowDirection == 0) || grow)
                    _LastGrowDirection = deltaSign;
                OnSelectionChanged();
                lastNewItem = Items[newLeadingEdge];
                return true;
            } else {
                int start = (delta < 0)
                    ? Items.Count - delta
                    : 0;
                int end = (delta < 0)
                    ? Items.Count - 1
                    : delta;
                for (int i = start; i != end; i += deltaSign)
                    _SelectedIndices.Add(i);
                _SelectedIndices.Add(end);
                OnSelectionChanged();
                lastNewItem = Items[end];
                return true;
            }
        }

        public bool TryToggleItemSelected (ref T item) {
            if (Items.Count == 0) {
                SelectedIndex = -1;
                return false;
            }

            _SelectionVersion++;
            var indexOf = Items.IndexOf(ref item, Comparer);
            if (indexOf < 0)
                return false;

            _LastGrowDirection = 0;
            var selectionIndexOf = _SelectedIndices.IndexOf(indexOf, IndexComparer.Instance);
            if (selectionIndexOf < 0)
                _SelectedIndices.Add(indexOf);
            else
                _SelectedIndices.RemoveAt(selectionIndexOf);

            _SelectedIndices.Sort(IndexComparer.Instance);
            OnSelectionChanged();
            return true;
        }

        public bool TrySetSelectedItem (ref T value) {
            var indexOfNewValue = Items.IndexOf(ref value, Comparer);
            if (indexOfNewValue < 0)
                return false;

            if (
                (indexOfNewValue >= 0) &&
                (indexOfNewValue == SelectedIndex) && 
                (_SelectedIndices.Count == 1)
            )
                return true;
            var indexOf = Items.IndexOf(ref value, Comparer);
            if (indexOf < 0)
                return false;
            SelectedIndex = indexOf;
            return true;
        }
    }

    public class ItemList<T> : IEnumerable<T> {
        private List<T> Items = new List<T>();
        private readonly Dictionary<T, Control> ControlForValue;
        private readonly Dictionary<Control, T> ValueForControl =
            new Dictionary<Control, T>(new ReferenceComparer<Control>());

        public ItemList (IEqualityComparer<T> comparer) 
            : base () {
            ControlForValue = new Dictionary<T, Control>(comparer);
        }

        private bool PurgePending;

        public int Version { get; internal set; }
        public int Count => Items.Count;

        public T this[int index] {
            get => Items[index];
            set {
                Items[index] = value;
                Invalidate();
            }
        }

        public Dictionary<Control, T>.KeyCollection Controls => ValueForControl.Keys;

        /// <summary>
        /// Forces all child controls to be re-created from scratch
        /// </summary>
        public void Purge () {
            PurgePending = true;
        }

        /// <summary>
        /// Flags the sequence as having changed so controls will be updated
        /// </summary>
        public void Invalidate () {
            Version++;
        }

        public void Clear () {
            Items.Clear();
            Invalidate();
        }

        public void AddRange (IEnumerable<T> collection) {
            Items.AddRange(collection);
            Invalidate();
        }

        public void Add (T value) {
            Items.Add(value);
            Invalidate();
        }

        public void Add (ref T value) {
            Items.Add(value);
            Invalidate();
        }

        public bool Remove (T value) {
            Invalidate();
            return Items.Remove(value);
        }

        public bool Remove (ref T value) {
            Invalidate();
            return Items.Remove(value);
        }

        public void RemoveAt (int index) {
            Items.RemoveAt(index);
            Invalidate();
        }

        public void Sort (Comparison<T> comparer) {
            Items.Sort(comparer);
            Invalidate();
        }

        public void Sort (IComparer<T> comparer) {
            Items.Sort(comparer);
            Invalidate();
        }

        public bool GetControlForValue (T value, out Control result) {
            if (value == null) {
                result = null;
                return false;
            }
            return ControlForValue.TryGetValue(value, out result);
        }

        public bool GetControlForValue (ref T value, out Control result) {
            if (value == null) {
                result = null;
                return false;
            }
            return ControlForValue.TryGetValue(value, out result);
        }

        public bool GetValueForControl (Control control, out T result) {
            if (control == null) {
                result = default(T);
                return false;
            }
            return ValueForControl.TryGetValue(control, out result);
        }

        public int IndexOf (T value, IEqualityComparer<T> comparer) {
            return IndexOf(ref value, comparer);
        }

        public int IndexOf (ref T value, IEqualityComparer<T> comparer) {
            for (int i = 0, c = Count; i < c; i++)
                if (comparer.Equals(value, this[i]))
                    return i;

            return -1;
        }

        public bool Contains (T value, IEqualityComparer<T> comparer) {
            return Contains(ref value, comparer);
        }

        public bool Contains (ref T value, IEqualityComparer<T> comparer) {
            return IndexOf(ref value, comparer) >= 0;
        }

        private Control CreateControlForValue (
            ref T value, Control existingControl,
            CreateControlForValueDelegate<T> createControlForValue
        ) {
            Control newControl;
            if (value is Control)
                newControl = (Control)(object)value;
            else if (createControlForValue != null)
                newControl = createControlForValue(ref value, existingControl);
            else
                throw new ArgumentNullException("createControlForValue");

            if (value != null)
                ControlForValue[value] = newControl;

            ValueForControl[newControl] = value;
            return newControl;
        }

        public void GenerateControls (
            ControlCollection output, 
            CreateControlForValueDelegate<T> createControlForValue,
            int offset = 0, int count = int.MaxValue
        ) {
            // FIXME: This is inefficient, it would be cool to reuse existing controls
            //  even if the order of values changes
            ControlForValue.Clear();

            count = Math.Min(Count, count);
            if (offset < 0) {
                count += offset;
                offset = 0;
            }

            count = Math.Max(Math.Min(Count - offset, count), 0);

            while (output.Count > count) {
                var ctl = output[output.Count - 1];
                if (ValueForControl.TryGetValue(ctl, out T temp))
                    ControlForValue.Remove(temp);
                ValueForControl.Remove(ctl);
                output.RemoveAt(output.Count - 1);
            }

            for (int i = 0; i < count; i++) {
                var value = this[i + offset];
                // FIXME
                if (value == null)
                    continue;

                var existingControl = ((i < output.Count) && !PurgePending)
                    ? output[i]
                    : null;

                var newControl = CreateControlForValue(ref value, existingControl, createControlForValue);

                if (newControl != existingControl) {
                    if (i < output.Count)
                        output[i] = newControl;
                    else
                        output.Add(newControl);
                }
            }

            PurgePending = false;
        }

        public IEnumerator<T> GetEnumerator () {
            return ((IEnumerable<T>)Items).GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator () {
            return ((IEnumerable<T>)Items).GetEnumerator();
        }
    }
}
