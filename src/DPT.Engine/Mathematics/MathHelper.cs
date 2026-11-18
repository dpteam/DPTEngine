//
// DPT.Engine - scalar helpers.
// Copyright (C) 2026 DartPower Team. GPLv2.
//

using System;

namespace DPT.Engine.Mathematics
{
    /// <summary>
    /// The handful of constants and clamps the rest of the engine uses.
    ///
    /// Deliberately a copy of XNA's MathHelper, including the exact values of Pi
    /// and TwoPi: the project format stores spline values that were authored
    /// against XNA, and a different Pi in a rotation conversion shows up as a
    /// slow drift over a long timeline rather than an obvious bug.
    /// </summary>
    public static class MathHelper
    {
        public const float Pi = 3.14159274f;
        public const float TwoPi = 6.28318548f;
        public const float PiOver2 = 1.57079637f;
        public const float PiOver4 = 0.785398185f;
        public const float E = 2.71828175f;
        public const float Epsilon = 1.401298E-45f;

        /// <summary>
        /// Degrees to radians as the engine uses them. The constant is written
        /// out rather than computed so a table of angles stays bit-identical to
        /// the one the legacy engine produced.
        /// </summary>
        public const float DegreesToRadiansFactor = 0.0174532924f;

        public const float RadiansToDegreesFactor = 57.2957764f;

        public static float ToRadians(float degrees)
        {
            return degrees * DegreesToRadiansFactor;
        }

        public static float ToDegrees(float radians)
        {
            return radians * RadiansToDegreesFactor;
        }

        public static float Clamp(float value, float min, float max)
        {
            value = value > max ? max : value;
            value = value < min ? min : value;
            return value;
        }

        public static int Clamp(int value, int min, int max)
        {
            if (value > max) return max;
            if (value < min) return min;
            return value;
        }

        public static float Clamp01(float value)
        {
            return Clamp(value, 0f, 1f);
        }

        public static float Lerp(float from, float to, float amount)
        {
            return from + (to - from) * amount;
        }

        /// <summary>Maps a value from one range to another, unclamped.</summary>
        public static float Map(float value, float inMin, float inMax, float outMin, float outMax)
        {
            if (inMax == inMin) return outMin;
            return outMin + (value - inMin) / (inMax - inMin) * (outMax - outMin);
        }

        public static float SmoothStep(float from, float to, float amount)
        {
            amount = Clamp01(amount);
            amount = amount * amount * (3f - 2f * amount);
            return from + (to - from) * amount;
        }

        public static float Max(float a, float b)
        {
            return a > b ? a : b;
        }

        public static float Min(float a, float b)
        {
            return a < b ? a : b;
        }

        public static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        /// <summary>
        /// Replaces NaN and infinity with a fallback.
        ///
        /// Used at the edges of the pipeline (project load, network input,
        /// script output) because a single NaN that reaches a matrix propagates
        /// into every transform and the whole frame goes black with no clue why.
        /// </summary>
        public static float Sanitize(float value, float fallback)
        {
            return IsFinite(value) ? value : fallback;
        }

        public static float WrapAngle(float angle)
        {
            angle = (float)Math.IEEERemainder(angle, 6.2831854820251465);

            if (angle <= -Pi)
            {
                angle += TwoPi;
            }
            else if (angle > Pi)
            {
                angle -= TwoPi;
            }

            return angle;
        }

        /// <summary>Smallest signed difference between two angles, in radians.</summary>
        public static float AngleDelta(float from, float to)
        {
            return WrapAngle(to - from);
        }
    }
}
