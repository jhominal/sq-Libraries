﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Squared.PRGUI.Decorations;
using Squared.Render;
using Squared.Render.Convenience;
using Squared.Util;

namespace Squared.PRGUI.Controls {
    public interface IParameterEditor {
        object Minimum { get; set; }
        object Maximum { get; set; }
        object Increment { get; set; }
        object Value { get; set; }
        bool TrySetValue (object value, bool forUserInput);
        Type ValueType { get; }
        bool ReadOnly { get; set; }
        bool IntegerOnly { get; set; }
        bool DoubleOnly { get; set; }
        string Description { get; set; }
        int DecimalDigits { get; set; }
    }

    public static class ParameterEditor {
        public static IParameterEditor Create (Type valueType, Delegate tryParseValue = null, Delegate compare = null) {
            var type = typeof(ParameterEditor<>).MakeGenericType(valueType);
            return (IParameterEditor)Activator.CreateInstance(type, tryParseValue, compare);
        }

        public delegate bool TryParseDelegate (string value, out object result);

        public static TryParseDelegate GetParseDelegate (Type type) {
            var tryParse = type.GetMethod(
                "TryParse", new [] { typeof(string), typeof(object).MakeByRefType() }
            );
            if (tryParse != null)
                return (TryParseDelegate)Delegate.CreateDelegate(typeof(TryParseDelegate), tryParse);

            return null;
        }
    }

