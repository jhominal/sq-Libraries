﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using Squared.Render.Evil;
using Squared.Render.Text;
using Squared.Util;
using Microsoft.Xna.Framework.Graphics;
using Squared.Game;
using Microsoft.Xna.Framework;
using System.Reflection;
using Squared.Util.Text;
using System.Globalization;

namespace Squared.Render.Text {
    public struct StringLayout {
        private static readonly ConditionalWeakTable<object, Dictionary<char, KerningAdjustment>> _DefaultKerningAdjustments =
            new ConditionalWeakTable<object, Dictionary<char, KerningAdjustment>>(); 

        public readonly Vector2 Position;
        /// <summary>
        /// The size of the layout's visible characters in their wrapped positions.
        /// </summary>
        public readonly Vector2 Size;
        /// <summary>
        /// The size that the layout would have had if it was unconstrained by wrapping and character/line limits.
        /// </summary>
        public readonly Vector2 UnconstrainedSize;
        public readonly float LineHeight;
        public readonly Bounds FirstCharacterBounds;
        public readonly Bounds LastCharacterBounds;
        public ArraySegment<BitmapDrawCall> DrawCalls;
        public List<AbstractTextureReference> UsedTextures;
        // TODO: Find a smaller representation for these, because this makes DynamicStringLayout big
        public DenseList<Bounds> Boxes;
        public readonly int WordCount, LineCount;
        public readonly bool WasLineLimited;

        public StringLayout (
            in Vector2 position, in Vector2 size, in Vector2 unconstrainedSize, 
            float lineHeight, in Bounds firstCharacter, in Bounds lastCharacter, 
            ArraySegment<BitmapDrawCall> drawCalls, bool wasLineLimited,
            int wordCount, int lineCount
        ) {
            Position = position;
            Size = size;
            UnconstrainedSize = unconstrainedSize;
            LineHeight = lineHeight;
            FirstCharacterBounds = firstCharacter;
            LastCharacterBounds = lastCharacter;
            DrawCalls = drawCalls;
            WasLineLimited = wasLineLimited;
            Boxes = default(DenseList<Bounds>);
            UsedTextures = null;
            WordCount = wordCount;
            LineCount = lineCount;
        }

        public int Count {
            get {
                return DrawCalls.Count;
            }
        }

        public BitmapDrawCall this[int index] {
            get {
                if ((index < 0) || (index >= Count))
                    throw new ArgumentOutOfRangeException("index");

                return DrawCalls.Array[DrawCalls.Offset + index];
            }
            set {
                if ((index < 0) || (index >= Count))
                    throw new ArgumentOutOfRangeException("index");

                DrawCalls.Array[DrawCalls.Offset + index] = value;
            }
        }

        public ArraySegment<BitmapDrawCall> Slice (int skip, int count) {
            return new ArraySegment<BitmapDrawCall>(
                DrawCalls.Array, DrawCalls.Offset + skip, Math.Max(Math.Min(count, DrawCalls.Count - skip), 0)
            );
        }

        public static implicit operator ArraySegment<BitmapDrawCall> (StringLayout layout) {
            return layout.DrawCalls;
        }

        public static Dictionary<char, KerningAdjustment> GetDefaultKerningAdjustments<TGlyphSource> (TGlyphSource font)
            where TGlyphSource : IGlyphSource
        {
            var key = font.UniqueKey;
            if (key == null)
                return null;

            Dictionary<char, KerningAdjustment> result;
            _DefaultKerningAdjustments.TryGetValue(key, out result);
            return result;
        }

        public static void SetDefaultKerningAdjustments (SpriteFont font, Dictionary<char, KerningAdjustment> adjustments) {
            _DefaultKerningAdjustments.Remove(font);
            _DefaultKerningAdjustments.Add(font, adjustments);
        }
    }

    public struct KerningAdjustment {
        public float LeftSideBearing, RightSideBearing, Width;

        public KerningAdjustment (float leftSide = 0f, float rightSide = 0f, float width = 0f) {
            LeftSideBearing = leftSide;
            RightSideBearing = rightSide;
            Width = width;
        }
    }

    public enum HorizontalAlignment : byte {
        Left,
        Center,
        Right,
        JustifyCharacters,
        JustifyCharactersCentered,
        JustifyWords,
        JustifyWordsCentered
    }

    public struct LayoutMarker {
        public sealed class Comparer : IRefComparer<LayoutMarker> {
            public static readonly Comparer Instance = new Comparer();

            public int Compare (ref LayoutMarker lhs, ref LayoutMarker rhs) {
                var result = lhs.FirstCharacterIndex.CompareTo(rhs.FirstCharacterIndex);
                if (result == 0)
                    result = lhs.LastCharacterIndex.CompareTo(rhs.LastCharacterIndex);
                return result;
            }
        }

