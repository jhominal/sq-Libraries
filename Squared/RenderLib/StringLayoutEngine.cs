﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Squared.Game;
using Squared.Util;
using Squared.Util.Text;

namespace Squared.Render.Text {
    public struct StringLayoutEngine : IDisposable {
        public DenseList<LayoutMarker> Markers;
        public DenseList<LayoutHitTest> HitTests;
        public DenseList<uint> WordWrapCharacters;

        public const int DefaultBufferPadding = 4;

        // Parameters
        public bool                characterWrap;
        public bool                wordWrap;
        public bool                hideOverflow;
        public bool                reverseOrder;
        public bool                measureOnly;
        public bool                recordUsedTextures;
        public bool                expandHorizontallyWhenAligning;
        public bool                splitAtWrapCharactersOnly;
        public bool                includeTrailingWhitespace;
        public bool                clearUserData;
        public UnorderedList<BitmapDrawCall>.Allocator allocator;
        public ArraySegment<BitmapDrawCall> buffer;
        public Vector2?            position;
        public Color?              overrideColor;
        public Color               defaultColor;
        public Color               addColor;
        public DrawCallSortKey     sortKey;
        public int                 characterSkipCount;
        public int?                characterLimit;
        public int?                lineLimit;
        public int?                lineBreakLimit;
        public float               scale;
        private float              _spacingMinusOne;
        public float               xOffsetOfFirstLine;
        public float               xOffsetOfWrappedLine;
        public float               xOffsetOfNewLine;
        public float               desiredWidth;
        public float               extraLineBreakSpacing;
        public float?              maxExpansion;
        public float?              lineBreakAtX;
        public float?              stopAtY;
        public GlyphPixelAlignment alignToPixels;
        public HorizontalAlignment alignment;
        public uint?               replacementCodepoint;
        public Func<ArraySegment<BitmapDrawCall>, ArraySegment<BitmapDrawCall>> growBuffer;
        public Vector4             userData;
        public Vector4             imageUserData;
        public List<AbstractTextureReference> usedTextures;

        public float spacing {
            get {
                return _spacingMinusOne + 1;
            }
            set {
                _spacingMinusOne = value - 1;
            }
        }

        // State
        public  float  maxLineHeight;
        public  float  currentLineMaxX, currentLineMaxXUnconstrained;
        private float  initialLineXOffset;
        private float  currentLineWrapPointLeft, currentLineWhitespaceMaxX;
        private float  maxX, maxY, maxXUnconstrained, maxYUnconstrained;
        private float  initialLineSpacing, currentLineSpacing;
        private float  currentXOverhang;
        private float  currentBaseline;
        private float  maxLineSpacing;
        public  float? currentLineBreakAtX;
        public Vector2 actualPosition, characterOffset, characterOffsetUnconstrained;
        public Bounds  firstCharacterBounds, lastCharacterBounds;
        public  int    drawCallsWritten, drawCallsSuppressed;
        private int    bufferWritePosition, wordStartWritePosition, baselineAdjustmentStart;
        private int _rowIndex, _colIndex, _wordIndex;
        private int    wordStartColumn;
        Vector2        wordStartOffset;
        private bool   ownsBuffer, suppress, suppressUntilNextLine, previousGlyphWasDead, 
            newLinePending, wordWrapSuppressed;
        private AbstractTextureReference lastUsedTexture;
        private DenseList<Bounds> boxes;

        public int     rowIndex => _rowIndex;
        public int     colIndex => _colIndex;
        public int     wordIndex => _wordIndex;

        public int     currentCharacterIndex { get; private set; }

        private bool IsInitialized;

        public void Initialize () {
            actualPosition = position.GetValueOrDefault(Vector2.Zero);
            characterOffsetUnconstrained = characterOffset = new Vector2(xOffsetOfFirstLine, 0);
            initialLineXOffset = characterOffset.X;

            previousGlyphWasDead = suppress = suppressUntilNextLine = false;

            bufferWritePosition = 0;
            drawCallsWritten = 0;
            drawCallsSuppressed = 0;
            wordStartWritePosition = -1;
            wordStartOffset = Vector2.Zero;
            wordStartColumn = 0;
            _rowIndex = _colIndex = _wordIndex = 0;
            wordWrapSuppressed = false;
            initialLineSpacing = 0;
            currentBaseline = 0;
            currentLineSpacing = 0;
            maxLineSpacing = 0;
            currentXOverhang = 0;

            HitTests.Sort(LayoutHitTest.Comparer.Instance);
            for (int i = 0; i < HitTests.Count; i++) {
                var ht = HitTests[i];
                ht.FirstCharacterIndex = null;
                ht.LastCharacterIndex = null;
                HitTests[i] = ht;
            }

            Markers.Sort(LayoutMarker.Comparer.Instance);
            for (int i = 0; i < Markers.Count; i++) {
                var m = Markers[i];
                m.Bounds.UnsafeFastClear();
                Markers[i] = m;
            }

            currentCharacterIndex = 0;
            lastUsedTexture = null;
            boxes = default(DenseList<Bounds>);
            ComputeLineBreakAtX();

            IsInitialized = true;
        }

        private void ProcessHitTests (ref Bounds bounds, float centerX) {
            var characterIndex = currentCharacterIndex;
            for (int i = 0; i < HitTests.Count; i++) {
                var ht = HitTests[i];
                if (bounds.Contains(ht.Position)) {
                    if (!ht.FirstCharacterIndex.HasValue) {
                        ht.FirstCharacterIndex = characterIndex;
                        // FIXME: Why is this literally always wrong?
                        ht.LeaningRight = (ht.Position.X >= centerX);
                    }
                    ht.LastCharacterIndex = characterIndex;
                    HitTests[i] = ht;
                }
            }
        }

        private void ProcessMarkers (ref Bounds bounds, int currentCodepointSize, int? drawCallIndex, bool splitMarker, bool didWrapWord) {
            if (measureOnly)
                return;
            if (suppress || suppressUntilNextLine)
                return;

            var characterIndex1 = currentCharacterIndex - currentCodepointSize + 1;
            var characterIndex2 = currentCharacterIndex;
            for (int i = 0; i < Markers.Count; i++) {
                var m = Markers[i];
                if (m.FirstCharacterIndex > characterIndex2)
                    continue;
                if (m.LastCharacterIndex < characterIndex1)
                    continue;
                var curr = m.Bounds.LastOrDefault();
                if (curr != default(Bounds)) {
                    if (splitMarker && !didWrapWord) {
                        var newBounds = bounds;
                        if (m.CurrentSplitGlyphCount > 0) {
                            newBounds.TopLeft.X = Math.Min(curr.BottomRight.X, bounds.TopLeft.X);
                            newBounds.TopLeft.Y = Math.Min(curr.TopLeft.Y, bounds.TopLeft.Y);
                        }
                        m.CurrentSplitGlyphCount = 0;
                        m.Bounds.Add(newBounds);
                    } else if (didWrapWord && splitMarker && (m.CurrentSplitGlyphCount == 0)) {
                        m.Bounds[m.Bounds.Count - 1] = bounds;
                    } else {
                        var newBounds = Bounds.FromUnion(bounds, curr);
                        m.Bounds[m.Bounds.Count - 1] = newBounds;
                    }
                } else if (bounds != default(Bounds))
                    m.Bounds.Add(bounds);

                if (drawCallIndex != null) {
                    m.GlyphCount++;
                    m.CurrentSplitGlyphCount++;
                }

                m.FirstLineIndex = m.FirstLineIndex ?? _rowIndex;
                m.LastLineIndex = _rowIndex;
                m.FirstDrawCallIndex = m.FirstDrawCallIndex ?? drawCallIndex;
                m.LastDrawCallIndex = drawCallIndex ?? m.LastDrawCallIndex;
                Markers[i] = m;
            }
        }

