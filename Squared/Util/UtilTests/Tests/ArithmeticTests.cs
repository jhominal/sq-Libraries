﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Squared.Util;
using System.Linq.Expressions;
using System.Diagnostics;

namespace Squared.Util {
    public struct ValueType {
        public float A, B;

        public ValueType (float a, float b) {
            A = a;
            B = b;
        }

        public static ValueType operator + (ValueType lhs, ValueType rhs) {
            return new ValueType(lhs.A + rhs.A, lhs.B + rhs.B);
        }

        public static ValueType operator - (ValueType lhs, ValueType rhs) {
            return new ValueType(lhs.A - rhs.A, lhs.B - rhs.B);
        }

        public static ValueType operator * (ValueType lhs, ValueType rhs) {
            return new ValueType(lhs.A * rhs.A, lhs.B * rhs.B);
        }

        public static ValueType operator / (ValueType lhs, ValueType rhs) {
            return new ValueType(lhs.A / rhs.A, lhs.B / rhs.B);
        }

        public static ValueType operator * (ValueType lhs, float rhs) {
            return new ValueType(lhs.A * rhs, lhs.B * rhs);
        }

        public static bool operator == (ValueType lhs, ValueType rhs) {
            return (lhs.A == rhs.A) && (lhs.B == rhs.B);
        }

        public static bool operator != (ValueType lhs, ValueType rhs) {
            return (lhs.A != rhs.A) && (lhs.B != rhs.B);
        }

        public override bool Equals (object obj) {
            return base.Equals(obj);
        }

        public override int GetHashCode () {
            return base.GetHashCode();
        }
    }

    [TestFixture]
    public class ArithmeticTests {
        [Test]
        public void ClampInt () {
            Assert.AreEqual(Arithmetic.Clamp(1, 0, 2), 1);
            Assert.AreEqual(Arithmetic.Clamp(0, 0, 2), 0);
            Assert.AreEqual(Arithmetic.Clamp(2, 0, 2), 2);
            Assert.AreEqual(Arithmetic.Clamp(-1, 0, 2), 0);
            Assert.AreEqual(Arithmetic.Clamp(3, 0, 2), 2);
        }

        [Test]
        public void ClampFloat () {
            Assert.AreEqual(Arithmetic.Clamp(1.0f, 0.0f, 2.0f), 1.0f);
            Assert.AreEqual(Arithmetic.Clamp(0.0f, 0.0f, 2.0f), 0.0f);
            Assert.AreEqual(Arithmetic.Clamp(2.0f, 0.0f, 2.0f), 2.0f);
            Assert.AreEqual(Arithmetic.Clamp(-0.5f, 0.0f, 2.0f), 0.0f);
            Assert.AreEqual(Arithmetic.Clamp(2.5f, 0.0f, 2.0f), 2.0f);
        }

        [Test]
        public void Add () {
            Assert.AreEqual(5, Arithmetic.InvokeOperator(Arithmetic.Operators.Add, 2, 3));
            Assert.AreEqual(5.5f, Arithmetic.InvokeOperator(Arithmetic.Operators.Add, 2.5f, 3.0f));
            Assert.AreEqual(
                new ValueType(5.0f, 4.0f),
                Arithmetic.InvokeOperator(Arithmetic.Operators.Add, new ValueType(3.0f, 2.0f), new ValueType(2.0f, 2.0f))
            );
        }

        [Test]
        public void Subtract () {
            Assert.AreEqual(3, Arithmetic.InvokeOperator(Arithmetic.Operators.Subtract, 5, 2));
            Assert.AreEqual(3.5f, Arithmetic.InvokeOperator(Arithmetic.Operators.Subtract, 5.5f, 2.0f));
            Assert.AreEqual(
                new ValueType(1.0f, 2.0f),
                Arithmetic.InvokeOperator(Arithmetic.Operators.Subtract, new ValueType(4.0f, 4.0f), new ValueType(3.0f, 2.0f))
            );
        }

        [Test]
        public void Multiply () {
            Assert.AreEqual(4, Arithmetic.InvokeOperator(Arithmetic.Operators.Multiply, 2, 2));
            Assert.AreEqual(4.5f, Arithmetic.InvokeOperator(Arithmetic.Operators.Multiply, 2.25f, 2.0f));
            Assert.AreEqual(
                new ValueType(4.5f, 4.0f),
                Arithmetic.InvokeOperator(Arithmetic.Operators.Multiply, new ValueType(2.25f, 2.0f), new ValueType(2.0f, 2.0f))
            );
        }

