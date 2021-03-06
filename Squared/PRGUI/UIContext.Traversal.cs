﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Squared.PRGUI.Controls;
using Squared.PRGUI.Input;
using Squared.Util;

namespace Squared.PRGUI {
    public partial class UIContext : IDisposable {
        public struct TraversalInfo {
            public Control Control, RedirectTarget;
            public IControlContainer Container;

            public ControlCollection Children => Container?.Children;

            public bool IsProxy => (Control is FocusProxy);
            public bool ContainsChildren => (Container != null) && (Container.Children.Count > 0);
        }

        public struct TraverseSettings {
            public bool AllowDescend, AllowDescendIfDisabled, AllowDescendIfInvisible, AllowAscend;
            // HACK: Will default to true for Window and false for everything else
            public bool? AllowWrap;
            // public bool FollowProxies;
            public int Direction;
            public Func<Control, bool> Predicate;

            internal int FrameIndex;
        }

        private TraversalInfo Traverse_MakeInfo (Control control) {
            return new TraversalInfo {
                Control = control,
                RedirectTarget = control.FocusBeneficiary,
                Container = (control as IControlContainer)
            };
        }

        private bool Traverse_CanDescend (ref TraversalInfo info, ref TraverseSettings settings) {
            if (!settings.AllowDescend)
                return false;
            if (!info.ContainsChildren)
                return false;
            if (!settings.AllowDescendIfDisabled && !info.Control.Enabled)
                return false;
            // FIXME: Optimize this check
            if (!settings.AllowDescendIfInvisible && Control.IsRecursivelyTransparent(info.Control, true))
                return false;

            return true;
        }

        private IEnumerable<TraversalInfo> TraverseChildren (ControlCollection collection, TraverseSettings settings) {
            if (collection.Count <= 0)
                yield break;

            int i = (settings.Direction > 0) ? 0 : collection.Count - 1;

            while (true) {
                var child = collection[i];
                var info = Traverse_MakeInfo(child);
                if ((settings.Predicate == null) || settings.Predicate(child))
                    yield return info;

                if (Traverse_CanDescend(ref info, ref settings)) {
                    foreach (var subchild in TraverseChildren(info.Container.Children, settings))
                        yield return subchild;
                }

                i += settings.Direction;
                if ((i < 0) || (i >= collection.Count))
                    break;
            }
        }

        private IEnumerable<TraversalInfo> SearchForSiblings (ControlCollection collection, Control startingPosition, TraverseSettings settings) {
            if (startingPosition == null)
                throw new ArgumentNullException(nameof(startingPosition));

            var descendSettings = settings;
            descendSettings.AllowAscend = false;
            var currentCollection = collection ?? ((startingPosition as IControlContainer)?.Children);
            if (currentCollection == null) {
                if (!startingPosition.TryGetParent(out Control startingParent) || !(startingParent is IControlContainer icc))
                    yield break;

                currentCollection = icc.Children;
            }

            var currentStartingPosition = startingPosition;
            while (true) {
                int index = currentCollection.IndexOf(currentStartingPosition), i = index + settings.Direction;

                while (true) {
                    // FIXME: Wrap
                    if ((i < 0) || (i >= currentCollection.Count))
                        break;

                    var child = currentCollection[i];
                    var info = Traverse_MakeInfo(child);
                    if ((settings.Predicate == null) || settings.Predicate(child))
                        yield return info;

                    if (Traverse_CanDescend(ref info, ref settings)) {
                        foreach (var subchild in TraverseChildren(info.Container.Children, descendSettings))
                            yield return subchild;
                    }

                    i += settings.Direction;
                    if (i == index)
                        break;
                }

                if (!settings.AllowAscend)
                    break;

                currentStartingPosition = currentCollection.Host;
                if (!currentStartingPosition.TryGetParent(out Control parent))
                    break;

                if (!(parent is IControlContainer parentContainer))
                    break;

                currentCollection = parentContainer.Children;
            }
        }

        private bool _FocusablePredicate (Control control) => control.IsValidFocusTarget;
        private bool _RotatablePredicate (Control control) => control.EligibleForFocusRotation;

        Func<Control, bool> FocusablePredicate, RotatablePredicate;

        public Control PickFocusableChild (Control container, int direction = 1) {
            var settings = new TraverseSettings {
                AllowDescend = true,
                AllowDescendIfDisabled = false,
                AllowDescendIfInvisible = false,
                AllowAscend = false,
                AllowWrap = false,
                Direction = direction,
                // FIXME: Maybe do this?
                // FollowProxies = false,
                FrameIndex = FrameIndex,
                Predicate = FocusablePredicate ?? (FocusablePredicate = _FocusablePredicate)
            };
            // FIXME: Handle cases where the control isn't a container
            var collection = ((container as IControlContainer)?.Children) ?? Controls;
            DebugLog($"Finding focusable child in {container} in direction {direction}");
            return TraverseChildren(collection, settings).FirstOrDefault().Control;
        }

        public Control PickFocusableSiblingForRotation (Control child, int direction, bool? allowLoop) {
            var settings = new TraverseSettings {
                AllowDescend = true,
                AllowDescendIfDisabled = false,
                AllowDescendIfInvisible = false,
                AllowAscend = true,
                AllowWrap = allowLoop,
                Direction = direction,
                // FIXME
                // FollowProxies = true,
                // FIXME: Prevent top level rotate here?
                FrameIndex = FrameIndex,
                Predicate = RotatablePredicate ?? (RotatablePredicate = _RotatablePredicate)
            };

            DebugLog($"Finding sibling for {child} in direction {direction}");
            return SearchForSiblings(null, child, settings).FirstOrDefault().Control;
        }
    }
}
