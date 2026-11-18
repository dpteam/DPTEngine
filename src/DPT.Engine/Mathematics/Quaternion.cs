//
// DPT.Engine - quaternion.
// Copyright (C) 2026 DartPower Team. GPLv2.
//

using System;

namespace DPT.Engine.Mathematics
{
    /// <summary>
    /// A rotation quaternion, X/Y/Z/W.
    ///
    /// The object animation format stores rotation as four separate splines
    /// (one per component) which are then interpolated with a Catmull-style
    /// slerp, so the component order here is part of the file format.
    /// </summary>
    public struct Quaternion : IEquatable<Quaternion>
    {
        public float X;
        public float Y;
        public float Z;
        public float W;

        public static readonly Quaternion Identity = new Quaternion(0f, 0f, 0f, 1f);

        public Quaternion(float x, float y, float z, float w)
        {
            X = x;
            Y = y;
            Z = z;
            W = w;
        }

        public Quaternion(Vector3 vectorPart, float scalarPart)
        {
            X = vectorPart.X;
            Y = vectorPart.Y;
            Z = vectorPart.Z;
            W = scalarPart;
        }

        public float Length
        {
            get { return (float)Math.Sqrt(X * X + Y * Y + Z * Z + W * W); }
        }

        public float LengthSquared
        {
            get { return X * X + Y * Y + Z * Z + W * W; }
        }

        public static Quaternion operator +(Quaternion a, Quaternion b)
        {
            return new Quaternion(a.X + b.X, a.Y + b.Y, a.Z + b.Z, a.W + b.W);
        }

        public static Quaternion operator -(Quaternion a, Quaternion b)
        {
            return new Quaternion(a.X - b.X, a.Y - b.Y, a.Z - b.Z, a.W - b.W);
        }

        public static Quaternion operator -(Quaternion a)
        {
            return new Quaternion(-a.X, -a.Y, -a.Z, -a.W);
        }

        public static Quaternion operator *(Quaternion a, Quaternion b)
        {
            return Multiply(a, b);
        }

        public static Quaternion operator *(Quaternion a, float scale)
        {
            return new Quaternion(a.X * scale, a.Y * scale, a.Z * scale, a.W * scale);
        }

        public static bool operator ==(Quaternion a, Quaternion b)
        {
            return a.X == b.X && a.Y == b.Y && a.Z == b.Z && a.W == b.W;
        }

        public static bool operator !=(Quaternion a, Quaternion b)
        {
            return !(a == b);
        }

        public static Quaternion Multiply(Quaternion a, Quaternion b)
        {
            return new Quaternion(
                a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
                a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
                a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
                a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);
        }

        public static float Dot(Quaternion a, Quaternion b)
        {
            return a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;
        }

        public void Normalize()
        {
            var length = Length;

            if (length < 1e-6f)
            {
                // An all-zero quaternion is not a rotation. Identity is the only
                // answer that keeps an object visible and unrotated.
                X = 0f;
                Y = 0f;
                Z = 0f;
                W = 1f;
                return;
            }

            var inverse = 1f / length;
            X *= inverse;
            Y *= inverse;
            Z *= inverse;
            W *= inverse;
        }

        public static Quaternion Normalize(Quaternion value)
        {
            value.Normalize();
            return value;
        }

        public static Quaternion Conjugate(Quaternion value)
        {
            return new Quaternion(-value.X, -value.Y, -value.Z, value.W);
        }

        public static Quaternion CreateFromAxisAngle(Vector3 axis, float radians)
        {
            axis.Normalize();

            var half = radians * 0.5f;
            var sin = (float)Math.Sin(half);

            return new Quaternion(axis.X * sin, axis.Y * sin, axis.Z * sin, (float)Math.Cos(half));
        }

        public static Quaternion CreateFromYawPitchRoll(float yaw, float pitch, float roll)
        {
            var halfRoll = roll * 0.5f;
            var halfPitch = pitch * 0.5f;
            var halfYaw = yaw * 0.5f;

            var sinRoll = (float)Math.Sin(halfRoll);
            var cosRoll = (float)Math.Cos(halfRoll);
            var sinPitch = (float)Math.Sin(halfPitch);
            var cosPitch = (float)Math.Cos(halfPitch);
            var sinYaw = (float)Math.Sin(halfYaw);
            var cosYaw = (float)Math.Cos(halfYaw);

            return new Quaternion(
                cosYaw * sinPitch * cosRoll + sinYaw * cosPitch * sinRoll,
                sinYaw * cosPitch * cosRoll - cosYaw * sinPitch * sinRoll,
                cosYaw * cosPitch * sinRoll - sinYaw * sinPitch * cosRoll,
                cosYaw * cosPitch * cosRoll + sinYaw * sinPitch * sinRoll);
        }

