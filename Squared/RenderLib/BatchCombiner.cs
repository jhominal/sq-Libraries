﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using Squared.Util;
using Squared.Threading;
using System.Runtime;

namespace Squared.Render {
    public interface IBatchCombiner {
        bool CanCombine (Batch lhs, Batch rhs);
        Batch Combine (Batch lhs, Batch rhs);
    }

    public sealed class BatchTypeSorter : IRefComparer<Batch>, IComparer<Batch> {
        [TargetedPatchingOptOut("")]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Compare (ref Batch x, ref Batch y) {
            if (x == null)
                return (x == y) ? 0 : -1;
            else if (y == null)
                return 1;

            unchecked {
                var typeResult = x.TypeId - y.TypeId;
                if (typeResult == 0)
                    return x.Layer - y.Layer;
                else
                    return typeResult;
            }
        }

        [TargetedPatchingOptOut("")]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Compare (Batch x, Batch y) {
            return Compare(ref x, ref y);
        }
    }

    public static class BatchCombiner {
        public static readonly BatchTypeSorter BatchTypeSorter = new BatchTypeSorter();
        public static readonly List<IBatchCombiner> Combiners = new List<IBatchCombiner>();

        /// <summary>
        /// Scans over a list of batches and applies batch combiners to reduce the total number of batches sent to the GPU and
        ///  improve batch preparation efficiency. Batches eliminated by combination are replaced with null.
        /// </summary>
        /// <param name="batches">The list of batches to perform a combination pass over.</param>
        /// <returns>The number of batches eliminated.</returns>
        public static int CombineBatches (ref DenseList<Batch> batches, ref DenseList<Batch> batchesToRelease) {
            batches.Sort(BatchTypeSorter);

            int i = 0, j = i + 1, l = batches.Count, eliminatedCount = 0;

            Batch a, b;
            Type aType, bType;

            while ((i < l) && (j < l)) {
                a = batches[i];

                if ((a == null) || (a.SuspendFuture != null)) {
                    i += 1;
                    j = i + 1;
                    continue;
                }

                aType = a.GetType();

                b = batches[j];

                if ((b == null) || (b.SuspendFuture != null)) {
                    j += 1;
                    continue;
                }

                bType = b.GetType();

                if ((aType != bType) || (a.Layer != b.Layer)) {
                    i = j;
                    j = i + 1;
                } else {
                    bool combined = false;

                    foreach (var combiner in Combiners) {
                        if (combined = combiner.CanCombine(a, b)) {
                            batches[i] = combiner.Combine(a, b);
                            batches[i].Container = a.Container;

                            if ((a != batches[i]) && (a.ReleaseAfterDraw))
                                batchesToRelease.Add(a);

                            eliminatedCount += 1;
                            break;
                        }
                    }

                    j += 1;
                }
            }

            if (false && eliminatedCount > 0)
                Console.WriteLine("Eliminated {0:0000} of {1:0000} batch(es)", eliminatedCount, batches.Count);

            return eliminatedCount;
        }
    }
}