    public class ParameterEditor<T> : EditableText, IScrollableControl, IParameterEditor, IValueControl<T>
        where T : struct
    {
        public delegate bool TypedTryParseDelegate2 (string value, IFormatProvider provider, out T result);
        public delegate bool TypedTryParseDelegate (string value, out T result);

        public const double NormalAccelerationMultiplier = 1.0,
            NormalRepeatSpeed = 0.75,
            FastAccelerationMultiplier = 0.85,
            FastRepeatSpeed = 0.55;

        // FIXME: Use a decorator for this?
        public const float ArrowWidth = 10,
            ArrowHeight = 18,
            ArrowPadding = ArrowWidth + 4;

        /// <summary>
        /// Number of trailing digits after the decimal point for floating-point types
        /// </summary>
        public int DecimalDigits { get; set; } = 3;

        public bool HasValue { get; private set; }
        private T _Value;
        private T? _Minimum, _Maximum;

        private bool IsDraggingGauge = false;

        public bool ClampToMinimum = true,
            ClampToMaximum = true;

        private double LastRepeatTimestamp;

        // FIXME: If for some reason your FastIncrementRate is very large, pgup/pgdn will be slow
        public int FastIncrementRate = 10;
        public T? Increment;

        public double? Exponent;

        public IFormatProvider FormatProvider;
        public Func<T, T?> ValueFilter;
        public Func<T, string> ValueEncoder;

        public static readonly Delegate DefaultTryParseValue;
        public static readonly Comparison<T> DefaultCompare;
        public static readonly Delegate DefaultFormatter;

        private Delegate _TryParseValue;
        private Comparison<T> _Compare;

        public TypedTryParseDelegate2 ValueDecoder2 {
            get => _TryParseValue as TypedTryParseDelegate2;
            set => _TryParseValue = value;
        }
        public TypedTryParseDelegate ValueDecoder {
            get => _TryParseValue as TypedTryParseDelegate;
            set => _TryParseValue = value;
        }

        static bool AwfulTryParseValue (string text, out T result) {
            try {
                result = (T)Convert.ChangeType(text, typeof(T));
                return true;
            } catch {
                result = default;
                return false;
            }
        }

        private static MethodInfo FindMethod (Type[] searchTypes, string name, BindingFlags flags, Type[] parameterTypes) {
            foreach (var type in searchTypes) {
                var result = type.GetMethod(name, flags, null, parameterTypes, null);
                if (result != null)
                    return result;
            }
            return null;
        }

        static ParameterEditor () {
            var staticFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            var searchTypes = new[] { typeof(T), typeof(Game.Geometry), typeof(Game.GameExtensionMethods) };

            var tryParse3 = FindMethod(searchTypes, "TryParse", staticFlags, new[] { typeof(string), typeof(IFormatProvider), typeof(T).MakeByRefType() });
            var tryParse = FindMethod(searchTypes, "TryParse", staticFlags, new[] { typeof(string), typeof(T).MakeByRefType() });
            if (tryParse3 != null)
                DefaultTryParseValue = (TypedTryParseDelegate2)Delegate.CreateDelegate(typeof(TypedTryParseDelegate2), null, tryParse3);
            else if (tryParse != null)
                DefaultTryParseValue = (TypedTryParseDelegate)Delegate.CreateDelegate(typeof(TypedTryParseDelegate), null, tryParse);
            else 
                DefaultTryParseValue = ParameterEditor.GetParseDelegate(typeof(T));

            if (DefaultTryParseValue == null)
                DefaultTryParseValue = (TypedTryParseDelegate)AwfulTryParseValue;

            if (DefaultCompare == null) {
                var compareTo = FindMethod(searchTypes, "CompareTo", staticFlags, new[] { typeof(T), typeof(T) }) ??
                    FindMethod(searchTypes, "Compare", staticFlags, new[] { typeof(T), typeof(T) });
                if (compareTo != null)
                    DefaultCompare = (Comparison<T>)Delegate.CreateDelegate(typeof(Comparison<T>), null, compareTo);
            }

            if (DefaultCompare == null) {
                if (typeof(IComparable).IsAssignableFrom(typeof(T)) || typeof(IComparable<T>).IsAssignableFrom(typeof(T))) {
                    // FIXME: There are probably other cases where this will work
                    try {
                        var dc = (Comparison<T>)Comparer<T>.Default.Compare;
                        var temp = Activator.CreateInstance<T>();
                        if (dc(temp, temp) == 0)
                            DefaultCompare = dc;
                    } catch {
                    }
                }
            }

            if (DefaultFormatter == null) {
                var format2 = FindMethod(searchTypes, "ToString", staticFlags, new[] { typeof(T), typeof(IFormatProvider) });
                if (format2 != null)
                    DefaultFormatter = (Func<T, IFormatProvider, string>)Delegate.CreateDelegate(typeof(Func<T, IFormatProvider, string>), null, format2);
            }

            if (DefaultFormatter == null) {
                var format1 = FindMethod(searchTypes, "ToString", staticFlags, new[] { typeof(T) });
                if (format1 != null)
                    DefaultFormatter = (Func<T, string>)Delegate.CreateDelegate(typeof(Func<T, string>), null, format1);
            }
        }

        protected bool TryParseValue (string text, out T result) {
            if (_TryParseValue is TypedTryParseDelegate2 ttpd2)
                return ttpd2(text, FormatProvider, out result);
            else if (_TryParseValue is TypedTryParseDelegate ttpd)
                return ttpd(text, out result);
            else if (_TryParseValue is ParameterEditor.TryParseDelegate tpd) {
                if (tpd(text, out object temp)) {
                    result = (T)temp;
                    return true;
                }
            }

            result = default;
            return false;
        }

        bool IsSettingValue;
        public T Value {
            get => _Value;
            set {
                SetValue(value, false);
            }
        }

        public Type ValueType => typeof(T);

        bool IParameterEditor.TrySetValue (object value, bool forUserInput) {
            if (value is T tv) {
                return SetValue(tv, forUserInput);
            }

            return false;
        }

        private int CompareTo (T lhs, T rhs) {
            if (_Compare != null)
                return _Compare(lhs, rhs);
            else if (Equals(lhs, rhs))
                return 0;
            else // YUCK
                return -1;
        }

        public bool SetValue (T value, bool forUserInput) {
            if (ReadOnly && forUserInput)
                return false;

            try {
                IsSettingValue = true;
                var clamped = ClampValue(value);
                if (clamped == null)
                    return false;

                value = clamped.Value;
                if ((CompareTo(_Value, value) == 0) && HasValue)
                    return true;

                CachedFractionD = null;
                HasValue = true;
                _Value = value;

                var newText = ValueEncoder(value);
                if (Text != newText) {
                    SetText(newText, true);
                    SelectNone();
                }

                if (forUserInput)
                    FireEvent(UIEvents.ValueChangedByUser, true);

                return true;
            } finally {
                IsSettingValue = false;
            }
        }

        public T? Minimum {
            get => _Minimum;
            set {
                if (
                    (value.HasValue == _Minimum.HasValue) &&
                    (
                        (value.HasValue && (CompareTo(value.Value, _Minimum.Value) == 0)) ||
                        !value.HasValue
                    )
                )
                    return;
                CachedFractionD = null;
                _Minimum = value;
                if (HasValue)
                    Value = Value;
            }
        }
        public T? Maximum {
            get => _Maximum;
            set {
                if (
                    (value.HasValue == _Maximum.HasValue) &&
                    (
                        (value.HasValue && (CompareTo(value.Value, _Maximum.Value) == 0)) ||
                        !value.HasValue
                    )
                )
                    return;
                CachedFractionD = null;
                _Maximum = value;
                if (HasValue)
                    Value = Value;
            }
        }

        object IParameterEditor.Minimum {
            get => Minimum;
            set => Minimum = (T?)value;
        }
        object IParameterEditor.Maximum {
            get => Maximum;
            set => Maximum = (T?)value;
        }
        object IParameterEditor.Increment {
            get => Increment;
            set => Increment = (T?)value;
        }
        object IParameterEditor.Value {
            get => Value;
            set => Value = (T)value;
        }

        public ParameterEditor ()
            : this (null, null) {
        }

        public ParameterEditor (TypedTryParseDelegate tryParse, Comparison<T> compare = null) {
            var t = typeof(T);
            if (!t.IsValueType)
                throw new ArgumentException("T must be a value type");

            ClampVirtualPositionToTextbox = false;
            AllowScroll = false;

            if (t == typeof(double) || t == typeof(float))
                DoubleOnly = true;
            else if (t == typeof(int) || t == typeof(long))
                IntegerOnly = true;

            FormatProvider = (IFormatProvider)CultureInfo.CurrentUICulture.NumberFormat.Clone();

            _TryParseValue = tryParse ?? DefaultTryParseValue;
            _Compare = compare ?? DefaultCompare;

            ValueEncoder = (v) => {
                var nfi = FormatProvider as NumberFormatInfo;
                if (nfi != null) {
                    // HACK
                    nfi.NumberGroupSeparator = "";
                    nfi.NumberDecimalDigits = IntegerOnly ? 0 : DecimalDigits;
                }
                if (DefaultFormatter is Func<T, string> f)
                    return f(v);
                else if (DefaultFormatter is Func<T, IFormatProvider, string> fp)
                    return fp(v, FormatProvider);
                else
                    return string.Format(FormatProvider, "{0:N}", v);
            };
            SelectAllOnFocus = true;
            SelectNoneOnFocusLoss = true;
        }

        private T? ClampValue (T value) {
            if (_Compare != null) {
                // FIXME: Throw
                if (ClampToMinimum && _Minimum.HasValue && (CompareTo(value, _Minimum.Value) < 0))
                    value = _Minimum.Value;
                if (ClampToMaximum && _Maximum.HasValue && (CompareTo(value, _Maximum.Value) > 0))
                    value = _Maximum.Value;
            }

            if (ValueFilter != null)
                return ValueFilter(value);
            else
                return value;
        }

        protected override void OnValueChanged (bool fromUserInput) {
            try {
                if (!IsSettingValue) {
                    T newValue;
                    if (!TryParseValue(Text, out var parsed))
                        return;
                    newValue = parsed;
                    var converted = ClampValue(newValue);
                    if (converted == null)
                        return;
                    HasValue = true;
                    _Value = converted.Value;
                }

                FireEvent(UIEvents.ValueChanged);
                if (fromUserInput)
                    FireEvent(UIEvents.ValueChangedByUser);
            } catch {
            }
        }

        private void FinalizeValue () {
            OnValueChanged(false);
            SetText(ValueEncoder(_Value), true);
        }

        protected override void ComputeAppearanceSpacing (
            ref UIOperationContext context, IDecorator decorations, 
            out Margins scaledMargins, out Margins scaledPadding, out Margins unscaledPadding
        ) {
            base.ComputeAppearanceSpacing(ref context, decorations, out scaledMargins, out scaledPadding, out unscaledPadding);

            if (Increment.HasValue) {
                unscaledPadding.Left += ArrowPadding;
                unscaledPadding.Right += ArrowPadding;
            }

            var gauge = context.DecorationProvider.ParameterGauge;
            if (gauge == null)
                return;

            if (Minimum.HasValue && Maximum.HasValue) {
                ComputeEffectiveScaleRatios(context.DecorationProvider, out Vector2 paddingScale, out Vector2 marginScale, out Vector2 sizeScale);
                var y1 = (gauge.Padding.Top * sizeScale.Y) + (gauge.Margins.Bottom * marginScale.Y);
                var y2 = (gauge.Padding.Bottom * sizeScale.Y) + (gauge.Margins.Top * marginScale.Y);

                // If there's a gauge, adjust our content box based on its padding so that it doesn't overlap
                //  our text or selection box
                if (gauge.Padding.Top > 0)
                    scaledPadding.Top = 0;
                if (gauge.Padding.Bottom > 0)
                    scaledPadding.Bottom = 0;
                if (gauge.Padding.Top > 0)
                    unscaledPadding.Top += y1;
                if (gauge.Padding.Bottom > 0)
                    unscaledPadding.Bottom += y2;
            }
        }

        void RasterizeArrow (ref ImperativeRenderer renderer, RectF box, bool facingRight) {
            Vector2 a = !facingRight ? box.Extent : box.Position,
                b = !facingRight 
                    ? new Vector2(box.Position.X, box.Center.Y)
                    : new Vector2(box.Extent.X, box.Center.Y),
                c = !facingRight
                    ? new Vector2(box.Extent.X, box.Position.Y)
                    : new Vector2(box.Position.X, box.Extent.Y);

            float alpha = 1;
            if (_Compare != null) {
                if (_Minimum.HasValue && !facingRight)
                    if (CompareTo(_Minimum.Value, _Value) >= 0)
                        alpha = 0.6f;
                if (_Maximum.HasValue && facingRight)
                    if (CompareTo(_Maximum.Value, _Value) <= 0)
                        alpha = 0.6f;
            }

            renderer.RasterizeTriangle(
                a, b, c, radius: 0f, outlineRadius: 1f,
                innerColor: pSRGBColor.White(alpha), outerColor: pSRGBColor.White(alpha), 
                outlineColor: Color.Black
            );
        }

        private RectF ComputeGaugeBox (IDecorationProvider decorations, RectF box) {
            if (decorations == null)
                return default(RectF);
            if (!Minimum.HasValue || !Maximum.HasValue)
                return default(RectF);
            ComputeEffectiveScaleRatios(decorations, out Vector2 paddingScale, out Vector2 marginScale, out Vector2 sizeScale);
            var gauge = decorations.ParameterGauge;
            var gaugeBox = box;
            gaugeBox.Top += gauge.Margins.Top;
            gaugeBox.Left += gauge.Margins.Left;
            gaugeBox.Width -= gauge.Margins.X;
            gaugeBox.Height -= gauge.Margins.Y;
            var gaugeHeight = gauge.Padding.Y * sizeScale.Y;
            gaugeBox.Top = gaugeBox.Extent.Y - gaugeHeight;
            gaugeBox.Height = gaugeHeight;
            return gaugeBox;
        }

        private RectF ComputeArrowBox (RectF contentBox, bool facingRight) {
            if (Increment == null)
                return default(RectF);

            var box = contentBox;
            // Compensate for padding
            var padding = ArrowPadding * Context.Decorations.SpacingScaleRatio.X * Context.Decorations.PaddingScaleRatio.X;
            box.Left -= padding;
            box.Width -= (padding * 2);

            var width = ArrowWidth * Context.Decorations.SizeScaleRatio.X;
            var height = ArrowHeight * Context.Decorations.SizeScaleRatio.Y;
            var space = (box.Height - height) / 2f;
            box.Top += space;
            box.Width = width;
            box.Height = height;
            if (!facingRight)
                return box;
            box.Left = contentBox.Extent.X + (padding - width);
            return box;
        }

        protected T CachedFractionValue;
        protected double? CachedFractionD;
        protected double FractionD {
            get {
                if (!_Maximum.HasValue)
                    return double.NaN;
                if (CachedFractionD.HasValue && (CompareTo(CachedFractionValue, _Value) == 0))
                    return CachedFractionD.Value;

                // FIXME: Handle omitted minimum?
                var sub = Arithmetic.OperatorCache<T>.Subtract;
                var range = sub(_Maximum.Value, _Minimum.Value);
                var result = Convert.ToDouble(sub(_Value, _Minimum.Value)) / Convert.ToDouble(range);
                CachedFractionValue = _Value;
                CachedFractionD = result;
                return result;
            }
        }

        protected override void OnRasterize (ref UIOperationContext context, ref ImperativeRenderer renderer, DecorationSettings settings, IDecorator decorations) {
            base.OnRasterize(ref context, ref renderer, settings, decorations);

            var gauge = context.DecorationProvider.ParameterGauge;
            if (
                (Minimum.HasValue && Maximum.HasValue) &&
                (gauge != null) &&
                (CompareTo(Minimum.Value, Maximum.Value) != 0)
            ) {
                var gaugeBox = ComputeGaugeBox(context.DecorationProvider, settings.Box);
                var fraction = FractionD;
                if (Exponent.HasValue)
                    fraction = 1 - Math.Pow(1 - fraction, Exponent.Value);
                var tempSettings = settings;
                tempSettings.State = settings.State & ~ControlStates.Hovering;
                if (gaugeBox.Contains(context.MousePosition) || IsDraggingGauge)
                    tempSettings.State |= ControlStates.Hovering;
                gaugeBox.Width = Math.Min(Math.Max(gaugeBox.Width * (float)fraction, gauge.Padding.X), settings.Box.Width - gauge.Margins.X);
                // HACK: Compensate for the hitbox being too small for some reason
                gaugeBox.Top += 1;
                gaugeBox.Height -= 1;
                tempSettings.Box = settings.ContentBox;
                tempSettings.ContentBox = gaugeBox;
                // HACK to prevent the background from covering the gauge fill
                if (Appearance.HasBackgroundColor)
                    renderer.Layer += 1;
                gauge.Rasterize(ref context, ref renderer, ref tempSettings);
            }

            // Draw in the "Above" pass to ensure it is not clipped (better batching)
            if (context.Pass != RasterizePasses.Above)
                return;

            if (!Increment.HasValue)
                return;

            RasterizeArrow(ref renderer, ComputeArrowBox(settings.ContentBox, false), false);
            RasterizeArrow(ref renderer, ComputeArrowBox(settings.ContentBox, true), true);
        }

        private bool Adjust (bool positive, bool fast) {
            if (!Increment.HasValue)
                return false;

            var increment = Increment.Value;
            if (fast) {
                var value = _Value;
                for (int i = 0; i < FastIncrementRate; i++) {
                    if (positive)
                        value = Arithmetic.OperatorCache<T>.Add(value, increment);
                    else
                        value = Arithmetic.OperatorCache<T>.Subtract(value, increment);
                }
                return SetValue(value, true);
            } else {
                if (positive)
                    return SetValue(Arithmetic.OperatorCache<T>.Add(_Value, increment), true);
                else
                    return SetValue(Arithmetic.OperatorCache<T>.Subtract(_Value, increment), true);
            }
        }

        protected override bool OnKeyPress (KeyEventArgs evt) {
            switch (evt.Key) {
                case Keys.Enter:
                    FinalizeValue();
                    return true;
                case Keys.Up:
                    if (evt.IsVirtualInput)
                        return false;
                    else
                        return Adjust(true, evt.Modifiers.Control);
                case Keys.Down:
                    if (evt.IsVirtualInput)
                        return false;
                    else
                        return Adjust(false, evt.Modifiers.Control);
                case Keys.Left:
                    if (evt.IsVirtualInput)
                        return Adjust(false, evt.Modifiers.Control);
                    break;
                case Keys.Right:
                    if (evt.IsVirtualInput)
                        return Adjust(true, evt.Modifiers.Control);
                    break;
                case Keys.PageUp:
                    return Adjust(true, true);
                case Keys.PageDown:
                    return Adjust(false, true);
                case Keys.Home:
                    if (Minimum.HasValue && evt.Modifiers.Control) {
                        if (SetValue(Minimum.Value, true)) {
                            SelectNone();
                            return true;
                        }
                        return false;
                    }
                    break;
                case Keys.End:
                    if (Maximum.HasValue && evt.Modifiers.Control) {
                        if (SetValue(Maximum.Value, true)) {
                            SelectNone();
                            return true;
                        }
                        return false;
                    }
                    break;
            }

            return base.OnKeyPress(evt);
        }

        protected override bool OnEvent<TArgs> (string name, TArgs args) {
            // Don't respond to focus changes if they involve a context menu
            if (args is Menu)
                return base.OnEvent(name, args);

            if (name == UIEvents.LostFocus)
                FinalizeValue();

            return base.OnEvent(name, args);
        }

        protected override bool OnClick (int clickCount) {
            if (clickCount > 1)
                return true;

            return base.OnClick(clickCount);
        }

        protected override void OnTick (MouseEventArgs args) {
            base.OnTick(args);
            if (args.Buttons == MouseButtons.None)
                return;

            var leftArrow = ComputeArrowBox(args.ContentBox, false);
            var rightArrow = ComputeArrowBox(args.ContentBox, true);
            var leftHeld = leftArrow.Contains(args.RelativeGlobalPosition) && leftArrow.Contains(args.MouseDownPosition);
            var rightHeld = rightArrow.Contains(args.RelativeGlobalPosition) && rightArrow.Contains(args.MouseDownPosition);
            var fast = args.Buttons == MouseButtons.Right;

            if ((leftHeld || rightHeld) && Context.UpdateRepeat(
                args.Now, args.MouseDownTimestamp, ref LastRepeatTimestamp, 
                speedMultiplier: fast ? FastRepeatSpeed : NormalRepeatSpeed, 
                accelerationMultiplier: fast ? FastAccelerationMultiplier : NormalAccelerationMultiplier
            ))
                Adjust(rightHeld, fast);
        }

        private bool TrySetNewFractionalValue (double fraction) {
            if (!_Maximum.HasValue || !_Minimum.HasValue)
                return false;
            var sub = Arithmetic.OperatorCache<T>.Subtract;
            var add = Arithmetic.OperatorCache<T>.Add;
            var range = sub(_Maximum.Value, _Minimum.Value);
            var offset = Convert.ToDouble(range) * fraction;
            var result = add(_Minimum.Value, (T)Convert.ChangeType(offset, typeof(T)));
            if (SetValue(result, true)) {
                SelectNone();
                return true;
            }
            return false;
        }

        protected override bool OnMouseEvent (string name, MouseEventArgs args) {
            ClampVirtualPositionToTextbox = Context.HasBeenFocusedSinceStartOfUpdate(this);

            var gauge = Context.Decorations.ParameterGauge;
            if (gauge != null) {
                var gaugeBox = ComputeGaugeBox(Context.Decorations, args.Box);
                if (gaugeBox.Contains(args.MouseDownPosition)) {
                    IsDraggingGauge = (args.Buttons == MouseButtons.Left);
                    double fraction = Arithmetic.Saturate((args.RelativeGlobalPosition.X - args.Box.Left) / args.Box.Width);
                    if (Exponent.HasValue)
                        fraction = 1 - Math.Pow(1 - fraction, 1.0 / Exponent.Value);
                    if ((args.Buttons == MouseButtons.Left) || (args.PreviousButtons == MouseButtons.Left))
                        return TrySetNewFractionalValue(fraction);
                    else
                        return true;
                } else {
                    IsDraggingGauge = false;
                }
            }

            var leftArrow = ComputeArrowBox(args.ContentBox, false);
            var rightArrow = ComputeArrowBox(args.ContentBox, true);
            if (leftArrow.Contains(args.RelativeGlobalPosition) || rightArrow.Contains(args.RelativeGlobalPosition)) {
                if (name == UIEvents.MouseDown) {
                    Adjust(rightArrow.Contains(args.RelativeGlobalPosition), (args.Buttons == MouseButtons.Right));
                    SelectNone();
                }

                return true;
            }

            if (leftArrow.Contains(args.MouseDownPosition) || rightArrow.Contains(args.MouseDownPosition))
                return true;

            return base.OnMouseEvent(name, args);
        }

        // HACK: Attach scrolling to our value instead of our text viewport so that the user can
        //  adjust our value the way they would normally scroll the view (with mousewheel, etc)
        const float VirtualScrollScale = 1500f;

        Vector2 IScrollableControl.ScrollOffset => new Vector2((float)FractionD * VirtualScrollScale, 0f);
        bool IScrollableControl.Scrollable {
            get => true;
            set {
            }
        }

        bool IScrollableControl.AllowDragToScroll => false;
        Vector2? IScrollableControl.MinScrollOffset => new Vector2(0f, 0f);
        Vector2? IScrollableControl.MaxScrollOffset => new Vector2(VirtualScrollScale, 0f);

        bool IScrollableControl.TrySetScrollOffset (Vector2 value, bool forUser) {
            return TrySetNewFractionalValue(Arithmetic.Saturate(value.X / VirtualScrollScale));
        }
    }
}
