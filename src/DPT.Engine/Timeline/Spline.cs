//
// DPT.Engine - animation keys and splines.
// Copyright (C) 2026 DartPower Team. GPLv2.
//

using System;

namespace DPT.Engine.Timeline
{
    /// <summary>How a spline gets from one key to the next.</summary>
    public enum Interpolation
    {
        /// <summary>Hold the previous value. Useful for on/off switches.</summary>
        Step = 0,

        /// <summary>Straight line. The default, and what the editor shows first.</summary>
        Linear = 1,

        /// <summary>Hermite with auto-computed tangents: smooth, no overshoot control.</summary>
        Hermite = 2,

        /// <summary>Catmull-Rom: smooth through the keys, may overshoot.</summary>
        Catmull = 3,

        /// <summary>Uniform cubic B-spline: smooth, never reaches the keys.</summary>
        Bezier = 4,

        /// <summary>Sine wave driven by the key values as amplitude and phase.</summary>
        Sine = 5,

        /// <summary>Sawtooth wave.</summary>
        Saw = 6,

        /// <summary>Square wave.</summary>
        Square = 7
    }

    /// <summary>
    /// One point on a spline.
    ///
    /// <see cref="TangentIn"/> and <see cref="TangentOut"/> are computed once
    /// after loading (see <see cref="Spline.BuildTangents"/>) rather than on every
    /// sample: an animation is sampled thousands of times per second and the
    /// tangents only change when the keys do.
    /// </summary>
    public struct Key
    {
        /// <summary>Position along the timeline, in seconds.</summary>
        public float Time;

        public float Value;

        /// <summary>Incoming tangent, used by Hermite.</summary>
        public float TangentIn;

        /// <summary>Outgoing tangent, used by Hermite.</summary>
        public float TangentOut;
    }

    /// <summary>
    /// A one-dimensional animation curve.
    ///
    /// This is the engine's core timeline primitive: every animated property -
    /// position, rotation, scale, colour, particle rate - is a spline of floats,
    /// and a quaternion is four of them sampled together.
    ///
    /// The interpolation maths is a faithful port of the original engine's, down
    /// to the constants, because existing project files were authored against it
    /// and a "cleaner" Catmull-Rom with different tangent scaling makes every
    /// old animation move differently.
    ///
    /// Sampling is clamped outside the key range rather than extrapolated: an
    /// animation that starts before its first key holds the first value, which is
    /// what an editor preview expects when the playhead is parked at zero.
    /// </summary>
    public sealed class Spline
    {
        private const float Tension = 0f;
        private const float Bias = 0f;
        private const float Continuity = 0f;

        private Key[] keys = new Key[0];

        public Interpolation Interpolation = Interpolation.Linear;

        /// <summary>When true, the curve wraps and the first and last keys are neighbours.</summary>
        public bool Loop;

        public int KeyCount
        {
            get { return keys.Length; }
        }

        public bool IsEmpty
        {
            get { return keys.Length == 0; }
        }

        public Key[] Keys
        {
            get { return keys; }
        }

        public Key this[int index]
        {
            get { return keys[index]; }
        }

        /// <summary>Replaces the keys and recomputes tangents.</summary>
        public void SetKeys(Key[] newKeys)
        {
            keys = newKeys ?? new Key[0];
            Array.Sort(keys, CompareKeys);
            BuildTangents();
        }

        private static int CompareKeys(Key a, Key b)
        {
            return a.Time.CompareTo(b.Time);
        }

        /// <summary>
        /// Samples the curve at <paramref name="time"/> seconds.
        ///
        /// Every branch returns a finite value. A malformed curve - one key, two
        /// keys at the same time, a division by zero in the tangent - must not put
        /// a NaN into a transform, because a NaN there blanks the whole frame with
        /// no clue which object caused it.
        /// </summary>
        public float Sample(float time)
        {
            if (keys.Length == 0) return 0f;
            if (keys.Length == 1) return keys[0].Value;

            // Waves are periodic by definition and ignore the key layout beyond
            // the first two keys, which carry amplitude and phase.
            switch (Interpolation)
            {
                case Interpolation.Sine:
                    return SampleWave(time, 0);
                case Interpolation.Saw:
                    return SampleWave(time, 1);
                case Interpolation.Square:
                    return SampleWave(time, 2);
            }

            if (time <= keys[0].Time) return keys[0].Value;

            var last = keys.Length - 1;
            if (time >= keys[last].Time) return keys[last].Value;

            var index = FindSegment(time);
            var a = keys[index];
            var b = keys[index + 1];

            var span = b.Time - a.Time;
            if (span <= 0f) return b.Value;

            var t = (time - a.Time) / span;

            switch (Interpolation)
            {
                case Interpolation.Step:
                    return a.Value;

                case Interpolation.Hermite:
                    return Hermite(a.Value, b.Value, a.TangentOut, b.TangentIn, t);

                case Interpolation.Catmull:
                    return Catmull(
                        keys[Math.Max(0, index - 1)].Value,
                        a.Value, b.Value,
                        keys[Math.Min(last, index + 2)].Value, t);

                case Interpolation.Bezier:
                    return BSpline(
                        keys[Math.Max(0, index - 1)].Value,
                        a.Value, b.Value,
                        keys[Math.Min(last, index + 2)].Value, t);

                default:
                    return a.Value + (b.Value - a.Value) * t;
            }
        }

