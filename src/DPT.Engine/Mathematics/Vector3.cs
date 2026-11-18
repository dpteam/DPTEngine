//
// DPT.Engine - 3D vector.
// Copyright (C) 2026 DartPower Team. GPLv2.
//

using System;

namespace DPT.Engine.Mathematics
{
    /// <summary>
    /// A three-component vector, laid out as X, Y, Z - the same layout the
    /// project format stores vertex positions and normals in.
    ///
    /// Row-vector convention throughout: <c>Vector3.Transform(p, m)</c> is
    /// <c>p * m</c>, so a translation lives in M41..M43. Mixing in a column-vector
    /// convention anywhere would silently transpose every transform in the
    /// engine, so this file and Matrix.cs have to agree.
    /// </summary>
    public struct Vector3 : IEquatable<Vector3>
    {
        public float X;
        public float Y;
        public float Z;

        public static readonly Vector3 Zero = new Vector3(0f, 0f, 0f);
        public static readonly Vector3 One = new Vector3(1f, 1f, 1f);
        public static readonly Vector3 UnitX = new Vector3(1f, 0f, 0f);
        public static readonly Vector3 UnitY = new Vector3(0f, 1f, 0f);
        public static readonly Vector3 UnitZ = new Vector3(0f, 0f, 1f);
        public static readonly Vector3 Up = new Vector3(0f, 1f, 0f);
        public static readonly Vector3 Down = new Vector3(0f, -1f, 0f);
        public static readonly Vector3 Right = new Vector3(1f, 0f, 0f);
        public static readonly Vector3 Left = new Vector3(-1f, 0f, 0f);
        public static readonly Vector3 Forward = new Vector3(0f, 0f, -1f);
        public static readonly Vector3 Backward = new Vector3(0f, 0f, 1f);