        public static Quaternion CreateFromRotationMatrix(Matrix matrix)
        {
            var trace = matrix.M11 + matrix.M22 + matrix.M33;

            if (trace > 0f)
            {
                var s = (float)Math.Sqrt(trace + 1f);
                var inverse = 0.5f / s;

                return new Quaternion(
                    (matrix.M23 - matrix.M32) * inverse,
                    (matrix.M31 - matrix.M13) * inverse,
                    (matrix.M12 - matrix.M21) * inverse,
                    0.5f * s);
            }

            if (matrix.M11 >= matrix.M22 && matrix.M11 >= matrix.M33)
            {
                var s = (float)Math.Sqrt(1f + matrix.M11 - matrix.M22 - matrix.M33);
                var inverse = 0.5f / s;

                return new Quaternion(
                    0.5f * s,
                    (matrix.M12 + matrix.M21) * inverse,
                    (matrix.M13 + matrix.M31) * inverse,
                    (matrix.M23 - matrix.M32) * inverse);
            }

            if (matrix.M22 > matrix.M33)
            {
                var s = (float)Math.Sqrt(1f + matrix.M22 - matrix.M11 - matrix.M33);
                var inverse = 0.5f / s;

                return new Quaternion(
                    (matrix.M12 + matrix.M21) * inverse,
                    0.5f * s,
                    (matrix.M23 + matrix.M32) * inverse,
                    (matrix.M31 - matrix.M13) * inverse);
            }
            else
            {
                var s = (float)Math.Sqrt(1f + matrix.M33 - matrix.M11 - matrix.M22);
                var inverse = 0.5f / s;

                return new Quaternion(
                    (matrix.M13 + matrix.M31) * inverse,
                    (matrix.M23 + matrix.M32) * inverse,
                    0.5f * s,
                    (matrix.M12 - matrix.M21) * inverse);
            }
        }

        /// <summary>
        /// Shortest-arc spherical interpolation.
        ///
        /// The sign flip on a negative dot product is not optional: without it a
        /// rotation between two nearly-opposite keys spins the long way round,
        /// which in an animation reads as the object flipping inside out.
        /// </summary>
        public static Quaternion Slerp(Quaternion from, Quaternion to, float amount)
        {
            var cosOmega = Dot(from, to);

            if (cosOmega < 0f)
            {
                to = -to;
                cosOmega = -cosOmega;
            }

            float scaleFrom;
            float scaleTo;

            if (cosOmega > 0.9999999f)
            {
                // Almost identical: linear interpolation and renormalise, which
                // avoids dividing by a vanishing sine.
                scaleFrom = 1f - amount;
                scaleTo = amount;
            }
            else
            {
                var omega = (float)Math.Acos(cosOmega);
                var sinOmega = (float)Math.Sin(omega);
                var inverseSin = 1f / sinOmega;

                scaleFrom = (float)Math.Sin((1f - amount) * omega) * inverseSin;
                scaleTo = (float)Math.Sin(amount * omega) * inverseSin;
            }

            var result = new Quaternion(
                from.X * scaleFrom + to.X * scaleTo,
                from.Y * scaleFrom + to.Y * scaleTo,
                from.Z * scaleFrom + to.Z * scaleTo,
                from.W * scaleFrom + to.W * scaleTo);

            result.Normalize();
            return result;
        }

        public static Quaternion Lerp(Quaternion from, Quaternion to, float amount)
        {
            var result = new Quaternion(
                from.X + (to.X - from.X) * amount,
                from.Y + (to.Y - from.Y) * amount,
                from.Z + (to.Z - from.Z) * amount,
                from.W + (to.W - from.W) * amount);

            result.Normalize();
            return result;
        }

        public static Quaternion Concatenate(Quaternion a, Quaternion b)
        {
            return Multiply(b, a);
        }

        public override bool Equals(object obj)
        {
            return obj is Quaternion && Equals((Quaternion)obj);
        }

        public bool Equals(Quaternion other)
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