        private void ProcessLineSpacingChange_Slow (in ArraySegment<BitmapDrawCall> buffer, float newLineSpacing, float newBaseline) {
            if (bufferWritePosition > baselineAdjustmentStart) {
                var yOffset = newBaseline - currentBaseline;
                for (int i = baselineAdjustmentStart; i < bufferWritePosition; i++) {
                    buffer.Array[buffer.Offset + i].Position.Y += yOffset * (1 - buffer.Array[buffer.Offset + i].UserData.W);
                }

                if (!measureOnly) {
                    for (int i = 0; i < Markers.Count; i++) {
                        var m = Markers[i];
                        if (m.Bounds.Count <= 0)
                            continue;
                        if (m.FirstCharacterIndex > bufferWritePosition)
                            continue;
                        if (m.LastCharacterIndex < baselineAdjustmentStart)
                            continue;
                        // FIXME
                        var b = m.Bounds.LastOrDefault();
                        b.TopLeft.Y += yOffset;
                        b.BottomRight.Y += yOffset;
                        m.Bounds[m.Bounds.Count - 1] = b;
                        Markers[i] = m;
                    }
                }
            }
            currentBaseline = newBaseline;
            baselineAdjustmentStart = bufferWritePosition;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ProcessLineSpacingChange (in ArraySegment<BitmapDrawCall> buffer, float newLineSpacing, float newBaseline) {
            if (newBaseline > currentBaseline)
                ProcessLineSpacingChange_Slow(buffer, newLineSpacing, newBaseline);

            if (newLineSpacing > currentLineSpacing)
                currentLineSpacing = newLineSpacing;

            ComputeLineBreakAtX();
        }

        private void WrapWord (
            ArraySegment<BitmapDrawCall> buffer,
            Vector2 firstOffset, int firstGlyphIndex, int lastGlyphIndex, 
            float glyphLineSpacing, float glyphBaseline, float currentWordSize
        ) {
            // FIXME: Can this ever happen?
            if (currentLineWhitespaceMaxX <= 0)
                maxX = Math.Max(maxX, currentLineMaxX);
            else
                maxX = Math.Max(maxX, currentLineWrapPointLeft);

            var previousLineSpacing = currentLineSpacing;
            var previousBaseline = currentBaseline;

            currentBaseline = glyphBaseline;
            initialLineSpacing = currentLineSpacing = glyphLineSpacing;

            // Remove the effect of the previous baseline adjustment then realign to our new baseline
            var yOffset = -previousBaseline + previousLineSpacing + currentBaseline;

            var suppressedByLineLimit = lineLimit.HasValue && (lineLimit.Value <= 0);
            var adjustment = Vector2.Zero;

            var xOffset = xOffsetOfWrappedLine;
            AdjustCharacterOffsetForBoxes(ref xOffset, characterOffset.Y + yOffset, currentLineSpacing, leftPad: 0f);
            var oldFirstGlyphBounds = (firstGlyphIndex > 0)
                ? buffer.Array[buffer.Offset + firstGlyphIndex - 1].EstimateDrawBounds()
                : default(Bounds);

            float wordX1 = 0, wordX2 = 0;

            for (var i = firstGlyphIndex; i <= lastGlyphIndex; i++) {
                var dc = buffer.Array[buffer.Offset + i];
                if ((dc.UserData.Y > 0) || (dc.UserData.Z != 0))
                    continue;
                var newCharacterX = (xOffset) + (dc.Position.X - firstOffset.X);
                if (i == firstGlyphIndex)
                    wordX1 = dc.Position.X;

                // FIXME: Baseline?
                var newPosition = new Vector2(newCharacterX, dc.Position.Y + yOffset);
                if (i == firstGlyphIndex)
                    adjustment = newPosition - dc.Position;
                dc.Position = newPosition;

                unchecked {
                    dc.LocalData2 = (byte)(dc.LocalData2 + 1);
                }

                if (i == lastGlyphIndex) {
                    var db = dc.EstimateDrawBounds();
                    wordX2 = db.BottomRight.X;
                }

                if (suppressedByLineLimit && hideOverflow)
                    // HACK: Just setting multiplycolor or scale etc isn't enough since a layout filter may modify it
                    buffer.Array[buffer.Offset + i] = default(BitmapDrawCall);
                else
                    buffer.Array[buffer.Offset + i] = dc;
            }

            // FIXME: If we hit a box on the right edge, this is broken
            characterOffset.X = xOffset + (characterOffset.X - firstOffset.X);
            characterOffset.Y += previousLineSpacing;

            // HACK: firstOffset may include whitespace so we want to pull the right edge in.
            //  Without doing this, the size rect for the string is too large.
            var actualRightEdge = firstOffset.X;
            var newFirstGlyphBounds = (firstGlyphIndex > 0)
                ? buffer.Array[buffer.Offset + firstGlyphIndex - 1].EstimateDrawBounds()
                : default(Bounds);
            if (firstGlyphIndex > 0)
                actualRightEdge = Math.Min(
                    actualRightEdge, newFirstGlyphBounds.BottomRight.X
                );

            // FIXME: This will break if the word mixes styles
            baselineAdjustmentStart = firstGlyphIndex;

            if (Markers.Count <= 0)
                return;

            // HACK: If a marker is inside of the wrapped word or around it, we need to adjust the marker to account
            //  for the fact that its anchoring characters have just moved
            for (int i = 0; i < Markers.Count; i++) {
                var m = Markers[i];
                Bounds oldBounds = m.Bounds.LastOrDefault(),
                    newBounds = oldBounds.Translate(adjustment);

                newBounds.TopLeft.X = (position?.X ?? 0) + xOffset;
                newBounds.TopLeft.Y = Math.Max(newBounds.TopLeft.Y, newBounds.BottomRight.Y - currentLineSpacing);

                if ((m.FirstDrawCallIndex == null) || (m.FirstDrawCallIndex > lastGlyphIndex))
                    continue;
                if (m.LastDrawCallIndex < firstGlyphIndex)
                    continue;
                if (m.Bounds.Count < 1)
                    continue;

                m.Bounds[m.Bounds.Count - 1] = newBounds;

                Markers[i] = m;
            }
        }

        private float AdjustCharacterOffsetForBoxes (ref float x, float y1, float h, float? leftPad = null) {
            if (boxes.Count < 1)
                return 0;

            Bounds b;
            float result = 0;
            var tempBounds = Bounds.FromPositionAndSize(x, y1, 1f, Math.Max(h, 1));
            if ((_rowIndex == 0) && (leftPad == null))
                leftPad = xOffsetOfFirstLine;
            for (int i = 0, c = boxes.Count; i < c; i++) {
                boxes.GetItem(i, out b);
                b.BottomRight.X += (leftPad ?? 0f);
                if (!Bounds.Intersect(b, tempBounds))
                    continue;
                var oldX = x;
                var newX = Math.Max(x, b.BottomRight.X);
                if (!currentLineBreakAtX.HasValue || (newX < currentLineBreakAtX.Value)) {
                    x = newX;
                    result += (oldX - x);
                }
            }
            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Snap (ref float value, PixelAlignmentMode mode) {
            switch (mode) {
                case PixelAlignmentMode.Floor:
                    value = (float)Math.Floor(value);
                    break;
                case PixelAlignmentMode.FloorHalf:
                    value = (float)Math.Floor(value * 2) / 2;
                    break;
                case PixelAlignmentMode.FloorQuarter:
                    value = (float)Math.Floor(value * 4) / 4;
                    break;
                case PixelAlignmentMode.Round:
                    value = (float)Math.Round(value, 0, MidpointRounding.AwayFromZero);
                    break;
                case PixelAlignmentMode.RoundHalf:
                    value = (float)Math.Round(value * 2, 0, MidpointRounding.AwayFromZero) / 2;
                    break;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Snap (ref float x) {
            Snap(ref x, alignToPixels.Horizontal);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Snap (Vector2 pos, out Vector2 result) {
            result = pos;
            Snap(ref result.X, alignToPixels.Horizontal);
            Snap(ref result.Y, alignToPixels.Vertical);
        }

        private void AlignLine (
            ArraySegment<BitmapDrawCall> buffer, int line, HorizontalAlignment globalAlignment,
            int firstIndex, int lastIndex, float originalMaxX
        ) {
            Bounds firstDc = default(Bounds), endDc = default(Bounds);
            int firstWord = 999999, lastWord = 0;
            for (int i = firstIndex; i <= lastIndex; i++) {
                var dc = buffer.Array[buffer.Offset + i];
                firstWord = Math.Min(firstWord, dc.LocalData1);
                lastWord = Math.Max(lastWord, dc.LocalData1);
                if (dc.UserData.X > 0)
                    continue;

                if (firstDc == default(Bounds))
                    firstDc = dc.EstimateDrawBounds();

                endDc = dc.EstimateDrawBounds();
            }

            int wordCountMinusOne = (firstWord < lastWord)
                ? lastWord - firstWord
                : 0; // FIXME: detect and handle wrap-around. Will only happen with very large word count

            // In justify mode if there is only one word or one character on the line, fall back to centering, otherwise
            //  the math will have some nasty divides by zero or one
            var localAlignment = globalAlignment;
            if (localAlignment >= HorizontalAlignment.JustifyWords) {
                if (wordCountMinusOne < 1) {
                    if (localAlignment == HorizontalAlignment.JustifyWordsCentered)
                        localAlignment = HorizontalAlignment.Center;
                    else
                        return;
                }
            } else if (localAlignment >= HorizontalAlignment.JustifyCharacters) {
                if (lastIndex <= (firstIndex + 1)) {
                    if (localAlignment == HorizontalAlignment.JustifyCharactersCentered)
                        localAlignment = HorizontalAlignment.Center;
                    else
                        return;
                }
            }

            float lineWidth = (endDc.BottomRight.X - firstDc.TopLeft.X),
                // FIXME: Why is this padding here?
                localMinX = firstDc.TopLeft.X - actualPosition.X, localMaxX = originalMaxX - 0.1f;

            if (
                currentLineBreakAtX.HasValue && expandHorizontallyWhenAligning &&
                // HACK: Expanding in justify mode doesn't make sense, especially if the expand was constrained
                (globalAlignment != HorizontalAlignment.JustifyCharacters) &&
                (globalAlignment != HorizontalAlignment.JustifyWords)
            )
                localMaxX = currentLineBreakAtX.Value;

            // Expand the line horizontally to fit the desired width
            // FIXME: This is still kind of busted and things are also busted with it turned off
            if (desiredWidth > 0)
                localMaxX = Math.Max(localMaxX, Math.Min(desiredWidth, currentLineBreakAtX ?? desiredWidth));

            // HACK: Attempt to ensure that alignment doesn't penetrate boxes
            // FIXME: This doesn't work and I can't figure out why
            /*
            AdjustCharacterOffsetForBoxes(ref localMinX, firstDc.TopLeft.Y, Math.Max(firstDc.Size.Y, endDc.Size.Y));
            AdjustCharacterOffsetForBoxes(ref localMaxX, firstDc.TopLeft.Y, Math.Max(firstDc.Size.Y, endDc.Size.Y));
            */

            float whitespace;
            // Factor in text starting offset from the left side, if we don't the text
            //  will overhang to the right after alignment. This is usually caused by boxes
            // FIXME: This doesn't seem to work anymore
            whitespace = localMaxX - lineWidth - localMinX;

            // HACK: Don't do anything if the line is too big, just overflow to the right.
            //  Otherwise, the sizing info will be wrong and bad things happen.
            if (whitespace <= 0)
                whitespace = 0;

            // HACK: We compute this before halving the whitespace, so that the size of 
            //  the layout is enough to ensure manually centering the whole layout will
            //  still preserve per-line centering.
            maxX = Math.Max(maxX, whitespace + lineWidth);

            if (localAlignment == HorizontalAlignment.Center)
                whitespace /= 2;

            // In JustifyCharacters mode we spread all the characters out to fill the line.
            // In JustifyWords mode we spread all the extra whitespace into the gaps between words.
            // In both cases the goal is for the last character of each line to end up flush
            //  against the right side of the layout box.
            float characterSpacing = 0, wordSpacing = 0, accumulatedSpacing = 0;
            if (localAlignment >= HorizontalAlignment.JustifyWords) {
                if (maxExpansion.HasValue && whitespace > maxExpansion.Value) {
                    whitespace = (localAlignment == HorizontalAlignment.JustifyWordsCentered)
                        ? whitespace / 2
                        : 0;
                } else {
                    wordSpacing = whitespace / wordCountMinusOne;
                    whitespace = 0;
                }
            } else if (localAlignment >= HorizontalAlignment.JustifyCharacters) {
                if (maxExpansion.HasValue && whitespace > maxExpansion.Value) {
                    whitespace = (localAlignment == HorizontalAlignment.JustifyCharactersCentered)
                        ? whitespace / 2
                        : 0;
                } else {
                    characterSpacing = whitespace / (lastIndex - firstIndex);
                    whitespace = 0;
                }
            }

            whitespace = (float)Math.Round(whitespace, alignToPixels.Horizontal != PixelAlignmentMode.Floor ? 2 : 0, MidpointRounding.AwayFromZero);

            // FIXME: This double-applies whitespace for some reason, or doesn't work at all?
            /*
            for (int j = 0, n = Markers.Count; j < n; j++) {
                Markers.TryGetItem(j, out LayoutMarker m);
                // FIXME: Multiline markers
                if ((m.FirstLineIndex >= line) && (m.LastLineIndex <= line)) {
                    for (int k = 0, kn = m.Bounds.Count; k < kn; k++) {
                        m.Bounds.TryGetItem(k, out Bounds b);
                        b.TopLeft.X += whitespace;
                        b.BottomRight.X += whitespace;
                        m.Bounds[k] = b;
                    }
                }
                Markers[j] = m;
            }
            */

            var previousWordIndex = firstWord;

            for (int j = firstIndex; j <= lastIndex; j++) {
                if (buffer.Array[buffer.Offset + j].UserData.X > 0)
                    continue;

                // If a word transition has happened, we want to shift the following characters
                //  over to the right to consume the extra whitespace in word justify mode.
                var currentWordIndex = (int)buffer.Array[buffer.Offset + j].LocalData1;
                if (currentWordIndex != previousWordIndex) {
                    previousWordIndex = currentWordIndex;
                    accumulatedSpacing += wordSpacing;
                }

                var computedOffset = whitespace + accumulatedSpacing;
                buffer.Array[buffer.Offset + j].Position.X += computedOffset;

                // In character justify mode we just spread all the characters out.
                accumulatedSpacing += characterSpacing;
            }

            for (int j = 0, c = Markers.Count; j < c; j++) {
                ref var marker = ref Markers.Item(j);
                // FIXME: Multiline boxes
                if ((marker.FirstLineIndex != line) || (marker.LastLineIndex != line))
                    continue;

                for (int k = 0, ck = marker.Bounds.Count; k < ck; k++) {
                    ref var b = ref marker.Bounds.Item(k);
                    b.TopLeft.X += whitespace;
                    b.BottomRight.X += whitespace;
                }
            }
        }

        private void AlignLines (
            ArraySegment<BitmapDrawCall> buffer, HorizontalAlignment alignment
        ) {
            if (buffer.Count == 0)
                return;

            int lineStartIndex = 0;
            int currentLine = buffer.Array[buffer.Offset].LocalData2;

            var originalMaxX = maxX;

            for (var i = 1; i < buffer.Count; i++) {
                var line = buffer.Array[buffer.Offset + i].LocalData2;

                if (line != currentLine) {
                    AlignLine(buffer, (int)currentLine, alignment, lineStartIndex, i - 1, originalMaxX);

                    lineStartIndex = i;
                    currentLine = line;
                }
            }

            AlignLine(buffer, _rowIndex, alignment, lineStartIndex, buffer.Count - 1, originalMaxX);
        }

        private void SnapPositions (ArraySegment<BitmapDrawCall> buffer) {
            for (var i = 0; i < buffer.Count; i++)
                Snap(buffer.Array[buffer.Offset + i].Position, out buffer.Array[buffer.Offset + i].Position);
        }

        private void EnsureBufferCapacity (int count) {
            int paddedCount = count + DefaultBufferPadding;

            if (buffer.Array == null) {
                ownsBuffer = true;
                buffer = allocator?.Allocate(paddedCount) ??
                    new ArraySegment<BitmapDrawCall>(new BitmapDrawCall[paddedCount]);
            } else if (buffer.Count < paddedCount) {
                if (ownsBuffer || (allocator != null)) {
                    var oldBuffer = buffer;
                    var newSize = UnorderedList<BitmapDrawCall>.PickGrowthSize(buffer.Count, paddedCount);
                    if (allocator != null)
                        buffer = allocator.Resize(buffer, newSize);
                    else {
                        buffer = new ArraySegment<BitmapDrawCall>(
                            new BitmapDrawCall[newSize]
                        );
                        Array.Copy(oldBuffer.Array, buffer.Array, oldBuffer.Count);
                    }
                } else if (buffer.Count >= count) {
                    // This is OK, there should be enough room...
                    ;
                } else {
                    throw new InvalidOperationException("Buffer too small");
                }
            }
        }

        public bool IsTruncated =>
            // FIXME: < 0 instead of <= 0?
            ((lineLimit ?? int.MaxValue) <= 0) ||
            ((lineBreakLimit ?? int.MaxValue) <= 0) ||
            ((characterLimit ?? int.MaxValue) <= 0);

        public void CreateBox (
            float width, float height, out Bounds box
        ) {
            box = Bounds.FromPositionAndSize(characterOffset.X, characterOffset.Y, width, height);
            CreateBox(ref box);
        }

        public void CreateBox (ref Bounds box) {
            boxes.Add(ref box);
        }

        /// <summary>
        /// Move the character offset forward as if an image of this size had been appended,
        ///  without actually appending anything
        /// </summary>
        public void Advance (
            float width, float height, bool doNotAdjustLineSpacing = false, bool considerBoxes = true
        ) {
            var lineSpacing = height;
            float x = characterOffset.X;
            if (!doNotAdjustLineSpacing)
                ProcessLineSpacingChange(buffer, lineSpacing, lineSpacing);
            var position = new Vector2(characterOffset.X, characterOffset.Y + currentBaseline);
            characterOffset.X += width;
            characterOffsetUnconstrained.X += width;
            if (_colIndex == 0) {
                characterOffset.X = Math.Max(characterOffset.X, 0);
                characterOffsetUnconstrained.X = Math.Max(characterOffsetUnconstrained.X, 0);
            }
            if (considerBoxes) {
                AdjustCharacterOffsetForBoxes(ref characterOffset.X, characterOffset.Y, Math.Max(lineSpacing, height));
                AdjustCharacterOffsetForBoxes(ref characterOffsetUnconstrained.X, characterOffsetUnconstrained.Y, Math.Max(lineSpacing, height));
            }
            currentLineMaxX = Math.Max(currentLineMaxX, x);
            currentLineMaxXUnconstrained = Math.Max(currentLineMaxXUnconstrained, x);
            if (characterSkipCount <= 0) {
                characterLimit--;
            } else {
                characterSkipCount--;
            }
        }

        /// <summary>
        /// Append an image as if it were a character
        /// </summary>
        /// <param name="verticalAlignment">Specifies the image's Y origin relative to the baseline</param>
        public void AppendImage (
            Texture2D texture, Bounds? textureRegion = null,
            Vector2? margin = null, 
            float scale = 1, float verticalAlignment = 1,
            Color? multiplyColor = null, bool doNotAdjustLineSpacing = false,
            bool createBox = false, float? hardXAlignment = null, float? hardYAlignment = null,
            float? overrideWidth = null, float? overrideHeight = null
        ) {
            float x = characterOffset.X, y = characterOffset.Y;

            var dc = new BitmapDrawCall {
                Position = Vector2.Zero,
                Texture = texture,
                SortKey = sortKey,
                TextureRegion = textureRegion ?? Bounds.Unit,
                ScaleF = scale * this.scale,
                MultiplyColor = multiplyColor ?? overrideColor ?? Color.White,
                AddColor = addColor,
                Origin = new Vector2(0, 0),
                // HACK
                UserData = new Vector4(
                    hardXAlignment.HasValue ? 1 : 0, 
                    hardYAlignment.HasValue ? 1 : 0, 
                    1, // This is an image
                    (hardYAlignment.HasValue ? 1 : 1 - verticalAlignment)
                )
            };
            clearUserData = true;
            var estimatedBounds = dc.EstimateDrawBounds();
            estimatedBounds.BottomRight.X = estimatedBounds.TopLeft.X + (overrideWidth ?? estimatedBounds.Size.X);
            estimatedBounds.BottomRight.Y = estimatedBounds.TopLeft.Y + (overrideHeight ?? estimatedBounds.Size.Y);
            var lineSpacing = estimatedBounds.Size.Y;
            if (!doNotAdjustLineSpacing)
                ProcessLineSpacingChange(buffer, lineSpacing, lineSpacing);
            float y1 = y,
                y2 = y + currentBaseline - estimatedBounds.Size.Y - (margin?.Y * 0.5f ?? 0);
            float? overrideX = null, overrideY = null;
            if (hardXAlignment.HasValue)
                overrideX = Arithmetic.Lerp(0, (lineBreakAtX ?? 0f) - estimatedBounds.Size.X, hardXAlignment.Value);
            if (hardYAlignment.HasValue)
                overrideY = Arithmetic.Lerp(0, (stopAtY ?? 0f) - estimatedBounds.Size.Y, hardYAlignment.Value);
            if (createBox)
                y2 = Math.Max(y1, y2);

            dc.Position = new Vector2(
                overrideX ?? x, 
                overrideY ?? Arithmetic.Lerp(y1, y2, verticalAlignment)
            );
            estimatedBounds = dc.EstimateDrawBounds();
            estimatedBounds.BottomRight.X = estimatedBounds.TopLeft.X + (overrideWidth ?? estimatedBounds.Size.X);
            estimatedBounds.BottomRight.Y = estimatedBounds.TopLeft.Y + (overrideHeight ?? estimatedBounds.Size.Y);
            var sizeX = (overrideWidth ?? estimatedBounds.Size.X) + (margin?.X ?? 0);
            if (!overrideX.HasValue) {
                characterOffset.X += sizeX;
                characterOffsetUnconstrained.X += sizeX;
                AdjustCharacterOffsetForBoxes(ref characterOffset.X, characterOffset.Y, currentLineSpacing);
                AdjustCharacterOffsetForBoxes(ref characterOffsetUnconstrained.X, characterOffsetUnconstrained.Y, currentLineSpacing);
            }
            dc.Position += actualPosition;
            // FIXME: Margins and stuff
            AppendDrawCall(ref dc, overrideX ?? x, 1, false, currentLineSpacing, 0f, x, ref estimatedBounds, false, false);
            maxY = Math.Max(maxY, characterOffset.Y + estimatedBounds.Size.Y);
            maxYUnconstrained = Math.Max(maxYUnconstrained, characterOffsetUnconstrained.Y + estimatedBounds.Size.Y);

            if (createBox) {
                var mx = (margin?.X ?? 0) / 2f;
                var my = (margin?.Y ?? 0) / 2f;
                estimatedBounds.TopLeft.X -= mx;
                estimatedBounds.TopLeft.Y -= my;
                estimatedBounds.BottomRight.X += mx;
                estimatedBounds.BottomRight.Y += my;
                CreateBox(ref estimatedBounds);
            }
        }

        private bool ComputeSuppress (bool? overrideSuppress) {
            if (suppressUntilNextLine)
                return true;
            return overrideSuppress ?? suppress;
        }

        public ArraySegment<BitmapDrawCall> AppendText<TGlyphSource> (
            TGlyphSource font, in AbstractString text,
            Dictionary<char, KerningAdjustment> kerningAdjustments = null,
            int? start = null, int? end = null, bool? overrideSuppress = null
        ) where TGlyphSource : IGlyphSource {
            if (!IsInitialized)
                throw new InvalidOperationException("Call Initialize first");

            if (!typeof(TGlyphSource).IsValueType) {
                if (font == null)
                    throw new ArgumentNullException("font");
            }
            if (text.IsNull)
                throw new ArgumentNullException("text");

            if (!measureOnly)
                EnsureBufferCapacity(bufferWritePosition + text.Length);

            if (kerningAdjustments == null)
                kerningAdjustments = StringLayout.GetDefaultKerningAdjustments(font);

            var effectiveScale = scale / Math.Max(0.0001f, font.DPIScaleFactor);
            var effectiveSpacing = spacing;

            var drawCall = new BitmapDrawCall {
                MultiplyColor = defaultColor,
                ScaleF = effectiveScale,
                SortKey = sortKey,
                AddColor = addColor
            };

            float x = 0;
            bool hasBoxes = boxes.Count > 0;

            for (int i = start ?? 0, l = Math.Min(end ?? text.Length, text.Length); i < l; i++) {
                if (lineLimit.HasValue && lineLimit.Value <= 0)
                    suppress = true;

                DecodeCodepoint(text, ref i, l, out char ch1, out int currentCodepointSize, out uint codepoint);

                AnalyzeWhitespace(
                    ch1, codepoint, out bool isWhiteSpace, out bool forcedWrap, out bool lineBreak, 
                    out bool deadGlyph, out bool isWordWrapPoint, out bool didWrapWord
                );

                if (isWordWrapPoint) {
                    _wordIndex++;
                    currentLineWrapPointLeft = Math.Max(currentLineWrapPointLeft, characterOffset.X);
                    if (isWhiteSpace)
                        wordStartWritePosition = -1;
                    else
                        wordStartWritePosition = bufferWritePosition;
                    wordStartOffset = characterOffset;
                    wordStartColumn = _colIndex;
                    wordWrapSuppressed = false;
                } else {
                    if (wordStartWritePosition < 0) {
                        wordStartWritePosition = bufferWritePosition;
                        wordStartOffset = characterOffset;
                        wordStartColumn = _colIndex;
                    }
                }

                BuildGlyphInformation(
                    font, kerningAdjustments, effectiveScale, effectiveSpacing, ch1, codepoint,
                    out deadGlyph, out Glyph glyph, out KerningAdjustment kerningAdjustment,
                    out float glyphLineSpacing, out float glyphBaseline
                );

                x =
                    characterOffset.X +
                    ((
                        glyph.WidthIncludingBearing + glyph.CharacterSpacing
                    ) * effectiveScale);

                if (x >= currentLineBreakAtX) {
                    if (
                        !deadGlyph &&
                        (_colIndex > 0) &&
                        !isWhiteSpace
                    )
                        forcedWrap = true;
                }

                if (forcedWrap)
                    PerformForcedWrap(x, ref lineBreak, ref didWrapWord, glyphLineSpacing, glyphBaseline);

                if (lineBreak)
                    PerformLineBreak(forcedWrap);

                // HACK: Recompute after wrapping
                x =
                    characterOffset.X +
                    (glyph.WidthIncludingBearing + glyph.CharacterSpacing) * effectiveScale;
                var yOffset = currentBaseline - glyphBaseline;
                var xUnconstrained = x - characterOffset.X + characterOffsetUnconstrained.X;

                if (deadGlyph || isWhiteSpace)
                    ProcessDeadGlyph(effectiveScale, x, isWhiteSpace, deadGlyph, glyph, yOffset);

                if (deadGlyph)
                    continue;

                if (!ComputeSuppress(overrideSuppress))
                    characterOffset.X += (glyph.CharacterSpacing * effectiveScale);
                characterOffsetUnconstrained.X += (glyph.CharacterSpacing * effectiveScale);
                // FIXME: Is this y/h right
                if (hasBoxes) {
                    AdjustCharacterOffsetForBoxes(ref characterOffset.X, characterOffset.Y, glyph.LineSpacing * effectiveScale);
                    AdjustCharacterOffsetForBoxes(ref characterOffsetUnconstrained.X, characterOffsetUnconstrained.Y, glyph.LineSpacing * effectiveScale);
                }

                // FIXME: Shift this stuff below into the append function
                var scaledGlyphSize = new Vector2(
                    glyph.WidthIncludingBearing,
                    glyph.LineSpacing
                ) * effectiveScale;

                if (!ComputeSuppress(overrideSuppress))
                    lastCharacterBounds = Bounds.FromPositionAndSize(
                        actualPosition + characterOffset + new Vector2(0, yOffset), scaledGlyphSize
                    );

                var testBounds = lastCharacterBounds;
                var centerX = (characterOffset.X + scaledGlyphSize.X) * 0.5f;
                // FIXME: boxes

                ProcessHitTests(ref testBounds, testBounds.Center.X);

                if ((_rowIndex == 0) && (_colIndex == 0))
                    firstCharacterBounds = lastCharacterBounds;

                if (!ComputeSuppress(overrideSuppress))
                    characterOffset.X += glyph.LeftSideBearing * effectiveScale;
                characterOffsetUnconstrained.X += glyph.LeftSideBearing * effectiveScale;

                // If a glyph has negative overhang on the right side we want to make a note of that,
                //  so that if a line ends with negative overhang we can expand the layout to include it.
                currentXOverhang = (glyph.RightSideBearing < 0) ? -glyph.RightSideBearing : 0;

                if (!measureOnly && !isWhiteSpace) {
                    var glyphPosition = new Vector2(
                        actualPosition.X + (glyph.XOffset * effectiveScale) + characterOffset.X,
                        actualPosition.Y + (glyph.YOffset * effectiveScale) + characterOffset.Y + yOffset
                    );
                    drawCall.Textures = new TextureSet(glyph.Texture);
                    drawCall.TextureRegion = glyph.BoundsInTexture;
                    drawCall.Position = glyphPosition;
                    drawCall.MultiplyColor = overrideColor ?? glyph.DefaultColor ?? defaultColor;
                }

                AppendDrawCall(
                    ref drawCall,
                    x, currentCodepointSize,
                    isWhiteSpace, glyphLineSpacing,
                    yOffset, xUnconstrained, ref testBounds,
                    lineBreak, didWrapWord, overrideSuppress
                );

                if (!ComputeSuppress(overrideSuppress))
                    characterOffset.X += (glyph.Width + glyph.RightSideBearing) * effectiveScale;
                characterOffsetUnconstrained.X += (glyph.Width + glyph.RightSideBearing) * effectiveScale;
                if (hasBoxes) {
                    AdjustCharacterOffsetForBoxes(ref characterOffset.X, characterOffset.Y, currentLineSpacing);
                    AdjustCharacterOffsetForBoxes(ref characterOffsetUnconstrained.X, characterOffsetUnconstrained.Y, currentLineSpacing);
                }
                ProcessLineSpacingChange(buffer, glyphLineSpacing, glyphBaseline);
                maxLineSpacing = Math.Max(maxLineSpacing, currentLineSpacing);

                currentCharacterIndex++;
                _colIndex += 1;
            }

            var segment = 
                measureOnly
                    ? default(ArraySegment<BitmapDrawCall>)
                    : new ArraySegment<BitmapDrawCall>(
                        buffer.Array, buffer.Offset, drawCallsWritten
                    );

            maxXUnconstrained = Math.Max(maxXUnconstrained, currentLineMaxXUnconstrained);
            maxX = Math.Max(maxX, currentLineMaxX);

            if (newLinePending) {
                var trailingSpace = currentLineSpacing;
                if (trailingSpace <= 0)
                    trailingSpace = font.LineSpacing;
                maxY += trailingSpace;
                maxYUnconstrained += trailingSpace;
                newLinePending = false;
            }

            return segment;
        }

        private void AnalyzeWhitespace (char ch1, uint codepoint, out bool isWhiteSpace, out bool forcedWrap, out bool lineBreak, out bool deadGlyph, out bool isWordWrapPoint, out bool didWrapWord) {
            isWhiteSpace = Unicode.IsWhiteSpace(ch1) && !replacementCodepoint.HasValue;
            forcedWrap = false;
            lineBreak = false;
            deadGlyph = false;
            didWrapWord = false;
            if (splitAtWrapCharactersOnly)
                isWordWrapPoint = (WordWrapCharacters.IndexOf(codepoint) >= 0);
            else
                isWordWrapPoint = isWhiteSpace || char.IsSeparator(ch1) ||
                    replacementCodepoint.HasValue || (WordWrapCharacters.IndexOf(codepoint) >= 0);

            if (codepoint > 255) {
                // HACK: Attempt to word-wrap at "other" punctuation in non-western character sets, which will include things like commas
                // This is less than ideal but .NET does not appear to expose the classification tables needed to do this correctly
                var category = CharUnicodeInfo.GetUnicodeCategory(ch1);
                if (category == UnicodeCategory.OtherPunctuation)
                    isWordWrapPoint = true;
            }

            if (ch1 == '\n')
                lineBreak = true;

            if (lineBreak) {
                if (lineLimit.HasValue) {
                    lineLimit--;
                    if (lineLimit.Value <= 0)
                        suppress = true;
                }
                if (lineBreakLimit.HasValue) {
                    lineBreakLimit--;
                    if (lineBreakLimit.Value <= 0)
                        suppress = true;
                }
                if (!suppress && includeTrailingWhitespace)
                    newLinePending = true;
            } else if (lineLimit.HasValue && lineLimit.Value <= 0) {
                suppress = true;
            }
        }

        private void DecodeCodepoint (in AbstractString text, ref int i, int l, out char ch1, out int currentCodepointSize, out uint codepoint) {
            char ch2 = i < (l - 1)
                    ? text[i + 1]
                    : '\0';
            ch1 = text[i];
            currentCodepointSize = 1;
            if (Unicode.DecodeSurrogatePair(ch1, ch2, out codepoint)) {
                currentCodepointSize = 2;
                currentCharacterIndex++;
                i++;
            } else if (ch1 == '\r') {
                if (ch2 == '\n') {
                    currentCodepointSize = 2;
                    ch1 = ch2;
                    i++;
                    currentCharacterIndex++;
                }
            }

            codepoint = replacementCodepoint ?? codepoint;
        }

        private void BuildGlyphInformation<TGlyphSource> (
            in TGlyphSource font, Dictionary<char, KerningAdjustment> kerningAdjustments, float effectiveScale, float effectiveSpacing, 
            char ch1, uint codepoint, out bool deadGlyph, out Glyph glyph, out KerningAdjustment kerningAdjustment, out float glyphLineSpacing, out float glyphBaseline
        ) where TGlyphSource : IGlyphSource {
            deadGlyph = !font.GetGlyph(codepoint, out glyph);

            glyphLineSpacing = glyph.LineSpacing * effectiveScale;
            glyphBaseline = glyph.Baseline * effectiveScale;
            if (deadGlyph) {
                if (currentLineSpacing > 0) {
                    glyphLineSpacing = currentLineSpacing;
                    glyphBaseline = currentBaseline;
                } else {
                    Glyph space;
                    if (font.GetGlyph(' ', out space)) {
                        glyphLineSpacing = space.LineSpacing * effectiveScale;
                        glyphBaseline = space.Baseline * effectiveScale;
                    }
                }
            }

            // glyph.LeftSideBearing *= effectiveSpacing;
            float leftSideDelta = 0;
            if (effectiveSpacing >= 0)
                glyph.LeftSideBearing *= effectiveSpacing;
            else
                leftSideDelta = Math.Abs(glyph.LeftSideBearing * effectiveSpacing);
            glyph.RightSideBearing *= effectiveSpacing;
            glyph.RightSideBearing -= leftSideDelta;

            if (initialLineSpacing <= 0)
                initialLineSpacing = glyphLineSpacing;
            ProcessLineSpacingChange(buffer, glyphLineSpacing, glyphBaseline);

            // FIXME: Don't key kerning adjustments off 'char'
            if (kerningAdjustments != null) {
                if (kerningAdjustments.TryGetValue(ch1, out kerningAdjustment)) {
                    glyph.LeftSideBearing += kerningAdjustment.LeftSideBearing;
                    glyph.Width += kerningAdjustment.Width;
                    glyph.RightSideBearing += kerningAdjustment.RightSideBearing;
                }
            } else {
                kerningAdjustment = default;
            }

            // MonoGame#1355 rears its ugly head: If a character with negative left-side bearing is at the start of a line,
            //  we need to compensate for the bearing to prevent the character from extending outside of the layout bounds
            if (_colIndex == 0) {
                if (glyph.LeftSideBearing < 0)
                    glyph.LeftSideBearing = 0;
            }
        }

        private void ProcessDeadGlyph (float effectiveScale, float x, bool isWhiteSpace, bool deadGlyph, Glyph glyph, float yOffset) {
            if (deadGlyph || isWhiteSpace) {
                var whitespaceBounds = Bounds.FromPositionAndSize(
                    new Vector2(characterOffset.X, characterOffset.Y + yOffset),
                    new Vector2(x - characterOffset.X, glyph.LineSpacing * effectiveScale)
                );
                // HACK: Why is this necessary?
                whitespaceBounds.TopLeft.Y = Math.Max(whitespaceBounds.TopLeft.Y, whitespaceBounds.BottomRight.Y - currentLineSpacing);

                // FIXME: is the center X right?
                // ProcessHitTests(ref whitespaceBounds, whitespaceBounds.Center.X);
                // HACK: AppendCharacter will invoke ProcessMarkers anyway
                // ProcessMarkers(ref whitespaceBounds, currentCodepointSize, null, false, didWrapWord);

                // Ensure that trailing spaces are factored into total size
                if (isWhiteSpace)
                    maxX = Math.Max(maxX, whitespaceBounds.BottomRight.X);
            }

            if (deadGlyph) {
                previousGlyphWasDead = true;
                currentCharacterIndex++;
                characterSkipCount--;
                if (characterLimit.HasValue)
                    characterLimit--;
            }

            if (isWhiteSpace) {
                previousGlyphWasDead = true;
                currentLineWrapPointLeft = Math.Max(currentLineWrapPointLeft, characterOffset.X);
                currentLineWhitespaceMaxX = Math.Max(currentLineWhitespaceMaxX, x);
            } else
                previousGlyphWasDead = false;
        }

        private void PerformLineBreak (bool forcedWrap) {
            // FIXME: We also want to expand markers to enclose the overhang
            currentLineMaxX += currentXOverhang;
            currentLineMaxXUnconstrained += currentXOverhang;

            if (!forcedWrap) {
                var spacingForThisLineBreak = currentLineSpacing + extraLineBreakSpacing;
                if (!suppress) {
                    characterOffset.X = xOffsetOfNewLine;
                    // FIXME: didn't we already do this?
                    characterOffset.Y += spacingForThisLineBreak;
                    maxX = Math.Max(maxX, currentLineMaxX);
                }
                characterOffsetUnconstrained.X = xOffsetOfNewLine;
                AdjustCharacterOffsetForBoxes(ref characterOffset.X, characterOffset.Y, spacingForThisLineBreak, leftPad: xOffsetOfNewLine);
                AdjustCharacterOffsetForBoxes(ref characterOffsetUnconstrained.X, characterOffsetUnconstrained.Y, spacingForThisLineBreak, leftPad: xOffsetOfNewLine);
                characterOffsetUnconstrained.Y += spacingForThisLineBreak;

                maxXUnconstrained = Math.Max(maxXUnconstrained, currentLineMaxXUnconstrained);
                currentLineMaxXUnconstrained = 0;
                initialLineSpacing = currentLineSpacing = 0;
                currentBaseline = 0;
                baselineAdjustmentStart = bufferWritePosition;
                suppressUntilNextLine = false;
            }

            ComputeLineBreakAtX();
            initialLineXOffset = characterOffset.X;
            if (!suppress) {
                currentLineMaxX = 0;
                currentLineWhitespaceMaxX = 0;
                currentLineWrapPointLeft = 0;
            }
            _rowIndex += 1;
            _colIndex = 0;
        }

        private void PerformForcedWrap (float x, ref bool lineBreak, ref bool didWrapWord, float glyphLineSpacing, float glyphBaseline) {
            var currentWordSize = x - wordStartOffset.X;

            if (
                wordWrap && !wordWrapSuppressed &&
                // FIXME: If boxes shrink the current line too far, we want to just keep wrapping until we have enough room
                //  instead of giving up
                (currentWordSize <= currentLineBreakAtX) &&
                (wordStartColumn > 0)
            ) {
                if (lineLimit.HasValue)
                    lineLimit--;
                WrapWord(buffer, wordStartOffset, wordStartWritePosition, bufferWritePosition - 1, glyphLineSpacing, glyphBaseline, currentWordSize);
                wordWrapSuppressed = true;
                lineBreak = true;
                didWrapWord = true;

                // FIXME: While this will abort when the line limit is reached, we need to erase the word we wrapped to the next line
                if (lineLimit.HasValue && lineLimit.Value <= 0)
                    suppress = true;
            } else if (characterWrap) {
                if (lineLimit.HasValue)
                    lineLimit--;
                characterOffset.X = xOffsetOfWrappedLine;
                AdjustCharacterOffsetForBoxes(ref characterOffset.X, characterOffset.Y, currentLineSpacing, leftPad: xOffsetOfWrappedLine);
                characterOffset.Y += currentLineSpacing;
                initialLineSpacing = currentLineSpacing = glyphLineSpacing;
                currentBaseline = glyphBaseline;
                baselineAdjustmentStart = bufferWritePosition;

                maxX = Math.Max(maxX, currentLineMaxX);
                wordStartWritePosition = bufferWritePosition;
                wordStartOffset = characterOffset;
                wordStartColumn = _colIndex;
                lineBreak = true;

                if (lineLimit.HasValue && lineLimit.Value <= 0)
                    suppress = true;
            } else if (hideOverflow) {
                // If wrapping is disabled but we've hit the line break boundary, we want to suppress glyphs from appearing
                //  until the beginning of the next line (i.e. hard line break), but continue performing layout
                suppressUntilNextLine = true;
            } else {
                // Just overflow. Hooray!
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ComputeLineBreakAtX () {
            if (!lineBreakAtX.HasValue)
                currentLineBreakAtX = null;
            else if (boxes.Count > 0)
                ComputeLineBreakAtX_Slow();
            else
                currentLineBreakAtX = lineBreakAtX.Value;
        }

        private void ComputeLineBreakAtX_Slow () {
            float rightEdge = lineBreakAtX.Value;
            var row = Bounds.FromPositionAndSize(0f, characterOffset.Y, lineBreakAtX.Value, currentLineSpacing);
            for (int i = 0, c = boxes.Count; i < c; i++) {
                ref var b = ref boxes.Item(i);
                // HACK
                if (b.BottomRight.X <= (rightEdge - 2f))
                    continue;
                if (!Bounds.Intersect(row, b))
                    continue;
                rightEdge = Math.Min(b.TopLeft.X, rightEdge);
            }
            currentLineBreakAtX = rightEdge;
        }

        private void AppendDrawCall (
            ref BitmapDrawCall drawCall, 
            float x, int currentCodepointSize, 
            bool isWhiteSpace, float glyphLineSpacing, float yOffset, 
            float xUnconstrained, ref Bounds testBounds, bool splitMarker, bool didWrapWord,
            bool? overrideSuppress = null
        ) {
            if (recordUsedTextures && 
                (drawCall.Textures.Texture1 != lastUsedTexture) && 
                (drawCall.Textures.Texture1 != null)
            ) {
                if (usedTextures == null)
                    throw new NullReferenceException("usedTextures must be set if recordUsedTextures is set");

                lastUsedTexture = drawCall.Textures.Texture1;
                int existingIndex = -1;
                for (int i = 0; i < usedTextures.Count; i++) {
                    if (usedTextures[i].Id == drawCall.Textures.Texture1.Id) {
                        existingIndex = i;
                        break;
                    }
                }

                if (existingIndex < 0)
                    usedTextures.Add(lastUsedTexture);
            }

            if (_colIndex == 0) {
                characterOffset.X = Math.Max(characterOffset.X, 0);
                characterOffsetUnconstrained.X = Math.Max(characterOffsetUnconstrained.X, 0);
            }

            if (stopAtY.HasValue && (characterOffset.Y >= stopAtY))
                suppress = true;

            if (characterSkipCount <= 0) {
                if (characterLimit.HasValue && characterLimit.Value <= 0)
                    suppress = true;

                if (!isWhiteSpace) {
                    unchecked {
                        drawCall.LocalData1 = (short)(_wordIndex % 32767);
                    }

                    if (!measureOnly) {
                        if (bufferWritePosition >= buffer.Count)
                            EnsureBufferCapacity(bufferWritePosition);

                        // So the alignment pass can detect rows
                        unchecked {
                            drawCall.LocalData2 = (byte)(_rowIndex % 256);
                        }

                        if (reverseOrder)
                            drawCall.SortOrder += 1;
                    }

                    newLinePending = false;

                    if (!ComputeSuppress(overrideSuppress)) {
                        if (!measureOnly) {
                            buffer.Array[buffer.Offset + bufferWritePosition] = drawCall;
                            ProcessMarkers(ref testBounds, currentCodepointSize, bufferWritePosition, splitMarker || previousGlyphWasDead, didWrapWord);
                            bufferWritePosition += 1;
                            drawCallsWritten += 1;
                        }
                        currentLineMaxX = Math.Max(currentLineMaxX, x);
                        maxY = Math.Max(maxY, characterOffset.Y + glyphLineSpacing);
                    } else {
                        drawCallsSuppressed++;
                    }

                    currentLineMaxXUnconstrained = Math.Max(currentLineMaxXUnconstrained, xUnconstrained);
                    maxYUnconstrained = Math.Max(maxYUnconstrained, (characterOffsetUnconstrained.Y + glyphLineSpacing));
                } else {
                    currentLineWrapPointLeft = Math.Max(currentLineWrapPointLeft, characterOffset.X);
                    currentLineWhitespaceMaxX = Math.Max(currentLineWhitespaceMaxX, x);

                    ProcessMarkers(ref testBounds, currentCodepointSize, null, splitMarker || previousGlyphWasDead, didWrapWord);
                }

                characterLimit--;
            } else {
                characterSkipCount--;
            }
        }

        private void FinishProcessingMarkers (ArraySegment<BitmapDrawCall> result) {
            if (measureOnly)
                return;

            // HACK: During initial layout we split each word of a marked region into
            //  separate bounds so that wrapping would work correctly. Now that we're
            //  done, we want to find words that weren't wrapped and weld their bounds
            //  together so the entire marked string will be one bounds (if possible).
            for (int i = 0; i < Markers.Count; i++) {
                var m = Markers[i];
                if (m.Bounds.Count <= 1)
                    continue;

                for (int j = m.Bounds.Count - 1; j >= 1; j--) {
                    var b1 = m.Bounds[j - 1];
                    var b2 = m.Bounds[j];
                    // HACK: Detect a wrap/line break
                    if (b2.TopLeft.Y >= b1.Center.Y)
                        continue;
                    var xDelta = b2.TopLeft.X - b1.BottomRight.X;
                    if (xDelta > 0.5f)
                        continue;
                    m.Bounds[j - 1] = Bounds.FromUnion(b1, b2);
                    m.Bounds.RemoveAt(j);
                }

                Markers[i] = m;
            }
        }

        public StringLayout Finish () {
            if (currentXOverhang > 0) {
                currentLineMaxX += currentXOverhang;
                currentLineMaxXUnconstrained += currentXOverhang;
                maxX = Math.Max(currentLineMaxX, maxX);
                maxXUnconstrained = Math.Max(currentLineMaxXUnconstrained, maxXUnconstrained);
            }

            var result = default(ArraySegment<BitmapDrawCall>);
            if (!measureOnly) {
                if (buffer.Array != null)
                    result = new ArraySegment<BitmapDrawCall>(
                        buffer.Array, buffer.Offset, drawCallsWritten
                    );

                if (alignment != HorizontalAlignment.Left)
                    AlignLines(result, alignment);
                else
                    SnapPositions(result);

                if (reverseOrder) {
                    for (int k = 0; k < Markers.Count; k++) {
                        var m = Markers[k];
                        var a = result.Count - m.FirstDrawCallIndex - 1;
                        var b = result.Count - m.LastDrawCallIndex - 1;
                        m.FirstDrawCallIndex = b;
                        m.LastDrawCallIndex = a;
                        Markers[k] = m;
                    }

                    int i = result.Offset;
                    int j = result.Offset + result.Count - 1;
                    while (i < j) {
                        var temp = result.Array[i];
                        temp.UserData = (temp.UserData.Z != 0) ? imageUserData : userData;
                        result.Array[i] = result.Array[j];
                        result.Array[j] = temp;
                        i++;
                        j--;
                    }
                } else if (clearUserData) {
                    for (int i = 0, l = result.Count; i < l; i++)
                        result.Array[i + result.Offset].UserData = 
                            (result.Array[i + result.Offset].UserData.Z != 0) ? imageUserData : userData;
                }
            }

            var endpointBounds = lastCharacterBounds;
            // FIXME: Index of last draw call?
            // FIXME: Codepoint size?
            ProcessMarkers(ref endpointBounds, 1, null, false, false);

            FinishProcessingMarkers(result);

            // HACK: Boxes are in local space so we have to offset them at the end
            for (int i = 0, c = boxes.Count; i < c; i++) {
                ref var box = ref boxes.Item(i);
                box.TopLeft += actualPosition;
                box.BottomRight += actualPosition;
            }

            maxX = Math.Max(maxX, desiredWidth);
            maxXUnconstrained = Math.Max(maxXUnconstrained, desiredWidth);

            return new StringLayout(
                position.GetValueOrDefault(), 
                new Vector2(maxX, maxY), new Vector2(maxXUnconstrained, maxYUnconstrained),
                maxLineSpacing,
                firstCharacterBounds, lastCharacterBounds,
                result, (lineLimit.HasValue && (lineLimit.Value <= 0)) || 
                    (lineBreakLimit.HasValue && (lineBreakLimit.Value <= 0)),
                wordIndex + 1, rowIndex + 1
            ) {
                Boxes = boxes,
                UsedTextures = usedTextures
            };
        }

        public void Dispose () {
        }
    }
}
