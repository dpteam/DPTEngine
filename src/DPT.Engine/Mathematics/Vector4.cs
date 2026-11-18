//
// DPT.Engine - 4D vector.
// Copyright (C) 2026 DartPower Team. GPLv2.
//

using System;

namespace DPT.Engine.Mathematics
{
    /// <summary>
    /// A four-component vector.
    ///
    /// Mostly a shader-constant carrier: colours, fog parameters and the
    /// contrast/blur knobs all travel to the GPU as a float4, and the editor's
    /// JSON stores them in the same order. Keeping it a real type rather than a
    /// float[] avoids an allocation per shader constant per draw call.
    /// </summary>
    public struct Vector4 : IEquatable<Vector4>
    {
        public float X;
        public float Y;
        public float Z;
        public float W;

        public static readonly Vector4 Zero = new Vector4(0f, 0f, 0f, 0f);
        public static readonly Vector4 One = new Vector4(1f, 1f, 1f, 1f);
        public static readonly Vector4 UnitX = new Vector4(1f, 0f, 0f, 0f);
        public static readonly Vector4 UnitY = new Vector4(0f, 1f, 0f, 0f);
        public static readonly Vector4 UnitZ = new Vector4(0f, 0f, 1f, 0f);
        public static readonly Vector4 UnitW = new Vector4(0f, 0f, 0f, 1f);

        public Vector4(float x, float y, float z, float w)
        {
            X = x;
            Y = y;
            Z = z;
            W = w;
        }

        public Vector4(Vector3 value, float w)
        {
            X = value.X;
            Y = value.Y;
            Z = value.Z;
            W = w;
        }

        public Vector4(Vector2 value, float z, float w)
        {
            X = value.X;
            Y = value.Y;
            Z = z;
            W = w;
        }

        public float Length
        {
            get { return (float)Math.Sqrt(X * X + Y * Y + Z * Z + W * W); }
        }

        public static Vector4 operator +(Vector4 a, Vector4 b)
        {
            return new Vector4(a.X + b.X, a.Y + b.Y, a.Z + b.Z, a.W + b.W);
        }

        public static Vector4 operator -(Vector4 a, Vector4 b)
        {
            return new Vector4(a.X - b.X, a.Y - b.Y, a.Z - b.Z, a.W - b.W);
        }

        public static Vector4 operator *(Vector4 a, float scale)
        {
            return new Vector4(a.X * scale, a.Y * scale, a.Z * scale, a.W * scale);
        }

        public static Vector4 operator *(float scale, Vector4 a)
        {
            return new Vector4(a.X * scale, a.Y * scale, a.Z * scale, a.W * scale);
        }

        public static bool operator ==(Vector4 a, Vector4 b)
        {
            return a.X == b.X && a.Y == b.Y && a.Z == b.Z && a.W == b.W;
        }

        public static bool operator !=(Vector4 a, Vector4 b)
        {
            return !(a == b);
        }

        public static float Dot(Vector4 a, Vector4 b)
        {
            return a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;
        }

        public static Vector4 Lerp(Vector4 from, Vector4 to, float amount)
        {
            return new Vector4(
                from.X + (to.X - from.X) * amount,
                from.Y + (to.Y - from.Y) * amount,
                from.Z + (to.Z - from.Z) * amount,
                from.W + (to.W - from.W) * amount);
        }

        public void Normalize()
        {
            var length = Length;

            if (length < 1e-6f)
            {
                X = 0f;
                Y = 0f;
                Z = 0f;
                W = 0f;
                return;
            }

            var inverse = 1f / length;
            X *= inverse;
            Y *= inverse;
            Z *= inverse;
            W *= inverse;
        }

        public static Vector4 Normalize(Vector4 value)
        {
            value.Normalize();
            return value;
        }

        public override bool Equals(object obj)
        {
            return obj is Vector4 && Equals((Vector4)obj);
        }

        public bool Equals(Vector4 other)
        {
            return this == other;
        }

        public override int GetHashCode()
        {
            var hash = X.GetHashCode();
            hash = hash * 397 ^ Y.GetHashCode();
            hash = hash * 397 ^ Z.GetHashCode();
            hash = hash * 397 ^ W.GetHashCode();
            return hash;
        }

        public override string ToString()
        {
            return "{X:" + X + " Y:" + Y + " Z:" + Z + " W:" + W + "}";
        }
    }
}
