﻿/*
 * Contents of this file derived from layout.h (https://github.com/randrew/layout), which was in turn
 *  derived from oui/blendish. This file is as such under the same license as those two single-file 
 *  libraries. License follows:
 * 
 * PRGUI.Layout
 * Copyright (c) 2020 Katelyn Gadd kg@luminance.org
 * 
 * Layout - Simple 2D stacking boxes calculations
 * Copyright (c) 2016 Andrew Richards randrew@gmail.com
 * 
 * Blendish - Blender 2.5 UI based theming functions for NanoVG
 * Copyright (c) 2014 Leonard Ritter leonard.ritter@duangle.com
 * 
 * Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:
 * The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
 * 
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Squared.Game;

namespace Squared.PRGUI.Layout {
    public unsafe sealed partial class LayoutContext : IDisposable {
        public unsafe struct ChildrenEnumerator : IEnumerator<ControlKey> {
            public readonly LayoutContext Context;
            public readonly ControlKey Parent;
            private readonly ControlKey FirstChild;
            private int Version;
            private unsafe LayoutItem* pLayoutItems, pCurrent;

            public ChildrenEnumerator (LayoutContext context, ControlKey parent) {
                Context = context;
                Version = context.Version;
                Parent = parent;
                pLayoutItems = null;
                pCurrent = null;
                FirstChild = ControlKey.Invalid;
            }

            internal ChildrenEnumerator (LayoutContext context, LayoutItem *pParent) {
                Context = context;
                Version = context.Version;
                Parent = pParent->Key;
                pLayoutItems = null;
                pCurrent = null;
                FirstChild = pParent->FirstChild;
            }

            public ControlKey Current {
                get {
                    if (pCurrent == null) {
                        Context.AssertionFailed("No current item");
                        return default;
                    }

                    return pCurrent->Key;
                }
            }

            object IEnumerator.Current => Current;

            public void Dispose () {
                pCurrent = null;
                Version = -1;
            }

            public bool MoveNext () {
                if (Version != Context.Version) {
                    Context.AssertionFailed("Context was modified");
                    return false;
                }

                if (pLayoutItems == null)
                    pLayoutItems = Context.LayoutPtr();

                if (pCurrent == null) {
                    if (FirstChild.IsInvalid) {
                        var pParent = &pLayoutItems[Parent.ID];
                        var firstChild = pParent->FirstChild;
                        if (firstChild.IsInvalid)
                            pCurrent = null;
                        else
                            pCurrent = &pLayoutItems[firstChild.ID];
                    } else {
                        pCurrent = &pLayoutItems[FirstChild.ID];
                    }
                } else if (pCurrent->NextSibling.IsInvalid) {
                    pCurrent = null;
                } else {
                    pCurrent = &pLayoutItems[pCurrent->NextSibling.ID];
                }

                return (pCurrent != null);
            }

            void IEnumerator.Reset () {
                if (Version != Context.Version) {
                    Context.AssertionFailed("Context was modified");
                    throw new Exception("Context was modified");
                }

                pCurrent = null;
            }
        }

        public struct ChildrenEnumerable : IEnumerable<ControlKey> {
            public readonly LayoutContext Context;
            public readonly ControlKey Parent;

            internal ChildrenEnumerable (LayoutContext context, ControlKey parent) {
                Context = context;
                Parent = parent;
            }

            public ChildrenEnumerator GetEnumerator () {
                return new ChildrenEnumerator(Context, Parent);
            }

            IEnumerator<ControlKey> IEnumerable<ControlKey>.GetEnumerator () {
                return GetEnumerator();
            }

            IEnumerator IEnumerable.GetEnumerator () {
                return GetEnumerator();
            }
        }

        public LayoutContext () {
            Initialize();
        }

        private void InvalidState () {
            throw new Exception("Invalid internal state");
        }

        private void AssertionFailed (string message) {
            throw new Exception(
                message != null
                    ? $"Assertion failed: {message}"
                    : "Assertion failed"
                );
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Assert (bool b, string message = null) {
            if (b)
                return;

            AssertionFailed(message);
        }

        private void AssertNotRoot (ControlKey key) {
            Assert(!key.IsInvalid, "Invalid key");
            Assert(key != Root, "Key must not be the root");
        }

        private void AssertNotEqual (ControlKey lhs, ControlKey rhs) {
            Assert(lhs != rhs, "Keys must not be equal");
        }

        private void AssertMasked (ControlFlags flags, ControlFlags mask) {
            Assert((flags & mask) == flags, "Flags must be compatible with mask");
        }

        public void Update () {
            if (!UpdateSubtree(Root))
                InvalidState();
        }

        private unsafe ChildrenEnumerable Children (LayoutItem *pParent) {
            Assert(pParent != null);
            return new ChildrenEnumerable(this, pParent->Key);
        }

        public ChildrenEnumerable Children (ControlKey parent) {
            Assert(!parent.IsInvalid);
            return new ChildrenEnumerable(this, parent);
        }

        private unsafe void ApplyFloatingPosition (LayoutItem *pItem, in RectF parentRect, int idim, int wdim) {
            bool stacked = pItem->Flags.IsFlagged(ControlFlags.Layout_Stacked),
                floating = pItem->Flags.IsFlagged(ControlFlags.Layout_Floating);
            if (!floating && !stacked)
                return;
            var pRect = RectPtr(pItem->Key);
            var fs = pItem->FixedSize.GetElement(idim);
            var size = (fs > 0)
                ? fs
                : Math.Max((*pRect)[wdim], pItem->MinimumSize.GetElement(idim));

            var bFlags = (ControlFlags)((uint)(pItem->Flags & ControlFlagMask.Layout) >> idim);
            var margins = pItem->Margins[idim] + pItem->Margins[wdim];
            var fill = bFlags.IsFlagged(ControlFlags.Layout_Fill_Row);
            if (fill && (fs < 0))
                size = Math.Max(size, parentRect[wdim] - margins);

            var max = pItem->MaximumSize.GetElement(idim);
            if ((max > 0) && (fs < 0))
                size = Math.Min(max, size);

            if (fill && ((fs >= 0) || (size == max)) && !floating) {
                // Fill + fixed size = center, same for fill + max size (if max was hit)
                (*pRect)[idim] = parentRect[idim] + (parentRect[wdim] - size) / 2f;
            } else {
                (*pRect)[idim] = 
                    (!fill && bFlags.IsFlagged(ControlFlags.Layout_Anchor_Right))
                        ? (parentRect[idim] + parentRect[wdim]) - size - margins
                        // FIXME: Should we be applying the margins to the offset here? It's kind of gross but seems expected
                        : parentRect[idim] + pItem->Margins[idim] + pItem->FloatingPosition.GetElement(idim);
            }
            (*pRect)[wdim] = size;
        }

        internal unsafe bool UpdateSubtree (ControlKey key) {
            if (key.IsInvalid)
                return false;

            var pItem = LayoutPtr(key);
            var constrainSize = pItem->Flags.IsFlagged(ControlFlags.Container_Constrain_Size);
            CalcSize(pItem, LayoutDimensions.X, constrainSize);
            Arrange (pItem, LayoutDimensions.X, constrainSize);
            CalcSize(pItem, LayoutDimensions.Y, constrainSize);
            Arrange (pItem, LayoutDimensions.Y, constrainSize);

            return true;
        }

        private LayoutItem ItemTemplate = new LayoutItem(ControlKey.Invalid);
        private RectF RectTemplate = default(RectF);

        public ControlKey CreateItem (LayoutTags tag = LayoutTags.Default) {
            var newData = ItemTemplate;
            var newIndex = Layout.Count;
            newData._Key = new ControlKey(newIndex);
            newData.Tag = tag;

            Layout.Add(ref newData);
            Boxes.Add(ref RectTemplate);

            _Count = newIndex + 1;

            return newData._Key;
        }

        private unsafe void InsertBefore (LayoutItem * pNewItem, LayoutItem * pLater) {
            AssertNotRoot(pNewItem->Key);

            var pPreviousSibling = LayoutPtr(pLater->PreviousSibling, true);

            if (pPreviousSibling != null)
                pNewItem->NextSibling = pPreviousSibling->NextSibling;
            else
                pNewItem->NextSibling = ControlKey.Invalid;

            pNewItem->Parent = pLater->Parent;
            pNewItem->PreviousSibling = pLater->PreviousSibling;

            if (pPreviousSibling != null)
                pPreviousSibling->NextSibling = pNewItem->Key;

            pLater->PreviousSibling = pNewItem->Key;
        }

        private unsafe void InsertAfter (LayoutItem * pEarlier, LayoutItem * pNewItem) {
            AssertNotRoot(pNewItem->Key);

            var pNextSibling = LayoutPtr(pEarlier->NextSibling, true);

            pNewItem->Parent = pEarlier->Parent;
            pNewItem->PreviousSibling = pEarlier->Key;
            pNewItem->NextSibling = pEarlier->NextSibling;

            pEarlier->NextSibling = pNewItem->Key;

            if (pNextSibling != null) {
                pNextSibling->PreviousSibling = pNewItem->Key;
            } else {
                var pParent = LayoutPtr(pEarlier->Parent);
                Assert(pParent->LastChild == pEarlier->Key);
                pParent->LastChild = pNewItem->Key;
            }
        }

        public unsafe void SetItemForceBreak (ControlKey key, bool newState) {
            var pItem = LayoutPtr(key);
            pItem->Flags = pItem->Flags & ~ControlFlags.Layout_ForceBreak;
            if (newState)
                pItem->Flags |= ControlFlags.Layout_ForceBreak;
        }

        public unsafe ControlKey GetParent (ControlKey child) {
            if (child.IsInvalid)
                return ControlKey.Invalid;

            var ptr = LayoutPtr(child);
            return ptr->Parent;
        }

        public unsafe ControlKey GetFirstChild (ControlKey parent) {
            if (parent.IsInvalid)
                return ControlKey.Invalid;

            var ptr = LayoutPtr(parent);
            return ptr->FirstChild;
        }

        public unsafe ControlKey GetLastChild (ControlKey parent) {
            if (parent.IsInvalid)
                return ControlKey.Invalid;

            var ptr = LayoutPtr(parent);
            return ptr->LastChild;
        }

        public unsafe void InsertBefore (ControlKey newSibling, ControlKey later) {
            AssertNotRoot(newSibling);
            Assert(!later.IsInvalid);
            Assert(!newSibling.IsInvalid);
            AssertNotEqual(later, newSibling);

            var pLater = LayoutPtr(later);
            var pNewSibling = LayoutPtr(newSibling);
            InsertBefore(pNewSibling, pLater);
        }

        public unsafe void InsertAfter (ControlKey earlier, ControlKey newSibling) {
            AssertNotRoot(newSibling);
            Assert(!earlier.IsInvalid);
            Assert(!newSibling.IsInvalid);
            AssertNotEqual(earlier, newSibling);

            var pEarlier = LayoutPtr(earlier);
            var pLater = LayoutPtr(newSibling);
            InsertAfter(pEarlier, pLater);
        }

        /// <summary>
        /// Alias for InsertAtEnd
        /// </summary>
        public void Append (ControlKey parent, ControlKey child) {
            InsertAtEnd(parent, child);
        }

        public unsafe void InsertAtEnd (ControlKey parent, ControlKey child) {
            AssertNotRoot(child);
            AssertNotEqual(parent, child);

            var pParent = LayoutPtr(parent);
            var pChild = LayoutPtr(child);

            Assert(pChild->Parent.IsInvalid, "is not inserted");

            if (pParent->FirstChild.IsInvalid) {
                Assert(pParent->LastChild.IsInvalid);
                pParent->FirstChild = child;
                pParent->LastChild = child;
                pChild->Parent = parent;
            } else {
                var lastChild = GetLastChild(parent);
                var pLastChild = LayoutPtr(lastChild);
                InsertAfter(pLastChild, pChild);
            }
        }

        public unsafe void InsertAtStart (ControlKey parent, ControlKey newFirstChild) {
            AssertNotRoot(newFirstChild);
            AssertNotEqual(parent, newFirstChild);
            var pParent = LayoutPtr(parent);
            var oldChild = pParent->FirstChild;
            var pChild = LayoutPtr(newFirstChild);

            Assert(pChild->Parent.IsInvalid, "is not inserted");

            pChild->Parent = parent;
            pChild->PreviousSibling = ControlKey.Invalid;
            pChild->NextSibling = oldChild;

            pParent->FirstChild = newFirstChild;
            if (pParent->LastChild.IsInvalid)
                pParent->LastChild = newFirstChild;

            if (!oldChild.IsInvalid) {
                var pOldChild = LayoutPtr(oldChild);
                pOldChild->PreviousSibling = newFirstChild;
            }
        }

        public unsafe Vector2 GetFixedSize (ControlKey key) {
            var pItem = LayoutPtr(key);
            return pItem->FixedSize;
        }

        public unsafe void SetFixedSize (ControlKey key, in Vector2 size) {
            var pItem = LayoutPtr(key);
            pItem->FixedSize = size;

            var flags = pItem->Flags;
            if (size.X <= 0)
                flags &= ~ControlFlags.Internal_FixedWidth;
            else
                flags |= ControlFlags.Internal_FixedWidth;

            if (size.Y <= 0)
                flags &= ~ControlFlags.Internal_FixedHeight;
            else
                flags |= ControlFlags.Internal_FixedHeight;
            pItem->Flags = flags;
        }

        public void SetFixedSize (ControlKey key, float width = 0, float height = 0) {
            SetFixedSize(key, new Vector2(width, height));
        }

        public unsafe void SetSizeConstraints (ControlKey key, in Vector2? minimumSize = null, in Vector2? maximumSize = null) {
            var pItem = LayoutPtr(key);
            pItem->MinimumSize = minimumSize ?? LayoutItem.NoSize;
            pItem->MaximumSize = maximumSize ?? LayoutItem.NoSize;
        }

        public void SetSizeConstraints (ControlKey key, in ControlDimension width, in ControlDimension height) {
            SetFixedSize(key, width.Fixed ?? LayoutItem.NoValue, height.Fixed ?? LayoutItem.NoValue);
            SetSizeConstraints(
                key, new Vector2(width.Minimum ?? LayoutItem.NoValue, height.Minimum ?? LayoutItem.NoValue), 
                new Vector2(width.Maximum ?? LayoutItem.NoValue, height.Maximum ?? LayoutItem.NoValue)
            );
        }

        public unsafe void GetSizeConstraints (ControlKey key, out Vector2 minimumSize, out Vector2 maximumSize) {
            var pItem = LayoutPtr(key);
            minimumSize = pItem->MinimumSize;
            maximumSize = pItem->MaximumSize;
        }

        public unsafe bool TryMeasureContent (ControlKey container, out RectF result) {
            var pItem = LayoutPtr(container);
            float minX = 999999, minY = 999999,
                maxX = -999999, maxY = -999999;

            if (pItem->FirstChild.IsInvalid) {
                result = default(RectF);
                return true;
            }

            RectF childRect;
            foreach (var child in Children(pItem)) {
                var pChild = LayoutPtr(child);
                TryGetRect(child, out childRect);

                // HACK: The arrange algorithms will clip an element to its containing box, which
                //  hinders attempts to measure all of the content inside a container for scrolling
                if (pChild->Flags.IsFlagged(ControlFlags.Internal_FixedWidth))
                    childRect.Width = pChild->FixedSize.X;
                if (pChild->Flags.IsFlagged(ControlFlags.Internal_FixedHeight))
                    childRect.Height = pChild->FixedSize.Y;

                minX = Math.Min(minX, childRect.Left - pChild->Margins.Left);
                maxX = Math.Max(maxX, childRect.Left + childRect.Width + pChild->Margins.Right);
                minY = Math.Min(minY, childRect.Top - pChild->Margins.Top);
                maxY = Math.Max(maxY, childRect.Top + childRect.Height + pChild->Margins.Bottom);
            }

            result = new RectF(minX, minY, maxX - minX, maxY - minY);
            return true;
        }

        public unsafe bool TryGetFlags (ControlKey key, out ControlFlags result) {
            var pItem = LayoutPtr(key, optional: true);
            if (pItem == null) {
                result = default(ControlFlags);
                return false;
            }
            result = pItem->Flags;
            return true;
        }

        public unsafe ControlFlags GetContainerFlags (ControlKey key) {
            var pItem = LayoutPtr(key);
            return pItem->Flags & ControlFlagMask.Container;
        }

        public unsafe void SetContainerFlags (ControlKey key, ControlFlags flags) {
            AssertMasked(flags, ControlFlagMask.Container);
            var pItem = LayoutPtr(key);
            var arrangement = flags & (ControlFlags.Container_Row | ControlFlags.Container_Column);
            if (arrangement == default)
                throw new ArgumentException("Container must be in either row or column mode");
            /*
            else if (arrangement == (ControlFlags.Container_Column | ControlFlags.Container_Row))
                throw new ArgumentException("Container must be in either row or column mode");
            */
            pItem->Flags = (pItem->Flags & ~ControlFlagMask.Container) | flags;
        }

        public unsafe ControlFlags GetLayoutFlags (ControlKey key) {
            var pItem = LayoutPtr(key);
            return pItem->Flags & ControlFlagMask.Layout;
        }

        public unsafe void SetLayoutFlags (ControlKey key, ControlFlags flags) {
            AssertMasked(flags, ControlFlagMask.Layout);
            var pItem = LayoutPtr(key);
            pItem->Flags = (pItem->Flags & ~ControlFlagMask.Layout) | flags;
        }

        public unsafe void SetLayoutData (ControlKey key, ref Vector2 floatingPosition, ref Margins margins, ref Margins padding) {
            var pItem = LayoutPtr(key);
            pItem->FloatingPosition = floatingPosition;
            pItem->Margins = margins;
            pItem->Padding = padding;
        }

        public unsafe void SetTag (ControlKey key, LayoutTags tag) {
            // HACK
            if (key.IsInvalid)
                return;
            var pItem = LayoutPtr(key);
            pItem->Tag = tag;
        }

        public unsafe void SetMargins (ControlKey key, in Margins m) {
            var pItem = LayoutPtr(key);
            pItem->Margins = m;
        }

        public unsafe void SetPadding (ControlKey key, in Margins m) {
            var pItem = LayoutPtr(key);
            pItem->Padding = m;
        }

        public unsafe Margins GetMargins (ControlKey key) {
            var pItem = LayoutPtr(key);
            return pItem->Margins;
        }

        public unsafe Margins GetPadding (ControlKey key) {
            var pItem = LayoutPtr(key);
            return pItem->Padding;
        }

        private unsafe float CalcMinimumSize (LayoutItem * pItem, int idim) {
            var result = Math.Max(
                pItem->MinimumSize.GetElement(idim),
                pItem->FixedSize.GetElement(idim)
            );
            var preventCrush = (uint)ControlFlags.Container_Prevent_Crush_X << idim;
            if (pItem->Flags.IsFlagged((ControlFlags)preventCrush))
                result = Math.Max(result, pItem->ComputedContentSize.GetElement(idim));
            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SetComputedContentSize (LayoutItem * pItem, int idim, float value) {
            PRGUIExtensions.SetElement(ref pItem->ComputedContentSize, idim, value);
        }

        private unsafe float CalcOverlaySize (LayoutItem * pItem, LayoutDimensions dim) {
            int idim = (int)dim, wdim = idim + 2;
            var outerPadding = pItem->Padding[idim] + pItem->Padding[wdim];
            if (pItem->FirstChild.IsInvalid) {
                SetComputedContentSize(pItem, idim, 0);
                return outerPadding;
            }

            float result = 0, minimum = 0;
            var rowFlag = (ControlFlags)((int)ControlFlags.Container_Row + idim);
            var isRowFlaggedExact = (pItem->Flags & (ControlFlags.Container_Row | ControlFlags.Container_Column)) == rowFlag;
            RectF childRect;
            foreach (var child in Children(pItem)) {
                var pChild = LayoutPtr(child);
                var isFloating = pChild->Flags.IsFlagged(ControlFlags.Layout_Floating);
                var isStacked = pChild->Flags.IsFlagged(ControlFlags.Layout_Stacked);

                TryGetRect(child, out childRect);
                var childRightMargin = pChild->Margins[wdim];
                var childMargin = pChild->Margins[idim] + childRightMargin;
                var childMinimum = CalcMinimumSize(pChild, idim) + childMargin;
                if (isFloating) {
                    minimum = Math.Max(childMinimum, minimum);
                    continue;
                } else if (isRowFlaggedExact)
                    minimum += childMinimum;
                else
                    minimum = Math.Max(childMinimum, minimum);
                // HACK: childRect[idim] already has our left margin applied
                var childSize = childRect[idim] + childRect[wdim] + childRightMargin;
                result = Math.Max(result, childSize);
            }
            minimum += outerPadding;
            result += outerPadding;
            SetComputedContentSize(pItem, idim, minimum);
            return result;
        }

        private unsafe float CalcStackedSize (LayoutItem * pItem, LayoutDimensions dim) {
            float result = 0, minimum = 0;
            int idim = (int)dim, wdim = idim + 2;
            var outerPadding = pItem->Padding[idim] + pItem->Padding[wdim];
            var rowFlag = (ControlFlags)((int)ControlFlags.Container_Row + idim);
            RectF childRect;
            foreach (var child in Children(pItem)) {
                var pChild = LayoutPtr(child);
                var isFloating = pChild->Flags.IsFlagged(ControlFlags.Layout_Floating);
                var isStacked = pChild->Flags.IsFlagged(ControlFlags.Layout_Stacked);

                TryGetRect(child, out childRect);
                var childRightMargin = pChild->Margins[wdim];
                var childMargin = pChild->Margins[idim] + childRightMargin;
                var childMinimum = CalcMinimumSize(pChild, idim) + childMargin;
                if (pItem->Flags.IsFlagged(rowFlag) && !isFloating)
                    minimum += childMinimum;
                else
                    minimum = Math.Max(childMinimum, minimum);

                var sum = childRect[idim] + childRect[wdim] + childRightMargin;
                if (isFloating)
                    ;
                else if (isStacked)
                    result = Math.Max(result, sum);
                else
                    result += sum;
            }
            minimum += outerPadding;
            result += outerPadding;
            SetComputedContentSize(pItem, idim, minimum);
            return result;
        }

        private unsafe float CalcWrappedSizeImpl (
            LayoutItem * pItem, LayoutDimensions dim, bool overlaid, bool forcedBreakOnly
        ) {
            int idim = (int)dim, wdim = idim + 2;
            var noExpand = pItem->Flags.IsFlagged((ControlFlags)((uint)ControlFlags.Container_No_Expansion_X << idim));
            float needSizeThisBlock = 0, needSizeTotal = 0;
            RectF childRect;
            foreach (var child in Children(pItem)) {
                var pChild = LayoutPtr(child);
                var isFloating = pChild->Flags.IsFlagged(ControlFlags.Layout_Floating);
                var isStacked = pChild->Flags.IsFlagged(ControlFlags.Layout_Stacked);
                if (isFloating)
                    continue;

                TryGetRect(child, out childRect);
                var childRightMargin = pChild->Margins[wdim];
                var childSize = childRect[idim] + childRect[wdim] + childRightMargin;

                if (
                    (!forcedBreakOnly && pChild->Flags.IsBreak()) ||
                    pChild->Flags.IsFlagged(ControlFlags.Layout_ForceBreak)
                ) {
                    if (overlaid)
                        needSizeTotal += needSizeThisBlock;
                    else
                        needSizeTotal = Math.Max(needSizeTotal, needSizeThisBlock);

                    needSizeThisBlock = 0;
                }

                if (overlaid)
                    needSizeThisBlock = Math.Max(needSizeThisBlock, childSize);
                else
                    needSizeThisBlock += childSize;
            }

            float result;
            if (noExpand)
                result = 0;
            else if (overlaid)
                result = needSizeThisBlock + needSizeTotal;
            else
                result = Math.Max(needSizeThisBlock, needSizeTotal);

            // FIXME: Is this actually necessary?
            GetComputedMinimumSize(pItem, out Vector2 minimumSize);
            GetComputedMaximumSize(pItem, null, out Vector2 maximumSize);
            SetComputedContentSize(pItem, idim, result);

            result += pItem->Padding[idim] + pItem->Padding[wdim];

            result = Constrain(result, minimumSize.GetElement(idim), maximumSize.GetElement(idim));
            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe void GetComputedMinimumSize (LayoutItem * pItem, out Vector2 result) {
            result = pItem->FixedSize;
            if (result.X < 0)
                result.X = pItem->MinimumSize.X;
            if (result.Y < 0)
                result.Y = pItem->MinimumSize.Y;

            if (pItem->Flags.IsFlagged(ControlFlags.Container_Prevent_Crush_X))
                if (pItem->ComputedContentSize.X > 0)
                    result.X = Math.Max(result.X, pItem->ComputedContentSize.X);

            if (pItem->Flags.IsFlagged(ControlFlags.Container_Prevent_Crush_Y))
                if (pItem->ComputedContentSize.Y > 0)
                    result.Y = Math.Max(result.Y, pItem->ComputedContentSize.Y);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe void GetComputedMaximumSize (LayoutItem * pItem, in Vector2? parentConstraint, out Vector2 result) {
            result = pItem->FixedSize;
            if (result.X < 0)
                result.X = pItem->MaximumSize.X;
            if (result.Y < 0)
                result.Y = pItem->MaximumSize.Y;

            if (parentConstraint.HasValue) {
                if (result.X < 0)
                    result.X = parentConstraint.Value.X;
                else
                    result.X = Math.Min(parentConstraint.Value.X, result.X);

                if (result.Y < 0)
                    result.Y = parentConstraint.Value.Y;
                else
                    result.Y = Math.Min(parentConstraint.Value.Y, result.Y);
            }
        }

        private unsafe float CalcWrappedOverlaidSize (LayoutItem * pItem, LayoutDimensions dim) {
            return CalcWrappedSizeImpl(pItem, dim, true, false);
        }

        private unsafe float CalcWrappedStackedSize (LayoutItem * pItem, LayoutDimensions dim) {
            return CalcWrappedSizeImpl(pItem, dim, false, false);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private float Constrain (float value, float maybeMin, float maybeMax) {
            if (maybeMin >= 0)
                value = Math.Max(value, maybeMin);
            if (maybeMax >= 0)
                value = Math.Min(value, maybeMax);
            return value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe float Constrain (float value, LayoutItem * pItem, int dimension) {
            GetComputedMinimumSize(pItem, out Vector2 minimum);
            GetComputedMaximumSize(pItem, null, out Vector2 maximum);
            return Constrain(value, minimum.GetElement(dimension), maximum.GetElement(dimension));
        }

        private unsafe void CalcSize (LayoutItem * pItem, LayoutDimensions dim, bool constrainSize) {
            var constrainChildSize = pItem->Flags.IsFlagged(ControlFlags.Container_Constrain_Size);
            foreach (var child in Children(pItem)) {
                // NOTE: Potentially unbounded recursion
                var pChild = LayoutPtr(child);
                CalcSize(pChild, dim, constrainChildSize);
            }

            var pRect = RectPtr(pItem->Key);
            var idim = (int)dim;

            SetComputedContentSize(pItem, idim, 0);

            if (pItem->Flags.IsFlagged(ControlFlags.Layout_Floating))
                // For floating controls we need to ensure that we don't accidentally trample the floating position
                // FIXME: This shouldn't be necessary
                (*pRect)[idim] = pItem->Margins[idim] + pItem->FloatingPosition.GetElement(idim);
            else
                // Start by setting position to top/left margin
                (*pRect)[idim] = pItem->Margins[idim];

            if (pItem->FixedSize.GetElement(idim) > 0) {
                (*pRect)[idim + 2] = Constrain(pItem->FixedSize.GetElement(idim), pItem, idim);
                // FIXME: This breaks computed content sizes for fixed size elements
                return;
            }

            float result = 0;
            switch (pItem->Flags & ControlFlagMask.BoxModel) {
                case ControlFlags.Container_Column | ControlFlags.Container_Wrap:
                    if (dim == LayoutDimensions.Y)
                        result = CalcStackedSize(pItem, dim);
                    else
                        result = CalcOverlaySize(pItem, dim);
                    break;
                case ControlFlags.Container_Row | ControlFlags.Container_Wrap:
                    if (dim == LayoutDimensions.X)
                        result = CalcWrappedStackedSize(pItem, dim);
                    else
                        result = CalcWrappedOverlaidSize(pItem, dim);
                    break;
                case ControlFlags.Container_Row:
                case ControlFlags.Container_Column:
                    if (((uint)pItem->Flags & 1) == (uint)dim) {
                        result = CalcStackedSize(pItem, dim);
                        // result = CalcWrappedSizeImpl(pItem, dim, false, true);
                    } else
                        result = CalcOverlaySize(pItem, dim);
                        // result = CalcWrappedSizeImpl(pItem, dim, true, true);
                    break;
                default:
                    result = CalcOverlaySize(pItem, dim);
                    break;
            }

            var isFloating = pItem->Flags.IsFlagged(ControlFlags.Layout_Floating);
            var parentRect = isFloating
                ? GetContentRect(pItem->Parent)
                : default;

            if (isFloating) {
                // HACK: If we are maximized, enforce our full layout instead of just size
                if (dim == LayoutDimensions.X && (parentRect.Width > 0)) {
                    if (pItem->Flags.IsFlagged(ControlFlags.Layout_Fill_Row)) {
                        // pRect->Left = parentRect.Left;
                        result = parentRect.Width - pItem->Margins.X;
                    }
                } else if (parentRect.Height > 0) {
                    if (pItem->Flags.IsFlagged(ControlFlags.Layout_Fill_Column)) {
                        // pRect->Top = parentRect.Top;
                        result = parentRect.Height - pItem->Margins.Y;
                    }
                }
            }

            (*pRect)[2 + idim] = Constrain(result, pItem, idim);
        }

        private unsafe void ArrangeStacked (LayoutItem * pItem, LayoutDimensions dim, bool wrap) {
            if (pItem->FirstChild.ID < 0)
                return;

            var itemFlags = pItem->Flags;
            var rect = GetContentRect(pItem);
            int idim = (int)dim, wdim = idim + 2;
            float space = rect[wdim], max_x2 = rect[idim] + space;

            var startChild = pItem->FirstChild;
            while (!startChild.IsInvalid) {
                float used;
                uint fillerCount, squeezedCount, fixedCount, total;
                bool hardBreak;
                ControlKey child, endChild;

                BuildStackedRow(
                    wrap, idim, wdim, space, startChild,
                    out used, out fillerCount, out squeezedCount, out fixedCount, out total,
                    out hardBreak, out child, out endChild
                );

                var extraSpace = space - used;
                float filler = 0, spacer = 0, extraMargin = 0, eater = 0;

                if (extraSpace > 0) {
                    if (fillerCount > 0)
                        filler = extraSpace / fillerCount;
                    else if (total > 0) {
                        switch (itemFlags & ControlFlags.Container_Align_Justify) {
                            case ControlFlags.Container_Align_Start:
                                break;
                            case ControlFlags.Container_Align_End:
                                extraMargin = extraSpace;
                                break;
                            case ControlFlags.Container_Align_Justify:
                                // justify when not wrapping or not in last line, or not manually breaking
                                if (!wrap || (!endChild.IsInvalid && !hardBreak))
                                    spacer = extraSpace / (total - 1);
                                else
                                    ;
                                break;
                            default:
                                extraMargin = extraSpace / 2;
                                break;
                        }
                    } else
                        ;

                    // oui.h
                    // } else if (!wrap && (extraSpace < 0)) {
                    // layout.h
                } else if (!wrap && (squeezedCount > 0)) {
                    eater = extraSpace / squeezedCount;
                }

                float x = rect[idim];
                child = startChild;
                ArrangeStackedRow(
                    wrap, idim, wdim, max_x2, 
                    pItem, ref child, endChild,
                    fillerCount, squeezedCount, 
                    fixedCount, total,
                    filler, spacer, 
                    extraMargin, eater, x
                );

                startChild = endChild;
            }
        }

        private unsafe void ArrangeStackedRow (
            bool wrap, int idim, int wdim, float max_x2,
            LayoutItem* pParent, ref ControlKey child, ControlKey endChild, 
            uint fillerCount, uint squeezedCount, uint fixedCount, uint total,
            float filler, float spacer, float extraMargin, 
            float eater, float x
        ) {
            if (child == endChild)
                return;

            int constrainedCount = 0, numProcessed = 0;
            float extraFromConstraints = 0, originalExtraMargin = extraMargin, originalX = x;

            var attemptLastChanceWrap = wrap && false;
            var startChild = child;
            RectF childRect;
            var parentRect = GetContentRect(pParent);

            // Perform initial size calculation for items, and then arrange items and calculate final sizes
            for (int pass = 0; pass < 2; pass++) {
                child = startChild;
                extraMargin = originalExtraMargin;
                x = originalX;
                numProcessed = 0;

                while (child != endChild) {
                    float ix0 = 0, ix1 = 0;

                    // FIXME: Duplication
                    var pChild = LayoutPtr(child);
                    var childFlags = pChild->Flags;
                    if (childFlags.IsFlagged(ControlFlags.Layout_Floating) || childFlags.IsFlagged(ControlFlags.Layout_Stacked)) {
                        // ApplyFloatingPosition(pChild, ref parentRect, idim, wdim);
                        child = pChild->NextSibling;
                        continue;
                    }

                    var flags = (ControlFlags)((uint)(childFlags & ControlFlagMask.Layout) >> idim);
                    var fFlags = (ControlFlags)((uint)(childFlags & ControlFlagMask.Fixed) >> idim);
                    var childMargins = pChild->Margins;
                    TryGetRect(child, out childRect);
                    var isFixedSize = fFlags.IsFlagged(ControlFlags.Internal_FixedWidth);

                    x += childRect[idim] + extraMargin;

                    float computedSize;
                    if (isFixedSize)
                        computedSize = childRect[wdim];
                    else if (flags.IsFlagged(ControlFlags.Layout_Fill_Row))
                        computedSize = filler;
                    else
                        computedSize = Math.Max(0f, childRect[wdim] + eater);

                    if ((pass == 1) && (fillerCount > constrainedCount) && (constrainedCount > 0) && !isFixedSize)
                        computedSize += extraFromConstraints / (fillerCount - constrainedCount);

                    GetComputedMinimumSize(pChild, out Vector2 childMinimum);
                    GetComputedMaximumSize(pChild, null, out Vector2 childMaximum);
                    // return Constrain(value, minimum.GetElement(dimension), maximum.GetElement(dimension));

                    float constrainedSize = Constrain(computedSize, childMinimum.GetElement(idim), childMaximum.GetElement(idim));
                    if (wrap)
                        constrainedSize = Constrain(Math.Min(max_x2 - childMargins[wdim] - x, constrainedSize), childMinimum.GetElement(idim), childMaximum.GetElement(idim));
                    if (pass == 0) {
                        float constraintDelta = (computedSize - constrainedSize);
                        // FIXME: Epsilon too big?
                        if (Math.Abs(constraintDelta) >= 0.1) {
                            extraFromConstraints += constraintDelta;
                            constrainedCount++;
                        }
                    }

                    ix0 = x;
                    ix1 = x + constrainedSize;

                    if (
                        pParent->Flags.IsFlagged(ControlFlags.Container_Constrain_Size) && 
                        (pChild->FixedSize.GetElement(idim) < 0)
                    ) {
                        float parentExtent = Math.Max((parentRect[idim] + parentRect[wdim]), 0);
                        ix1 = Constrain(ix1, -1, parentExtent);
                    }

                    float finalSize = ix1 - ix0;

                    if (attemptLastChanceWrap && (pass == 0)) {
                        // FIXME: This needs to expand the previous items somehow...
                        // Identify cases where we need to wrap but only know after the first layout pass
                        var predictedRect = childRect;
                        predictedRect[idim] = ix0;
                        predictedRect[wdim] = finalSize;
                        var extent = predictedRect.Extent;
                        var predictedExtent = extent.GetElement(idim);
                        var predictedOverflow = predictedExtent - max_x2;
                        // HACK :-(
                        if (predictedOverflow >= 0.5f)
                            pChild->Flags |= ControlFlags.Internal_Break;
                    } else if (pass == 1) {
                        // FIXME: Is this correct?
                        childRect[idim] = ix0;
                        childRect[wdim] = finalSize;
                        SetRect(child, in childRect);
                        CheckConstraints(child, idim);
                    }

                    if (attemptLastChanceWrap && pChild->Flags.IsBreak()) {
                        extraMargin = originalExtraMargin;
                        x = originalX;
                    }

                    x = x + constrainedSize + childMargins[wdim];
                    child = pChild->NextSibling;
                    extraMargin = spacer;
                    numProcessed++;
                }
            }
        }

        private unsafe void CheckConstraints (ControlKey control, int dimension) {
#if DEBUG
            // FIXME
            return;

            var wdim = dimension + 2;

            var pItem = LayoutPtr(control);
            var rect = GetRect(control);
            if (false && (rect[wdim] < -1))
                System.Diagnostics.Debugger.Break();

            GetComputedMinimumSize(pItem, out Vector2 minimum);
            GetComputedMaximumSize(pItem, null, out Vector2 maximum);
            var min = minimum.GetElement(dimension);
            var max = maximum.GetElement(dimension);
            // FIXME
            if ((min > max) && (min >= 0) && (max >= 0))
                return;

            var padding = 0.5f;
            if (
                ((min >= 0) && (rect[wdim] + padding < min)) ||
                ((max >= 0) && (rect[wdim] - padding > max))
            )
                System.Diagnostics.Debugger.Break();
#endif
        }

        private unsafe void BuildStackedRow (
            bool wrap, int idim, int wdim, float space, ControlKey startChild, 
            out float used, out uint fillerCount, out uint squeezedCount, 
            out uint fixedCount, out uint total, 
            out bool hardBreak, out ControlKey child, out ControlKey endChild
        ) {
            used = 0;
            fixedCount = fillerCount = squeezedCount = total = 0;
            hardBreak = false;

            RectF childRect;
            var parentRect = startChild.IsInvalid 
                ? default(RectF)
                : GetContentRect(GetParent(startChild));

            // first pass: count items that need to be expanded, and the space that is used
            child = startChild;
            endChild = ControlKey.Invalid;
            while (!child.IsInvalid) {
                var pChild = LayoutPtr(child);
                var childFlags = pChild->Flags;
                if (childFlags.IsFlagged(ControlFlags.Layout_Floating) || childFlags.IsFlagged(ControlFlags.Layout_Stacked)) {
                    // FIXME: Should we need to do this?
                    // ApplyFloatingPosition(pChild, ref parentRect, idim, wdim);
                    child = pChild->NextSibling;
                    continue;
                }

                var flags = (ControlFlags)((uint)(childFlags & ControlFlagMask.Layout) >> idim);
                var fFlags = (ControlFlags)((uint)(childFlags & ControlFlagMask.Fixed) >> idim);
                var childMargins = pChild->Margins;
                TryGetRect(child, out childRect);
                var extend = used;
                var isFillRow = flags.IsFlagged(ControlFlags.Layout_Fill_Row);
                var isFixedSize = fFlags.IsFlagged(ControlFlags.Internal_FixedWidth);

                var computedExtend = childRect[idim] + childMargins[wdim];
                var computedSize = computedExtend + childRect[wdim];

                if (
                    childFlags.IsFlagged(ControlFlags.Layout_ForceBreak) &&
                    (child != startChild)
                ) {
                    endChild = child;
                    break;
                } else if (isFixedSize) {
                    fixedCount++;
                    extend += computedSize;
                } else if (isFillRow) {
                    ++fillerCount;
                    extend += computedExtend;
                } else {
                    ++squeezedCount;
                    extend += computedSize;
                }

                var overflowAmount = extend - space;
                // HACK :-(
                var overflowed = overflowAmount >= 0.5f;
                if (
                    wrap &&
                    (total != 0) && (
                        overflowed ||
                        childFlags.IsBreak()
                    )
                ) {
                    endChild = child;
                    hardBreak = childFlags.IsBreak();
                    pChild->Flags |= ControlFlags.Internal_Break;
                    break;
                } else {
                    used = extend;
                    child = pChild->NextSibling;
                }

                ++total;
            }
        }

        private unsafe void ArrangeOverlay (LayoutItem * pItem, LayoutDimensions dim) {
            if (pItem->FirstChild.ID < 0)
                return;

            int idim = (int)dim, wdim = idim + 2;

            var contentRect = GetContentRect(pItem);
            var offset = contentRect[idim];
            var space = contentRect[wdim];
            RectF childRect;

            foreach (var child in Children(pItem)) {
                var pChild = LayoutPtr(child);
                if (pChild->Flags.IsFlagged(ControlFlags.Layout_Floating) || pChild->Flags.IsFlagged(ControlFlags.Layout_Stacked))
                    continue;

                var bFlags = (ControlFlags)((uint)(pItem->Flags & ControlFlagMask.Layout) >> idim);
                var childMargins = pChild->Margins;
                TryGetRect(child, out childRect);

                switch (bFlags & ControlFlags.Layout_Fill_Row) {
                    case 0: // ControlFlags.Layout_Center:
                        childRect[idim] += (space - childRect[wdim]) / 2 - childMargins[wdim];
                        break;
                    case ControlFlags.Layout_Anchor_Right:
                        childRect[idim] += space - childRect[wdim] - childMargins[idim] - childMargins[wdim];
                        break;
                    case ControlFlags.Layout_Fill_Row:
                        var fillValue = Constrain(Math.Max(0, space - childRect[idim] - childMargins[wdim]), pChild, idim);
                        childRect[wdim] = fillValue;
                        break;
                }

                childRect[idim] += offset;
                SetRect(child, in childRect);
                CheckConstraints(child, idim);
            }
        }

        private unsafe void ArrangeOverlaySqueezedRange (LayoutItem *pParent, ref RectF parentRect, LayoutDimensions dim, ControlKey startItem, ControlKey endItem, float offset, float space) {
            if (startItem == endItem)
                return;

            Assert(!startItem.IsInvalid);

            int idim = (int)dim, wdim = idim + 2;

            RectF rect;
            var item = startItem;
            while (item != endItem) {
                var pItem = LayoutPtr(item);
                if (pItem->Flags.IsFlagged(ControlFlags.Layout_Floating) || pItem->Flags.IsFlagged(ControlFlags.Layout_Stacked)) {
                    // ApplyFloatingPosition(pItem, ref parentRect, idim, wdim);
                    item = pItem->NextSibling;
                    continue;
                }

                var bFlags = (ControlFlags)((uint)(pItem->Flags & ControlFlagMask.Layout) >> idim);
                var margins = pItem->Margins;
                TryGetRect(item, out rect);
                var maxSize = Math.Max(0, space - rect[idim] - margins[wdim]);

                switch (bFlags & ControlFlags.Layout_Fill_Row) {
                    case 0: // ControlFlags.Layout_Center:
                        rect[wdim] = Math.Min(rect[wdim], maxSize);
                        rect[idim] += (space - rect[wdim]) / 2 - margins[wdim];
                        break;
                    case ControlFlags.Layout_Anchor_Right:
                        rect[wdim] = Math.Min(rect[wdim], maxSize);
                        rect[idim] = space - rect[wdim] - margins[wdim];
                        break;
                    case ControlFlags.Layout_Fill_Row:
                        rect[wdim] = maxSize;
                        break;
                    default:
                        rect[wdim] = Math.Min(rect[wdim], maxSize);
                        break;
                }

                rect[idim] += offset;
                var unconstrained = rect[wdim];
                // FIXME: Redistribute remaining space?

                Vector2? parentConstraint = null;
                if (pParent->Flags.IsFlagged(ControlFlags.Container_Constrain_Size)) {
                    // rect[idim] = Constrain(rect[idim], parentRect[idim], parentRect[wdim]);
                    var temp = parentRect.Extent - rect.Position;
                    var spacing = pParent->Padding;
                    // FIXME: This previously included margins, but that breaks listbox autosize
                    parentConstraint = new Vector2(
                        Math.Max(0, temp.X - spacing.Right),
                        Math.Max(0, temp.Y - spacing.Bottom)
                    );
                }

                GetComputedMinimumSize(pItem, out Vector2 minimum);
                GetComputedMaximumSize(pItem, parentConstraint, out Vector2 maximum);
                rect[wdim] = Constrain(unconstrained, minimum.GetElement(idim), maximum.GetElement(idim));
                float extent = rect[idim] + rect[wdim];

                SetRect(item, in rect);
                CheckConstraints(item, idim);
                item = pItem->NextSibling;
            }
        }

        private unsafe float ArrangeWrappedOverlaySqueezed (LayoutItem * pItem, LayoutDimensions dim) {
            // FIXME: Find some way to early-out here if there are no children?

            int idim = (int)dim, wdim = idim + 2;
            var contentRect = GetContentRect(pItem);
            float offset = contentRect[idim], needSize = 0;

            var startChild = pItem->FirstChild;
            LayoutItem* childToExpand = null, lastChild = null;
            RectF childRect;
            foreach (var child in Children(pItem)) {
                var pChild = LayoutPtr(child);
                if (pChild->Flags.IsFlagged(ControlFlags.Layout_Floating) || pChild->Flags.IsFlagged(ControlFlags.Layout_Stacked))
                    continue;

                lastChild = pChild;
                if ((dim == LayoutDimensions.X) && pChild->Flags.IsFlagged(ControlFlags.Layout_Fill_Row))
                    childToExpand = pChild;
                else if ((dim == LayoutDimensions.Y) && pChild->Flags.IsFlagged(ControlFlags.Layout_Fill_Column))
                    childToExpand = pChild;

                if (
                    pChild->Flags.IsBreak()
                ) {
                    ArrangeOverlaySqueezedRange(pItem, ref contentRect, dim, startChild, child, offset, needSize);
                    offset += needSize;
                    startChild = child;
                    needSize = 0;
                }

                TryGetRect(child, out childRect);
                var childSize = childRect[idim] + childRect[wdim] + pChild->Margins[wdim];
                needSize = Math.Max(needSize, childSize);
            }

            // HACK: Strange but seemingly necessary - even though the last child was not set to fill mode,
            //  what this will do is fill it (to consume the available space) and then shrink it to fit
            //  its size constraint again afterward with the correct alignment. I guess.
            if (childToExpand == null)
                childToExpand = lastChild;

            // HACK: We want to expand the last expandable item to fill all available space
            var space = (childToExpand == lastChild)
                ? Math.Max(needSize, contentRect[wdim] - offset + contentRect[idim])
                : needSize;

            ArrangeOverlaySqueezedRange(
                pItem, ref contentRect, dim, startChild, ControlKey.Invalid, offset, space
            );
            offset += needSize;

            return offset;
        }

        private unsafe void Arrange (LayoutItem * pItem, LayoutDimensions dim, bool constrainSize) {
            var flags = pItem->Flags;
            var pRect = RectPtr(pItem->Key);
            GetContentRect(pItem, ref *pRect, out RectF contentRect);
            int idim = (int)dim, wdim = idim + 2;

            if (constrainSize && !pItem->Parent.IsInvalid) {
                var parentRect = GetContentRect(pItem->Parent);
                // FIXME: Investigate why this is necessary
                // HACK: Sometimes we end up with a rect larger than our parent. Not sure why, but let's fix that
                //  so that our children don't end up being way too big as well
                var limit = parentRect[idim] + parentRect[wdim] - (*pRect)[idim];
                (*pRect)[wdim] = Constrain((*pRect)[wdim], 0, limit);
            }

            switch (flags & ControlFlagMask.BoxModel) {
                case ControlFlags.Container_Column | ControlFlags.Container_Wrap:
                    if (dim == LayoutDimensions.Y) {
                        ArrangeStacked(pItem, LayoutDimensions.Y, true);
                        var offset = ArrangeWrappedOverlaySqueezed(pItem, LayoutDimensions.X);
                        // FIXME: What on earth is this here for?
                        // (*pRect)[0] = offset - (*pRect)[0];
                        ;
                    } else {
                        // FIXME: Should we do something here?
                    }
                    break;
                case ControlFlags.Container_Row | ControlFlags.Container_Wrap:
                    if (!pItem->FirstChild.IsInvalid && pItem->Flags.IsFlagged(ControlFlags.Layout_Floating))
                        ;

                    if (dim == LayoutDimensions.X)
                        ArrangeStacked(pItem, LayoutDimensions.X, true);
                    else
                        ArrangeWrappedOverlaySqueezed(pItem, LayoutDimensions.Y);
                    break;
                case ControlFlags.Container_Column:
                    contentRect = ArrangeUnwrappedColumnOrRow(pItem, dim, flags, contentRect, idim);
                    break;
                case ControlFlags.Container_Row:
                    // For debugging
                    contentRect = ArrangeUnwrappedColumnOrRow(pItem, dim, flags, contentRect, idim);
                    break;
                default:
                    ArrangeOverlay(pItem, dim);
                    break;
            }

            var constrainChildSize = pItem->Flags.IsFlagged(ControlFlags.Container_Constrain_Size);
            foreach (var child in Children(pItem)) {
                // NOTE: Potentially unbounded recursion
                var pChild = LayoutPtr(child);
                ApplyFloatingPosition(pChild, in contentRect, idim, idim + 2);
                Arrange(pChild, dim, constrainChildSize);
            }
        }

        private RectF ArrangeUnwrappedColumnOrRow (LayoutItem* pItem, LayoutDimensions dim, ControlFlags flags, RectF contentRect, int idim) {
            if (((uint)flags & 1) == (uint)dim) {
                ArrangeStacked(pItem, dim, false);
            } else {
                ArrangeOverlaySqueezedRange(
                    pItem, ref contentRect, dim, pItem->FirstChild, ControlKey.Invalid,
                    contentRect[idim], contentRect[idim + 2]
                );
            }

            return contentRect;
        }
    }
}
