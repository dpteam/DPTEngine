//
// DPT.Engine - 2D vector.
// Copyright (C) 2026 DartPower Team. GPLv2.
//

using System;

namespace DPT.Engine.Mathematics
{
    /// <summary>
    /// A two-component vector, laid out as X, Y.
    ///
    /// Matches XNA's Vector2 operation for operation, including the row-vector
    /// transform in <see cref="Transform(Vector2, Matrix)"/>. The project format
    /// stores texture coordinates as 16-bit fixed point that the loader divides
    /// by 32767, so the arithmetic here must stay float, not double.
    /// </summary>
    public struct Vector2 : IEquatable<Vector2>
    {
        public float X;
        public float Y;

        public static readonly Vector2 Zero = new Vector2(0f, 0f);
        public static readonly Vector2 One = new Vector2(1f, 1f);
        public static readonly Vector2 UnitX = new Vector2(1f, 0f);
        public static readonly Vector2 UnitY = new Vector2(0f, 1f);

        public Vector2(float x, float y)
        {
            X = x;
            Y = y;
        }

        public Vector2(float value)
        {
            X = value;
            Y = value;
        }

        public float Length
        {
            get { return (float)Math.Sqrt(X * X + Y * Y); }
        }

        public float LengthSquared
        {
            get { return X * X + Y * Y; }
        }

        public static Vector2 operator +(Vector2 a, Vector2 b)
        {
            return new Vector2(a.X + b.X, a.Y + b.Y);
        }

        public static Vector2 operator -(Vector2 a, Vector2 b)
        {
            return new Vector2(a.X - b.X, a.Y - b.Y);
        }

        public static Vector2 operator -(Vector2 a)
        {
            return new Vector2(-a.X, -a.Y);
        }

        public static Vector2 operator *(Vector2 a, Vector2 b)
        {
            return new Vector2(a.X * b.X, a.Y * b.Y);
        }

        public static Vector2 operator *(Vector2 a, float scale)
        {
            return new Vector2(a.X * scale, a.Y * scale);
        }

        public static Vector2 operator *(float scale, Vector2 a)
        {
            return new Vector2(a.X * scale, a.Y * scale);
        }

        public static Vector2 operator /(Vector2 a, Vector2 b)
        {
            return new Vector2(a.X / b.X, a.Y / b.Y);
        }

        public static Vector2 operator /(Vector2 a, float scale)
        {
            var inverse = 1f / scale;
            return new Vector2(a.X * inverse, a.Y * inverse);
        }

        public static bool operator ==(Vector2 a, Vector2 b)
        {
            return a.X == b.X && a.Y == b.Y;
        }

        public static bool operator !=(Vector2 a, Vector2 b)
        {
            return !(a == b);
        }

        public static float Dot(Vector2 a, Vector2 b)
        {
            return a.X * b.X + a.Y * b.Y;
        }

        public static float Distance(Vector2 a, Vector2 b)
        {
            var dx = a.X - b.X;
            var dy = a.Y - b.Y;
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }

        public static float DistanceSquared(Vector2 a, Vector2 b)
        {
            var dx = a.X - b.X;
            var dy = a.Y - b.Y;
            return dx * dx + dy * dy;
        }

        public static Vector2 Lerp(Vector2 from, Vector2 to, float amount)
        {
            return new Vector2(
                from.X + (to.X - from.X) * amount,
                from.Y + (to.Y - from.Y) * amount);
        }

        public static Vector2 Min(Vector2 a, Vector2 b)
        {
            return new Vector2(a.X < b.X ? a.X : b.X, a.Y < b.Y ? a.Y : b.Y);
        }

        public static Vector2 Max(Vector2 a, Vector2 b)
        {
            return new Vector2(a.X > b.X ? a.X : b.X, a.Y > b.Y ? a.Y : b.Y);
        }

        public static Vector2 Clamp(Vector2 value, Vector2 min, Vector2 max)
        {
            return new Vector2(
                MathHelper.Clamp(value.X, min.X, max.X),
                MathHelper.Clamp(value.Y, min.Y, max.Y));
        }

        public void Normalize()
        {
            var length = Length;

            if (length < 1e-6f)
            {
                // A zero-length vector has no direction. Leaving it at zero is
                // what XNA does and what the legacy renderer assumed; returning
                // NaN here would poison every transform downstream.
                X = 0f;
                Y = 0f;
                return;
            }

            var inverse = 1f / length;
            X *= inverse;
            Y *= inverse;
        }

        public static Vector2 Normalize(Vector2 value)
        {
            value.Normalize();
            return value;
        }

        public static Vector2 Transform(Vector2 position, Matrix matrix)
        {
            return new Vector2(
                position.X * matrix.M11 + position.Y * matrix.M21 + matrix.M41,
                position.X * matrix.M12 + position.Y * matrix.M22 + matrix.M42);
        }

        public static Vector2 TransformNormal(Vector2 normal, Matrix matrix)
        {
            return new Vector2(
                normal.X * matrix.M11 + normal.Y * matrix.M21,
                normal.X * matrix.M12 + normal.Y * matrix.M22);
        }

        public override bool Equals(object obj)
        {
            return obj is Vector2 && Equals((Vector2)obj);
        }

        public bool Equals(Vector2 other)
        {
            return X == other.X && Y == other.Y;
        }

        public override int GetHashCode()
        {
            return X.GetHashCode() * 397 ^ Y.GetHashCode();
        }

        public override string ToString()
        {
            return "{X:" + X + " Y:" + Y + "}";
        }
    }
}