        private float SampleWave(float time, int shape)
        {
            // The original used the second key's time as the period and the first
            // two keys' values as the amplitude ramp. Keeping that means old
            // projects keep their exact look.
            var period = keys.Length > 2 ? keys[2].Time : (keys.Length > 1 ? keys[1].Time : 1f);
            if (period <= 0f) period = 1f;

            var amplitude = keys.Length > 1 ? keys[1].Value : keys[0].Value;
            var phase = time / period;

            switch (shape)
            {
                case 1: // saw
                {
                    var saw = phase / 3.1415f;
                    saw -= (float)Math.Floor(saw);
                    var value = saw < 0.5f ? 4f * saw - 1f : 1f - 4f * (saw - 0.5f);
                    return amplitude * value;
                }

                case 2: // square
                {
                    var sine = (float)Math.Sin(phase);
                    var sign = Math.Abs(sine) < 1e-9f ? 1f : sine / Math.Abs(sine);
                    return amplitude * sign;
                }

                default: // sine
                    return amplitude * (float)Math.Sin(phase);
            }
        }

        /// <summary>Finds the segment containing <paramref name="time"/>.</summary>
        private int FindSegment(float time)
        {
            var low = 0;
            var high = keys.Length - 1;

            // Binary search: a long timeline with hundreds of keys is sampled for
            // every object every frame, and a linear scan there is quadratic.
            while (low < high - 1)
            {
                var middle = (low + high) / 2;

                if (keys[middle].Time <= time)
                {
                    low = middle;
                }
                else
                {
                    high = middle;
                }
            }

            return low;
        }

        // ---------------------------------------------------------------- curves

        /// <summary>Hermite basis, as the original wrote it.</summary>
        private static float Hermite(float p1, float p2, float r1, float r2, float t)
        {
            return p1 * (2f * (t * t * t) - 3f * (t * t) + 1f) +
                   r1 * (t * t * t - 2f * (t * t) + t) +
                   p2 * (-2f * (t * t * t) + 3f * (t * t)) +
                   r2 * (t * t * t - t * t);
        }

        /// <summary>Catmull-Rom through four control values.</summary>
        private static float Catmull(float v0, float v1, float v2, float v3, float t)
        {
            var a = -v0 + 3f * v1 - 3f * v2 + v3;
            var b = 2f * v0 - 5f * v1 + 4f * v2 - v3;
            var c = -v0 + v2;
            var d = 2f * v1;

            return (a * t * t * t + b * t * t + c * t + d) * 0.5f;
        }

        /// <summary>Uniform cubic B-spline. Never passes through its control values.</summary>
        private static float BSpline(float c0, float c1, float c2, float c3, float u)
        {
            var u2 = u * u;
            var u3 = u * u * u;

            var b0 = u3 / 6f;
            var b1 = (1f + 3f * u + 3f * u2 - 3f * u3) / 6f;
            var b2 = (4f - 6f * u2 + 3f * u3) / 6f;
            var b3 = (1f - 3f * u + 3f * u2 - u3) / 6f;

            return c0 * b3 + c1 * b2 + c2 * b1 + c3 * b0;
        }

        /// <summary>
        /// Computes the auto tangents used by Hermite.
        ///
        /// The recursion in the original (a tangent at the first key depends on
        /// the tangent at the second) is unrolled into a forward and a backward
        /// pass here: same result, no stack depth, and it cannot loop forever on a
        /// degenerate two-key curve.
        /// </summary>
        public void BuildTangents()
        {
            var count = keys.Length;
            if (count < 2) return;

            var scale = 1f - Tension;

            // Forward pass: outgoing tangents.
            for (var i = 0; i < count - 1; i++)
            {
                var current = keys[i].Value;
                var next = keys[i + 1].Value;

                if (i == 0)
                {
                    keys[i].TangentOut = (next - current) * scale;
                }
                else
                {
                    var previous = keys[i - 1].Value;
                    var incoming = (current - previous) * (1f + Bias);
                    var outgoing = (next - current) * (1f - Bias);
                    keys[i].TangentOut = (incoming + (outgoing - incoming) * (0.5f + 0.5f * Continuity)) * scale;
                }
            }

            // Backward pass: incoming tangents.
            for (var i = count - 1; i > 0; i--)
            {
                var current = keys[i].Value;
                var previous = keys[i - 1].Value;

                if (i == count - 1)
                {
                    keys[i].TangentIn = (current - previous) * scale;
                }
                else
                {
                    var next = keys[i + 1].Value;
                    var incoming = (current - previous) * (1f + Bias);
                    var outgoing = (next - current) * (1f - Bias);
                    keys[i].TangentIn = (incoming + (outgoing - incoming) * (0.5f - 0.5f * Continuity)) * scale;
                }
            }

            // The first key has no incoming tangent and the last none outgoing;
            // mirror them so Hermite at the ends behaves like a clamped spline.
            keys[0].TangentIn = keys[0].TangentOut;
            keys[count - 1].TangentOut = keys[count - 1].TangentIn;
        }

        /// <summary>A spline that holds one value forever.</summary>
        public static Spline Constant(float value)
        {
            var spline = new Spline();
            spline.SetKeys(new[] { new Key { Time = 0f, Value = value } });
            return spline;
        }

        /// <summary>A two-key linear ramp, for tests and for the editor's defaults.</summary>
        public static Spline Ramp(float from, float to, float duration)
        {
            var spline = new Spline();
            spline.Interpolation = Interpolation.Linear;
            spline.SetKeys(new[]
            {
                new Key { Time = 0f, Value = from },
                new Key { Time = duration, Value = to }
            });
            return spline;
        }
    }
}