        [Test]
        public void Divide () {
            Assert.AreEqual(2, Arithmetic.InvokeOperator(Arithmetic.Operators.Divide, 4, 2));
            Assert.AreEqual(2.25f, Arithmetic.InvokeOperator(Arithmetic.Operators.Divide, 4.5f, 2.0f));
            Assert.AreEqual(
                new ValueType(2.25f, 2.0f),
                Arithmetic.InvokeOperator(Arithmetic.Operators.Divide, new ValueType(4.5f, 4.0f), new ValueType(2.0f, 2.0f))
            );
        }

        [Test]
        public void Modulus () {
            Assert.AreEqual(1, Arithmetic.InvokeOperator(Arithmetic.Operators.Modulo, 5, 2));
            Assert.AreEqual(1.25f, Arithmetic.InvokeOperator(Arithmetic.Operators.Modulo, 5.25f, 2.0f));
        }

        [Test]
        public void Lerp () {
            Assert.AreEqual(0.0f, Arithmetic.Lerp(0.0f, 2.0f, -0.5f));
            Assert.AreEqual(0.0f, Arithmetic.Lerp(0.0f, 2.0f, 0.0f));
            Assert.AreEqual(1.0f, Arithmetic.Lerp(0.0f, 2.0f, 0.5f));
            Assert.AreEqual(2.0f, Arithmetic.Lerp(0.0f, 2.0f, 1.0f));
            Assert.AreEqual(2.0f, Arithmetic.Lerp(0.0f, 2.0f, 1.5f));
        }

        [Test]
        public void MultiplyMixedTypes () {
            Assert.AreEqual(
                new ValueType(4.5f, 4.0f),
                Arithmetic.InvokeOperator(Arithmetic.Operators.Multiply, new ValueType(2.25f, 2.0f), 2.0f)
            );
        }

        [Test]
        public void ThrowsIfParticularOperationNotImplemented () {
            try {
                Arithmetic.InvokeOperator(Arithmetic.Operators.Add, 2.0f, new ValueType(1.0f, 1.0f));
                Assert.Fail("Did not throw");
            } catch (InvalidOperationException ex) {
#if WINDOWS
                Assert.IsTrue(ex.Message.Contains("GenerateOperatorIL failed"));
#endif
            }

            try {
                Arithmetic.InvokeOperator(Arithmetic.Operators.Add, 2.0m, 1);
                Assert.Fail("Did not throw");
            } catch (InvalidOperationException ex) {
#if WINDOWS
                Assert.IsTrue(ex.Message.Contains("GenerateOperatorIL failed"));
#endif
            }
        }

        [Test]
        public void CompileExpression () {
            Func<float, float> fn;
            Arithmetic.CompileExpression(
                (a) => a * 2.0f,
                out fn
            );

            Assert.AreEqual(fn(2.0f), 4.0f);
            Assert.AreEqual(fn(2), 4.0f);

            Arithmetic.CompileExpression(
                (a) => -a / 2.0f + 1.0f,
                out fn
            );

            Assert.AreEqual(fn(2.0f), 0.0f);
            Assert.AreEqual(fn(-1), 1.5f);

            Func<float, bool> cmp;
            Arithmetic.CompileExpression(
                (a) => a == 2.0f,
                out cmp
            );

            Assert.IsTrue(cmp(2.0f));
            Assert.IsTrue(cmp(2));
            Assert.IsFalse(cmp(3.0f));

            Func<ValueType, ValueType, bool> cmpvt;
            Arithmetic.CompileExpression(
                (a, b) => a == b,
                out cmpvt
            );

            ValueType vtA = new ValueType(1.0f, 1.0f);
            ValueType vtB = new ValueType(1.0f, 2.0f);
            Assert.IsTrue(cmpvt(vtA, vtA));
            Assert.IsFalse(cmpvt(vtA, vtB));

            Arithmetic.CompileExpression(
                (a) => Math.Cos(a),
                out fn
            );

            Assert.AreEqual(fn(5.0f), (float)Math.Cos(5.0f));
            Assert.AreEqual(fn(0.5f), (float)Math.Cos(0.5f));
        }