        public Vector3(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public Vector3(float value)
        {
            X = value;
            Y = value;
            Z = value;
        }

        public Vector3(Vector2 value, float z)
        {
            X = value.X;
            Y = value.Y;
            Z = z;
        }

        public float Length
        {
            get { return (float)Math.Sqrt(X * X + Y * Y + Z * Z); }
        }

        public float LengthSquared
        {
            get { return X * X + Y * Y + Z * Z; }
        }

        public static Vector3 operator +(Vector3 a, Vector3 b)
        {
            return new Vector3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        }

        public static Vector3 operator -(Vector3 a, Vector3 b)
        {
            return new Vector3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        }

        public static Vector3 operator -(Vector3 a)
        {
            return new Vector3(-a.X, -a.Y, -a.Z);
        }

        public static Vector3 operator *(Vector3 a, Vector3 b)
        {
            return new Vector3(a.X * b.X, a.Y * b.Y, a.Z * b.Z);
        }

        public static Vector3 operator *(Vector3 a, float scale)
        {
            return new Vector3(a.X * scale, a.Y * scale, a.Z * scale);
        }

        public static Vector3 operator *(float scale, Vector3 a)
        {
            return new Vector3(a.X * scale, a.Y * scale, a.Z * scale);
        }

        public static Vector3 operator /(Vector3 a, Vector3 b)
        {
            return new Vector3(a.X / b.X, a.Y / b.Y, a.Z / b.Z);
        }

        public static Vector3 operator /(Vector3 a, float scale)
        {
            var inverse = 1f / scale;
            return new Vector3(a.X * inverse, a.Y * inverse, a.Z * inverse);
        }

        public static bool operator ==(Vector3 a, Vector3 b)
        {
            return a.X == b.X && a.Y == b.Y && a.Z == b.Z;
        }

        public static bool operator !=(Vector3 a, Vector3 b)
        {
            return !(a == b);
        }

        public static float Dot(Vector3 a, Vector3 b)
        {
            return a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        }

        public static Vector3 Cross(Vector3 a, Vector3 b)
        {
            return new Vector3(
                a.Y * b.Z - a.Z * b.Y,
                a.Z * b.X - a.X * b.Z,
                a.X * b.Y - a.Y * b.X);
        }

        public static float Distance(Vector3 a, Vector3 b)
        {
            var dx = a.X - b.X;
            var dy = a.Y - b.Y;
            var dz = a.Z - b.Z;
            return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        public static float DistanceSquared(Vector3 a, Vector3 b)
        {
            var dx = a.X - b.X;
            var dy = a.Y - b.Y;
            var dz = a.Z - b.Z;
            return dx * dx + dy * dy + dz * dz;
        }

        public static Vector3 Lerp(Vector3 from, Vector3 to, float amount)
        {
            return new Vector3(
                from.X + (to.X - from.X) * amount,
                from.Y + (to.Y - from.Y) * amount,
                from.Z + (to.Z - from.Z) * amount);
        }

        public static Vector3 Min(Vector3 a, Vector3 b)
        {
            return new Vector3(
                a.X < b.X ? a.X : b.X,
                a.Y < b.Y ? a.Y : b.Y,
                a.Z < b.Z ? a.Z : b.Z);
        }

        public static Vector3 Max(Vector3 a, Vector3 b)
        {
            return new Vector3(
                a.X > b.X ? a.X : b.X,
                a.Y > b.Y ? a.Y : b.Y,
                a.Z > b.Z ? a.Z : b.Z);
        }

        public static Vector3 Clamp(Vector3 value, Vector3 min, Vector3 max)
        {
            return new Vector3(
                MathHelper.Clamp(value.X, min.X, max.X),
                MathHelper.Clamp(value.Y, min.Y, max.Y),
                MathHelper.Clamp(value.Z, min.Z, max.Z));
        }

        public static Vector3 Reflect(Vector3 vector, Vector3 normal)
        {
            var dot = Dot(vector, normal);
            return vector - 2f * dot * normal;
        }

        /// <summary>
        /// Normalises in place. A zero-length vector stays zero rather than
        /// becoming NaN: the particle system divides by the result and a NaN
        /// there blanks the whole frame with no diagnostic.
        /// </summary>
        public void Normalize()
        {
            var length = Length;

            if (length < 1e-6f)
            {
                X = 0f;
                Y = 0f;
                Z = 0f;
                return;
            }

            var inverse = 1f / length;
            X *= inverse;
            Y *= inverse;
            Z *= inverse;
        }

        public static Vector3 Normalize(Vector3 value)
        {
            value.Normalize();
            return value;
        }

        public static Vector3 Transform(Vector3 position, Matrix matrix)
        {
            return new Vector3(
                position.X * matrix.M11 + position.Y * matrix.M21 + position.Z * matrix.M31 + matrix.M41,
                position.X * matrix.M12 + position.Y * matrix.M22 + position.Z * matrix.M32 + matrix.M42,
                position.X * matrix.M13 + position.Y * matrix.M23 + position.Z * matrix.M33 + matrix.M43);
        }

        public static Vector3 TransformNormal(Vector3 normal, Matrix matrix)
        {
            return new Vector3(
                normal.X * matrix.M11 + normal.Y * matrix.M21 + normal.Z * matrix.M31,
                normal.X * matrix.M12 + normal.Y * matrix.M22 + normal.Z * matrix.M32,
                normal.X * matrix.M13 + normal.Y * matrix.M23 + normal.Z * matrix.M33);
        }

        /// <summary>
        /// Full homogeneous transform, returning the W component.
        ///
        /// Needed by the particle code: a deflected particle's position is
        /// transformed by an inverse matrix and the W tells the caller whether
        /// the point went behind the plane.
        /// </summary>
        public static Vector3 Transform(Vector3 position, Matrix matrix, out float w)
        {
            var x = position.X * matrix.M11 + position.Y * matrix.M21 + position.Z * matrix.M31 + matrix.M41;
            var y = position.X * matrix.M12 + position.Y * matrix.M22 + position.Z * matrix.M32 + matrix.M42;
            var z = position.X * matrix.M13 + position.Y * matrix.M23 + position.Z * matrix.M33 + matrix.M43;
            w = position.X * matrix.M14 + position.Y * matrix.M24 + position.Z * matrix.M34 + matrix.M44;
            return new Vector3(x, y, z);
        }

        public override bool Equals(object obj)
        {
            return obj is Vector3 && Equals((Vector3)obj);
        }

        public bool Equals(Vector3 other)
        {
            return X == other.X && Y == other.Y && Z == other.Z;
        }

        public override int GetHashCode()
        {
            var hash = X.GetHashCode();
            hash = hash * 397 ^ Y.GetHashCode();
            hash = hash * 397 ^ Z.GetHashCode();
            return hash;
        }

        public override string ToString()
        {
            return "{X:" + X + " Y:" + Y + " Z:" + Z + "}";
        }
    }
}