        // Inputs
        public AbstractString MarkedString, MarkedStringActualText;
        public string MarkedID;
        public int FirstCharacterIndex, LastCharacterIndex;

        // Outputs
        public int? FirstDrawCallIndex, LastDrawCallIndex;
        public int? FirstLineIndex, LastLineIndex;
        public int GlyphCount;
        internal int CurrentSplitGlyphCount;
        public DenseList<Bounds> Bounds;

        public LayoutMarker (int firstIndex, int lastIndex) {
            FirstCharacterIndex = firstIndex;
            LastCharacterIndex = lastIndex;
            MarkedString = default;
            MarkedStringActualText = default;
            MarkedID = null;
            FirstDrawCallIndex = LastDrawCallIndex = null;
            FirstLineIndex = LastLineIndex = null;
            GlyphCount = CurrentSplitGlyphCount = 0;
            Bounds = default(DenseList<Bounds>);
        }

        public Bounds UnionBounds {
            get {
                if (Bounds.Count <= 1)
                    return Bounds.LastOrDefault();
                var b = Bounds[0];
                for (int i = 1; i < Bounds.Count; i++)
                    b = Squared.Game.Bounds.FromUnion(b, Bounds[i]);
                return b;
            }
        }

        public override string ToString () {
            return $"{MarkedID ?? "marker"} [{FirstCharacterIndex} - {LastCharacterIndex}] -> [{FirstDrawCallIndex} - {LastDrawCallIndex}] {Bounds.FirstOrDefault()}";
        }
    }

    public struct LayoutHitTest {
        public sealed class Comparer : IRefComparer<LayoutHitTest> {
            public static readonly Comparer Instance = new Comparer();

            public int Compare (ref LayoutHitTest lhs, ref LayoutHitTest rhs) {
                var result = lhs.Position.X.CompareTo(rhs.Position.X);
                if (result == 0)
                    result = lhs.Position.Y.CompareTo(rhs.Position.Y);
                return result;
            }
        }

        public object Tag;
        public Vector2 Position;
        public int? FirstCharacterIndex, LastCharacterIndex;
        public bool LeaningRight;

        public LayoutHitTest (Vector2 position, object tag = null) {
            Position = position;
            Tag = tag;
            FirstCharacterIndex = LastCharacterIndex = null;
            LeaningRight = false;
        }

        public override string ToString () {
            return $"{Tag ?? "hitTest"} {Position} -> {FirstCharacterIndex} leaning {(LeaningRight ? "right" : "left")}";
        }
    }
}

namespace Squared.Render {
    public static class SpriteFontExtensions {
        public static StringLayout LayoutString (
            this SpriteFont font, in AbstractString text, ArraySegment<BitmapDrawCall>? buffer = null,
            in Vector2? position = null, Color? color = null, float scale = 1, 
            DrawCallSortKey sortKey = default(DrawCallSortKey),
            int characterSkipCount = 0, int? characterLimit = null,
            float xOffsetOfFirstLine = 0, float? lineBreakAtX = null,
            GlyphPixelAlignment alignToPixels = default(GlyphPixelAlignment),
            Dictionary<char, KerningAdjustment> kerningAdjustments = null,
            bool wordWrap = false,
            bool reverseOrder = false, HorizontalAlignment? horizontalAlignment = null,
            Color? addColor = null
        ) {
            var state = new StringLayoutEngine {
                allocator = UnorderedList<BitmapDrawCall>.DefaultAllocator.Instance,
                position = position,
                defaultColor = color ?? Color.White,
                scale = scale,
                sortKey = sortKey,
                characterSkipCount = characterSkipCount,
                characterLimit = characterLimit,
                xOffsetOfFirstLine = xOffsetOfFirstLine,
                lineBreakAtX = lineBreakAtX,
                alignToPixels = alignToPixels,
                characterWrap = lineBreakAtX.HasValue,
                wordWrap = wordWrap,
                buffer = buffer.GetValueOrDefault(default(ArraySegment<BitmapDrawCall>)),
                reverseOrder = reverseOrder,
                addColor = addColor ?? Color.Transparent
            };
            var gs = new SpriteFontGlyphSource(font);

            if (horizontalAlignment.HasValue)
                state.alignment = horizontalAlignment.Value;

            state.Initialize();

            using (state) {
                var segment = state.AppendText(
                    gs, text, kerningAdjustments
                );

                return state.Finish();
            }
        }