        [Test]
        public void CompileExpressionWithMixedTypes () {
            Func<ValueType, float, ValueType> mul;
            Arithmetic.CompileExpression(
                (a, b) => a * b,
                out mul
            );

            ValueType vt = new ValueType(1.0f, 1.0f);
            Assert.AreEqual(mul(vt, 2.0f), new ValueType(2.0f, 2.0f));
        }

        [Test]
        public void PerformanceTest () {
            int numIterations = 20000;
            float[] r = new float[numIterations];
            float numIterationsF = numIterations;
            float a = 0.0f, b = 1.0f, c;

            var _add = Arithmetic.GetOperator<float, float>(Arithmetic.Operators.Add);
            var _mul = Arithmetic.GetOperator<float, float>(Arithmetic.Operators.Multiply);
            var _sub = Arithmetic.GetOperator<float, float>(Arithmetic.Operators.Subtract);
            _add(0.0f, 0.0f);
            _mul(0.0f, 0.0f);
            _sub(0.0f, 0.0f);

            Expression<Func<float, float, float, float>> expr = (A, B, C) => A + ((B - A) * C);
            Func<float, float, float, float> nativeExpr = expr.Compile();
            Func<float, float, float, float> genericExpr;
            Arithmetic.CompileExpression(
                (A, B, C) => A + ((B - A) * C),
                out genericExpr
            );

            long start = Time.Ticks;
            for (int i = 0; i < numIterations; i++) {
                c = (i / numIterationsF);
                r[i] = a + ((b - a) * c);
            }
            long end = Time.Ticks;
            Console.WriteLine("Native expression execution time: {0} ticks for {1} iterations ({2:0.000} ticks/iter)", end - start, numIterations, (end - start) / numIterationsF);

            start = Time.Ticks;
            for (int i = 0; i < numIterations; i++) {
                c = (i / numIterationsF);
                r[i] = Arithmetic.InvokeOperator(Arithmetic.Operators.Add, a, Arithmetic.InvokeOperator(Arithmetic.Operators.Multiply, Arithmetic.InvokeOperator(Arithmetic.Operators.Subtract, b, a), c));
            }
            end = Time.Ticks;
            Console.WriteLine("Naive delegate generic execution time: {0} ticks for {1} iterations ({2:0.000} ticks/iter)", end - start, numIterations, (end - start) / numIterationsF);

            start = Time.Ticks;
            for (int i = 0; i < numIterations; i++) {
                c = (i / numIterationsF);
                r[i] = _add(a, _mul(_sub(b, a), c));
            }
            end = Time.Ticks;
            Console.WriteLine("Cached delegate execution time: {0} ticks for {1} iterations ({2:0.000} ticks/iter)", end - start, numIterations, (end - start) / numIterationsF);

            start = Time.Ticks;
            for (int i = 0; i < numIterations; i++) {
                c = (i / numIterationsF);
                r[i] = nativeExpr(a, b, c);
            }
            end = Time.Ticks;
            Console.WriteLine("Native expression delegate execution time: {0} ticks for {1} iterations ({2:0.000} ticks/iter)", end - start, numIterations, (end - start) / numIterationsF);

            start = Time.Ticks;
            for (int i = 0; i < numIterations; i++) {
                c = (i / numIterationsF);
                r[i] = genericExpr(a, b, c);
            }
            end = Time.Ticks;
            Console.WriteLine("Generic expression delegate execution time: {0} ticks for {1} iterations ({2:0.000} ticks/iter)", end - start, numIterations, (end - start) / numIterationsF);
        }
    }

    public static class EvilTests {
        private static readonly float[] TestValues = new[] {
            // Problem child impossible to format correctly in debug output, I hate it
            // -0f
            0f, 0.1f, 1.0f, 2.0f, 0.001f, 256f, 1024f, 8192f, (float)0xFFFFFF, (float)0xFFFFFFF, (float)0xFFFFFFFF, float.MinValue, float.MaxValue, float.NegativeInfinity, float.PositiveInfinity, float.NaN
        };

