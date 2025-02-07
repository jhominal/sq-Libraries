﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Runtime.InteropServices;
using System.Security;
using System.Diagnostics;

namespace Squared.Util {
    public static class Time {
        /// <summary>
        /// The length of a second in ticks.
        /// </summary>
        public const long SecondInTicks = 10000000;

        /// <summary>
        /// The length of a millisecond in ticks.
        /// </summary>
        public const long MillisecondInTicks = SecondInTicks / 1000;

        /// <summary>
        /// The default time provider.
        /// </summary>
        public static ITimeProvider DefaultTimeProvider;

        public static long Ticks {
            get {
                return DefaultTimeProvider.Ticks;
            }
        }

        public static double Seconds {
            get {
                return DefaultTimeProvider.Seconds;
            }
        }

        static Time () {
            DefaultTimeProvider = new DotNetTimeProvider();
        }
    }

    public interface ITimeProvider {
        long Ticks {
            get;
        }

        double Seconds {
            get;
        }
    }

    public sealed class DotNetTimeProvider : ITimeProvider {
        long _Offset;
        decimal _Scale;

        public DotNetTimeProvider () {
            _Offset = Stopwatch.GetTimestamp();
            _Scale = Time.SecondInTicks;
        }

        public long Ticks {
            get {
                return Stopwatch.GetTimestamp() - _Offset;
            }
        }

        public double Seconds {
            get {
                decimal scaledTicks = Stopwatch.GetTimestamp() - _Offset;
                scaledTicks /= _Scale;
                return (double)scaledTicks;
            }
        }
    }

    public sealed class Win32TimeProvider : ITimeProvider {
        [DllImport("Kernel32.dll")]
        [SuppressUnmanagedCodeSecurity()]
        private static extern bool QueryPerformanceCounter (out long lpPerformanceCount);

        [DllImport("Kernel32.dll")]
        [SuppressUnmanagedCodeSecurity()]
        private static extern bool QueryPerformanceFrequency (out long lpFrequency);

        private decimal _Frequency;
        private long _Offset;

        public Win32TimeProvider () {
            long temp;
            if (!QueryPerformanceFrequency(out temp))
                throw new InvalidOperationException("High performance timing not supported");

            _Frequency = temp;

            QueryPerformanceCounter(out _Offset);
        }

        public long RawToTicks (long raw) {
            decimal ticks = raw;
            ticks /= _Frequency;
            ticks *= Time.SecondInTicks;
            return (long)ticks;
        }

        public long Raw {
            get {
                long temp;
                QueryPerformanceCounter(out temp);
                temp -= _Offset;
                return temp;
            }
        }

        public long Ticks {
            get {
                long temp;
                QueryPerformanceCounter(out temp);
                temp -= _Offset;
                decimal ticks = temp;
                ticks /= _Frequency;
                ticks *= Time.SecondInTicks;
                return (long)ticks;
            }
        }

        public double Seconds {
            get {
                long temp;
                QueryPerformanceCounter(out temp);
                temp -= _Offset;
                decimal ticks = temp;
                ticks /= _Frequency;
                return (double)ticks;
            }
        }
    }

    public sealed class MockTimeProvider : ITimeProvider {
        public long CurrentTime = 0;

        public long Ticks {
            get { return CurrentTime; }
        }

        public double Seconds {
            get { 
                decimal ticks = CurrentTime;
                return (double)(ticks / Squared.Util.Time.SecondInTicks); 
            }
        }

        public void Advance (long ticks) {
            CurrentTime += ticks;
        }
    }

    public sealed class PausableTimeProvider : ITimeProvider {
        public readonly ITimeProvider Source;

        private long? _DesiredTime = null;
        private long? _PausedSince = null;
        private long _Offset = 0;

        public PausableTimeProvider (ITimeProvider source) {
            Source = source;
        }

        public PausableTimeProvider (ITimeProvider source, long desiredTime) {
            Source = source;
            SetTime(desiredTime);
        }

        public PausableTimeProvider (ITimeProvider source, double desiredTime) {
            Source = source;
            SetTime(desiredTime);
        }

        public long Ticks {
            get {
                if (_PausedSince.HasValue) {
                    if (_DesiredTime.HasValue)
                        return _DesiredTime.Value;

                    return _PausedSince.Value + _Offset;
                }

                return Source.Ticks + _Offset;
            }
        }

        public double Seconds {
            get {
                decimal ticks = Ticks;
                return (double)(ticks / Time.SecondInTicks);
            }
        }

        public bool Paused {
            get {
                return _PausedSince.HasValue;
            }
            set {
                if (_PausedSince.HasValue == value)
                    return;

                long now = Source.Ticks;

                if (value == true) {
                    _PausedSince = now;
                } else if (_DesiredTime.HasValue) {
                    _PausedSince = null;
                    _Offset = _DesiredTime.Value - now;
                    _DesiredTime = null;
                } else {
                    long since = _PausedSince.Value;
                    _PausedSince = null;
                    _Offset -= (now - since);
                }
            }
        }

        public void SetTime (double seconds) {
            SetTime((long)(seconds * Time.SecondInTicks));
        }

        public void SetTime (long ticks) {
            if (_PausedSince.HasValue)
                _DesiredTime = ticks;
            else
                _Offset = -Source.Ticks + ticks;
        }

        public void Reset () {
            _Offset = 0;
        }

        public void StartNow () {
            _Offset = -Source.Ticks;
        }
    }

    public sealed class ScalableTimeProvider : ITimeProvider {
        public readonly ITimeProvider Source;

        private long? _LastTimeScaleChange = null;
        private int _TimeScale = 10000;
        private long _Offset = 0;

        public ScalableTimeProvider (ITimeProvider source) {
            Source = source;
        }

        long Elapsed {
            get {
                return Source.Ticks + _Offset;
            }
        }

        long ScaledElapsed {
            get {
                long ticks = Source.Ticks;

                if (_LastTimeScaleChange.HasValue)
                    ticks -= _LastTimeScaleChange.Value;

                return _Offset + (ticks * _TimeScale / 10000);
            }
        }

        public long Ticks {
            get {
                return ScaledElapsed;
            }
        }

        public double Seconds {
            get {
                decimal ticks = ScaledElapsed;

                return (double)(ticks / Squared.Util.Time.SecondInTicks);
            }
        }

        public float TimeScale {
            get {
                return _TimeScale / 10000.0f;
            }
            set {
                var oldTimeScale = _TimeScale;
                var newTimeScale = (int)Math.Floor(value * 10000.0f);
                if (oldTimeScale == newTimeScale)
                    return;

                OnTimeScaleChange(oldTimeScale, newTimeScale);

                _TimeScale = newTimeScale;
            }
        }

        void OnTimeScaleChange (int oldTimeScale, int newTimeScale) {
            long ticks = Source.Ticks;
            long offset = 0;

            if (_LastTimeScaleChange.HasValue)
                offset = _LastTimeScaleChange.Value;

            _Offset += (ticks - offset) * oldTimeScale / 10000;

            _LastTimeScaleChange = ticks;
        }

        public void Reset () {
            _Offset = 0;
        }

        public void StartNow () {
            _Offset = -Source.Ticks;
        }
    }
}
