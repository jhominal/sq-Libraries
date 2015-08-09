﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

using RuleSet = System.Linq.Expressions.Expression<System.Func<bool>>;

namespace Squared.Util.DeclarativeSort {
    public interface ITags {
        /// <returns>Whether this tagset contains all the tags in rhs.</returns>
        bool Contains (ITags rhs);

        /// <summary>
        /// The number of tags in this tagset.
        /// </summary>
        int Count { get; }

        Tag this [ int index ] { get; }

        /// <summary>
        /// For internal use only.
        /// </summary>
        Dictionary<Tag, ITags> TransitionCache { get; }
    }

    public class Tag : ITags {
        public class EqualityComparer : IEqualityComparer<Tag> {
            public static readonly EqualityComparer Instance = new EqualityComparer();

            [MethodImpl(MethodImplOptions.AggressiveInlining)] 
            public bool Equals (Tag x, Tag y) {
                return ReferenceEquals(x, y);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)] 
            public int GetHashCode (Tag tag) {
                return tag.Id;
            }
        }

        // Only for sorting within tag arrays to make equality comparisons of tag arrays valid
        internal class Comparer : IComparer<Tag> {
            public static readonly Comparer Instance = new Comparer();

            public int Compare (Tag x, Tag y) {
                return y.Id - x.Id;
            }
        }

        private static int NextId = 1;
        private static readonly Dictionary<string, Tag> TagCache = new Dictionary<string, Tag>();

        public readonly string Name;
        public readonly int    Id;
        public Dictionary<Tag, ITags> TransitionCache { get; private set; }

        internal Tag (string name) {
            Name = name;
            Id = NextId++;
            TransitionCache = new Dictionary<Tag, ITags>(EqualityComparer.Instance);
        }

        Tag ITags.this [int index] {
            [MethodImpl(MethodImplOptions.AggressiveInlining)] 
            get {
                if (index == 0)
                    return this;
                else
                    throw new ArgumentOutOfRangeException(nameof(index));
            }
        }

        int ITags.Count {
            [MethodImpl(MethodImplOptions.AggressiveInlining)] 
            get {
                return 1;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)] 
        bool ITags.Contains (ITags tags) {
            if (tags == this)
                return true;
            if (tags.Count == 1)
                return (this == tags[0]);
            else
                return false;
        }

        public override int GetHashCode () {
            return Id;
        }

        public override bool Equals (object obj) {
            return ReferenceEquals(this, obj);
        }

