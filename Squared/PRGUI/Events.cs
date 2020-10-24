﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace Squared.PRGUI {
    public static class UIEvents {
        public const string LostFocus = "LostFocus",
            GotFocus = "GotFocus",
            MouseDown = "MouseDown",
            MouseMove = "MouseMove",
            MouseUp = "MouseUp",
            MouseEnter = "MouseEnter",
            MouseLeave = "MouseLeave",
            // Another mouse button was pressed/released in addition to the one being held
            MouseButtonsChanged = "MouseButtonsChanged",
            Click = "Click",
            Scroll = "Scroll",
            KeyDown = "KeyDown",
            KeyPress = "KeyPress",
            KeyUp = "KeyUp",
            Moved = "Moved",
            ValueChanged = "ValueChanged",
            CheckedChanged = "CheckedChanged",
            RadioButtonSelected = "RadioButtonSelected",
            SelectionChanged = "SelectionChanged",
            ItemChosen = "ItemChosen",
            Shown = "Shown",
            Closed = "Closed",
            OpacityTweenEnded = "OpacityTweenEnded",
            BackgroundColorTweenEnded = "BackgroundColorTweenEnded",
            TextColorTweenEnded = "TextColorTweenEnded";
    }

    public struct KeyboardModifiers {
        public bool Control => LeftControl || RightControl;
        public bool Shift => LeftShift || RightShift;
        public bool Alt => LeftAlt || RightAlt;

        public bool LeftControl, RightControl, LeftShift, RightShift, LeftAlt, RightAlt;
    }

    public struct MouseEventArgs {
        public UIContext Context;

        public KeyboardModifiers Modifiers;
        public Control MouseOver, MouseCaptured, Hovering, Focused;
        public Vector2 GlobalPosition, OrientedGlobalPosition, LocalPosition;
        public Vector2 MouseDownPosition;
        public RectF Box, ContentBox;
        public MouseButtons PreviousButtons, Buttons;
        public bool MovedSinceMouseDown, DoubleClicking;
    }

    public struct KeyEventArgs {
        public UIContext Context;

        public KeyboardModifiers Modifiers;
        public Keys? Key;
        public char? Char;
    }
}