        private static void TestPairImpl (float a, float b, ref int errorCount) {
            var buf = new FastMath.U32F32 { F1 = a, F2 = b };
            var expected = a.CompareTo(b);
            var actual = Math.Sign(FastMath.CompareF(ref buf));
            if (expected != actual) {
                var sA = a.ToString("R");
                var sB = b.ToString("R");
                if (!float.IsNaN(a) && (a < 0) && !sA.StartsWith("-"))
                    sA = "-" + sA;
                if (!float.IsNaN(b) && (b < 0) && !sB.StartsWith("-"))
                    sB = "-" + sB;
                Console.WriteLine("for {0}, {1} : expected {2}, got {3}", sA, sB, expected, actual);
                errorCount++;
            }
        }

        private static void TestPair (float a, float b, ref int errorCount) {
            TestPairImpl(a, b, ref errorCount);
            // Flipping signs on NaNs is going to produce garbage, we don't care
            if (float.IsNaN(a) || float.IsNaN(b))
                return;
            // Flipping signs on 0 produces nonsense
            if (a != 0)
                TestPairImpl(-a, b, ref errorCount);
            if (b != 0)
                TestPairImpl(a, -b, ref errorCount);
            if (b != 0)
                TestPairImpl(-b, a, ref errorCount);
            if (a != 0)
                TestPairImpl(b, -a, ref errorCount);
        }

        private static float[] GetAllTestValues () {
            var allTestValues = new List<float>(TestValues);
            allTestValues.Capacity = allTestValues.Capacity + 0x1FFF;

            var buf = default(FastMath.U32F32);
            // This burns some time but it's worthwhile to be exhaustive
            for (var i = 0; i < 0x1FFF; i++) {
                buf.I1 = i;
                allTestValues.Add(buf.F1);
            }

            return allTestValues.ToArray();
        }

#if DEBUG
        const int passCount = 4;
#else
        const int passCount = 256;
#endif

        private static void CompareToPass (float[] allTestValues, ref long totalAccumulator) {
            int accumulator = 0;
            foreach (var a in allTestValues) {
                foreach (var b in allTestValues) {
                    var temp = a.CompareTo(b);
                    accumulator += (temp > 0) ? 1 : 0;
                }
            }
            totalAccumulator += accumulator;
        }

        [Test]
        public static void CompareToBenchmark () {
            long accumulator = 0;
            var allTestValues = GetAllTestValues();
            var sw = Stopwatch.StartNew();

            long totalSteps = (passCount * allTestValues.Length * allTestValues.Length);
            // Compare inlining does not seem to happen if the pass is inlined into this function
            for (int i = 0; i < passCount; i++)
                CompareToPass(allTestValues, ref accumulator);

            sw.Stop();
            Console.WriteLine("acc={0}", accumulator);
            Console.WriteLine("Elapsed: {0:R}ms", sw.Elapsed.TotalMilliseconds);
            Console.WriteLine("{0:N11}ms/100k compares", sw.Elapsed.TotalMilliseconds / (totalSteps / 100000));
        }

        private static void CompareFPass (float[] allTestValues, ref long totalAccumulator) {
            var buf = default(FastMath.U32F32);
            int accumulator = 0;
            foreach (var a in allTestValues) {
                buf.F1 = a;
                foreach (var b in allTestValues) {
                    buf.F2 = b;
                    var temp = FastMath.CompareF(ref buf);
                    accumulator += (temp > 0) ? 1 : 0;
                }
            }
            totalAccumulator += accumulator;
        }

        [Test]
        public static void CompareFBenchmark () {
            long accumulator = 0;
            var allTestValues = GetAllTestValues();
            var sw = Stopwatch.StartNew();

            long totalSteps = (passCount * allTestValues.Length * allTestValues.Length);
            // Compare inlining does not seem to happen if the pass is inlined into this function
            for (int i = 0; i < passCount; i++)
                CompareFPass(allTestValues, ref accumulator);

            sw.Stop();
            Console.WriteLine("acc={0}", accumulator);
            Console.WriteLine("Elapsed: {0:R}ms", sw.Elapsed.TotalMilliseconds);
            Console.WriteLine("{0:N11}ms/100k compares", sw.Elapsed.TotalMilliseconds / (totalSteps / 100000));
        }

        [Test]
        public static void CompareFSimple () {
            int errorCount = 0;
            var allTestValues = GetAllTestValues();

            foreach (var a in allTestValues) {
                foreach (var b in allTestValues) {
                    TestPair(a, b, ref errorCount);
                }
            }
            Assert.AreEqual(0, errorCount);
        }
    }
}