        public override string ToString () {
            return Name;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)] 
        public static ITags operator + (Tag lhs, ITags rhs) {
            return TagSet.Transition(rhs, lhs);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)] 
        public static ITags operator + (ITags lhs, Tag rhs) {
            return TagSet.Transition(lhs, rhs);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)] 
        public static ITags operator + (Tag lhs, Tag rhs) {
            return TagSet.Transition(lhs, rhs);
        }

        /// <returns>Whether lhs contains rhs.</returns>
        public static bool operator & (ITags lhs, Tag rhs) {
            return lhs.Contains(rhs);
        }

        /// <returns>Whether lhs does not contain rhs.</returns>
        public static bool operator ^ (ITags lhs, Tag rhs) {
            return !lhs.Contains(rhs);
        }

        public static TagOrdering operator < (Tag lhs, Tag rhs) {
            return new TagOrdering(lhs, rhs);
        }

        public static TagOrdering operator > (Tag lhs, Tag rhs) {
            return new TagOrdering(rhs, lhs);
        }

        public static Tag New (string name) {
            Tag result;

            lock (TagCache)
            if (!TagCache.TryGetValue(name, out result))
                TagCache.Add(name, result = new Tag(string.Intern(name)));

            return result;
        }

        /// <summary>
        /// Finds all static Tag fields of type and ensures they are initialized.
        /// If instance is provided, also initializes all non-static Tag fields of that instance.
        /// </summary>
        public static void AutoCreate (Type type, object instance = null) {
            var flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            if (instance != null)
                flags |= BindingFlags.Instance;

            var tTag = typeof(Tag);

            lock (type)
            foreach (var f in type.GetFields(flags)) {
                if (f.FieldType != tTag)
                    continue;

                object lookupInstance = null;
                if (!f.IsStatic)
                    lookupInstance = instance;

                var tag = f.GetValue(lookupInstance);
                if (tag == null)
                    f.SetValue(lookupInstance, New(f.Name));
            }
        }

        /// <summary>
        /// Finds all static Tag fields of type and ensures they are initialized.
        /// If instance is provided, also initializes all non-static Tag fields of that instance.
        /// </summary>
        public static void AutoCreate<T> (T instance = default(T)) {
            AutoCreate(typeof(T), instance);
        }
    }

    public partial class TagSet : ITags {
        private static int NextId = 1;

        private readonly Tag[] Tags;
        private readonly HashSet<Tag> HashSet = new HashSet<Tag>();
        public Dictionary<Tag, ITags> TransitionCache { get; private set; }
        public readonly int Id;

        private TagSet (Tag[] tags) {
            if (tags == null)
                throw new ArgumentNullException(nameof(tags));
            if (tags.Length == 0)
                throw new ArgumentOutOfRangeException(nameof(tags), "Must not be empty");

            Tags = (Tag[]) tags.Clone();
            foreach (var tag in tags)
                HashSet.Add(tag);

            TransitionCache = new Dictionary<Tag, ITags>(Tag.EqualityComparer.Instance);
            Id = NextId++;
        }

        public int Count {
            [MethodImpl(MethodImplOptions.AggressiveInlining)] 
            get {
                return Tags.Length;
            }
        }

        public Tag this [int index] {
            [MethodImpl(MethodImplOptions.AggressiveInlining)] 
            get {
                return Tags[index];
            }
        }

        /// <returns>Whether this tagset contains all the tags in rhs.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)] 
        public bool Contains (ITags rhs) {
            if (rhs == this)
                return true;

            for (int l = rhs.Count, i = 0; i < l; i++) {
                if (!HashSet.Contains(rhs[i]))
                    return false;
            }

            return true;
        }

        public override int GetHashCode () {
            return Id;
        }

        public override bool Equals (object obj) {
            return ReferenceEquals(this, obj);
        }

        public override string ToString () {
            return string.Format("<{0}>", string.Join<Tag>(", ", Tags));
        }
    }

    public partial class TagSet : ITags {
        private class TagArrayComparer : IEqualityComparer<Tag[]> {
            public bool Equals (Tag[] x, Tag[] y) {
                return x.SequenceEqual(y);
            }

            public int GetHashCode (Tag[] tags) {
                var result = 0;
                foreach (var tag in tags)
                    result = (result << 2) ^ tag.Id;
                return result;
            }
        }

        internal static readonly Dictionary<Tag[], TagSet> SetCache = new Dictionary<Tag[], TagSet>(new TagArrayComparer());

        [MethodImpl(MethodImplOptions.AggressiveInlining)] 
        internal static ITags Transition (ITags lhs, Tag rhs) {
            ITags result;

            if (rhs == lhs)
                return lhs;

            bool existing;
            lock (lhs.TransitionCache)
                existing = lhs.TransitionCache.TryGetValue(rhs, out result);

            if (existing)
                return result;
            else
                return TransitionSlow(lhs, rhs);
        }

        internal static ITags TransitionSlow (ITags lhs, Tag rhs) {
            var newTags = new Tag[lhs.Count + 1];

            for (var i = 0; i < newTags.Length - 1; i++) {
                var tag = lhs[i];
                if (tag == rhs)
                    return lhs;

                newTags[i] = tag;
            }

            newTags[newTags.Length - 1] = rhs;
                
            Array.Sort(newTags, Tag.Comparer.Instance);

            var result = New(newTags);
                
            lock (lhs.TransitionCache) {
                if (!lhs.TransitionCache.ContainsKey(rhs))
                    lhs.TransitionCache.Add(rhs, result);
            }

            return result;
        }

        internal static TagSet New (Tag[] tags) {
            TagSet result;

            lock (SetCache)
            if (!SetCache.TryGetValue(tags, out result))
                SetCache.Add(tags, result = new TagSet(tags));

            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)] 
        public static ITags New (ITags lhs, ITags rhs) {
            if (lhs == rhs)
                return lhs;

            var lhsCount = lhs.Count;
            var rhsCount = rhs.Count;

            ITags result = lhs[0];

            for (int i = 1, l = lhs.Count; i < l; i++)
                result = Transition(result, lhs[i]);

            for (int i = 0, l = rhs.Count; i < l; i++)
                result = Transition(result, rhs[i]);

            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)] 
        public static ITags operator + (TagSet lhs, ITags rhs) {
            return New(lhs, rhs);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)] 
        public static ITags operator + (ITags lhs, TagSet rhs) {
            return New(lhs, rhs);
        }
    }

    public struct TagOrdering {
        public  readonly ITags Lower, Higher;
        private readonly int   HashCode;

        public TagOrdering (ITags lower, ITags higher) {
            if (lower == null)
                throw new ArgumentNullException(nameof(lower));
            else if (higher == null)
                throw new ArgumentNullException(nameof(higher));

            Lower = lower;
            Higher = higher;

            HashCode = Lower.GetHashCode() ^ (Higher.GetHashCode() << 2);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)] 
        public int Compare (ITags lhs, ITags rhs) {
            if (lhs.Contains(Lower) && rhs.Contains(Higher))
                return -1;

            if (lhs.Contains(Higher) && rhs.Contains(Lower))
                return 1;

            return 0;
        }

        public override int GetHashCode () {
            return HashCode;
        }

        public bool Equals (TagOrdering rhs) {
            return (Lower == rhs.Lower) && (Higher == rhs.Higher);
        }

        public override bool Equals (object rhs) {
            if (rhs is TagOrdering)
                return Equals((TagOrdering)rhs);
            else
                return false;
        }

        public override string ToString () {
            return string.Format("{0} < {1}", Lower, Higher);
        }
    }

    public class Group {
        public readonly string Name;
    }

    public class ContradictoryOrderingException : Exception {
        public readonly TagOrdering A, B;
        public readonly ITags Left, Right;

        public ContradictoryOrderingException (TagOrdering a, TagOrdering b, ITags lhs, ITags rhs) 
            : base(
                  string.Format("Orderings {0} and {1} are contradictory for {2}, {3}", a, b, lhs, rhs)
            ) {
            A = a;
            B = b;
            Left = lhs;
            Right = rhs;
        }
    }

    public class TagOrderingCollection : HashSet<TagOrdering> {
        public void Add (ITags lower, ITags higher) {
            Add(new TagOrdering(lower, higher));
        }

        public int? Compare (ITags lhs, ITags rhs, out Exception error) {
            int result = 0;
            var lastOrdering = default(TagOrdering);

            foreach (var ordering in this) {
                var subResult = ordering.Compare(lhs, rhs);

                if (subResult == 0)
                    continue;
                else if (
                    (result != 0) &&
                    (Math.Sign(subResult) != Math.Sign(result))
                ) {
                    error = new ContradictoryOrderingException(
                        lastOrdering, ordering, lhs, rhs
                    );
                    return null;
                } else {
                    result = subResult;
                    lastOrdering = ordering;
                }
            }

            error = null;
            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)] 
        public int Compare (ITags lhs, ITags rhs) {
            Exception error;
            var result = Compare(lhs, rhs, out error);

            if (result.HasValue)
                return result.Value;
            else
                throw error;
        }
    }

    public class Sorter : IEnumerable<TagOrdering> {
        public readonly TagOrderingCollection Orderings = new TagOrderingCollection();

        public void Add (TagOrdering ordering) {
            Orderings.Add(ordering);
        }

        public void Add (params TagOrdering[] orderings) {
            foreach (var o in orderings)
                Orderings.Add(o);
        }

        IEnumerator IEnumerable.GetEnumerator () {
            return Orderings.GetEnumerator();
        }

        IEnumerator<TagOrdering> IEnumerable<TagOrdering>.GetEnumerator () {
            return Orderings.GetEnumerator();
        }
    }
}