        // Yuck :(
        public static StringLayout LayoutString<TGlyphSource> (
            this TGlyphSource glyphSource, in AbstractString text, ArraySegment<BitmapDrawCall>? buffer = null,
            in Vector2? position = null, Color? color = null, float scale = 1, 
            DrawCallSortKey sortKey = default(DrawCallSortKey),
            int characterSkipCount = 0, int? characterLimit = null,
            float xOffsetOfFirstLine = 0, float? lineBreakAtX = null,
            bool alignToPixels = false,
            Dictionary<char, KerningAdjustment> kerningAdjustments = null,
            bool wordWrap = false,
            bool reverseOrder = false, HorizontalAlignment? horizontalAlignment = null,
            Color? addColor = null
        ) where TGlyphSource : IGlyphSource {
            var state = new StringLayoutEngine {
                allocator = UnorderedList<BitmapDrawCall>.DefaultAllocator.Instance,
                position = position,
                defaultColor = color ?? Color.White,
                scale = scale,
                sortKey = sortKey,
                characterSkipCount = characterSkipCount,
                characterLimit = characterLimit,
                xOffsetOfFirstLine = xOffsetOfFirstLine,
                lineBreakAtX = lineBreakAtX,
                alignToPixels = alignToPixels,
                characterWrap = lineBreakAtX.HasValue,
                wordWrap = wordWrap,
                buffer = buffer.GetValueOrDefault(default(ArraySegment<BitmapDrawCall>)),
                reverseOrder = reverseOrder,
                addColor = addColor ?? Color.Transparent
            };

            if (horizontalAlignment.HasValue)
                state.alignment = horizontalAlignment.Value;

            state.Initialize();

            using (state) {
                var segment = state.AppendText(
                    glyphSource, text, kerningAdjustments
                );

                return state.Finish();
            }
        }
    }

    namespace Text {
        public enum PixelAlignmentMode {
            None = 0,
            /// <summary>
            /// Snaps positions to 0 or 1
            /// </summary>
            Floor = 1,
            /// <summary>
            /// Allows [0, 0.5, 1] instead of [0, 1]
            /// </summary>
            FloorHalf = 2,
            /// <summary>
            /// Allows [0, 0.25, 0.5, 0.75, 1]
            /// </summary>
            FloorQuarter = 4,
            /// <summary>
            /// Rounds to 0 or 1
            /// </summary>
            Round = 5,
            /// <summary>
            /// Rounds to 0, 0.5, or 1
            /// </summary>
            RoundHalf = 6,
            /// <summary>
            /// Uses the default value set on the glyph source, if possible, otherwise None
            /// </summary>
            Default = 10,
        }

        public struct GlyphPixelAlignment : IEquatable<GlyphPixelAlignment> {
            public PixelAlignmentMode Horizontal, Vertical;

            public GlyphPixelAlignment (bool alignToPixels) {
                Horizontal = Vertical = alignToPixels ? PixelAlignmentMode.Floor : PixelAlignmentMode.None;
            }

            public GlyphPixelAlignment (PixelAlignmentMode mode) {
                Horizontal = Vertical = mode;
            }

            public GlyphPixelAlignment (PixelAlignmentMode horizontal, PixelAlignmentMode vertical) {
                Horizontal = horizontal;
                Vertical = vertical;
            }

            public static implicit operator GlyphPixelAlignment (bool alignToPixels) {
                return new GlyphPixelAlignment(alignToPixels);
            }

            public static readonly GlyphPixelAlignment None = new GlyphPixelAlignment(PixelAlignmentMode.None);
            public static readonly GlyphPixelAlignment Default = new GlyphPixelAlignment(PixelAlignmentMode.Default);
            public static readonly GlyphPixelAlignment RoundXY = new GlyphPixelAlignment(PixelAlignmentMode.Round);
            public static readonly GlyphPixelAlignment FloorXY = new GlyphPixelAlignment(PixelAlignmentMode.Floor);
            public static readonly GlyphPixelAlignment FloorY = new GlyphPixelAlignment(PixelAlignmentMode.None, PixelAlignmentMode.Floor);

            public bool Equals (GlyphPixelAlignment other) {
                return (other.Horizontal == Horizontal) && (other.Vertical == Vertical);
            }

            public override int GetHashCode () {
                return Horizontal.GetHashCode() ^ Vertical.GetHashCode();
            }

            public override bool Equals (object obj) {
                if (obj is GlyphPixelAlignment)
                    return Equals((GlyphPixelAlignment)obj);

                return false;
            }

            public override string ToString () {
                if (Horizontal == Vertical)
                    return Horizontal.ToString();
                else
                    return string.Format("{0}, {1}", Horizontal, Vertical);
            }

            internal GlyphPixelAlignment Or (GlyphPixelAlignment? defaultAlignment) {
                return new GlyphPixelAlignment {
                    Horizontal = (Horizontal == PixelAlignmentMode.Default) 
                        ? (defaultAlignment?.Horizontal ?? PixelAlignmentMode.None)
                        : Horizontal,
                    Vertical = (Vertical == PixelAlignmentMode.Default) 
                        ? (defaultAlignment?.Vertical ?? PixelAlignmentMode.None)
                        : Vertical,
                };
            }
        }
    }
}