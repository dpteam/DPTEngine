//
// DPT.Engine - 4x4 matrix.
// Copyright (C) 2026 DartPower Team. GPLv2.
//

using System;

namespace DPT.Engine.Mathematics
{
    /// <summary>
    /// A 4x4 matrix in the same row-major, row-vector layout XNA uses.
    ///
    /// Every transform in the engine is <c>v * M</c>, so translation is the
    /// fourth row and <see cref="M.Multiply"/> is <c>a * b</c> meaning "apply a,
    /// then b". The project format stores object transforms as compact floats
    /// read straight into M11..M44, so the field order here is a file format, not
    /// a style choice: reordering the fields would silently transpose every
    /// object in every existing project.
    /// </summary>
    public struct Matrix : IEquatable<Matrix>
    {
        public float M11, M12, M13, M14;
        public float M21, M22, M23, M24;
        public float M31, M32, M33, M34;
        public float M41, M42, M43, M44;

        public static readonly Matrix Identity = new Matrix(
            1f, 0f, 0f, 0f,
            0f, 1f, 0f, 0f,
            0f, 0f, 1f, 0f,
            0f, 0f, 0f, 1f);

        public Matrix(
            float m11, float m12, float m13, float m14,
            float m21, float m22, float m23, float m24,
            float m31, float m32, float m33, float m34,
            float m41, float m42, float m43, float m44)
        {
            M11 = m11; M12 = m12; M13 = m13; M14 = m14;
            M21 = m21; M22 = m22; M23 = m23; M24 = m24;
            M31 = m31; M32 = m32; M33 = m33; M34 = m34;
            M41 = m41; M42 = m42; M43 = m43; M44 = m44;
        }

        /// <summary>Translation part, as a vector. M41..M43.</summary>
        public Vector3 Translation
        {
            get { return new Vector3(M41, M42, M43); }
            set
            {
                M41 = value.X;
                M42 = value.Y;
                M43 = value.Z;
            }
        }

        public Vector3 Right
        {
            get { return new Vector3(M11, M12, M13); }
        }

        public Vector3 Up
        {
            get { return new Vector3(M21, M22, M23); }
        }

        public Vector3 Forward
        {
            get { return new Vector3(M31, M32, M33); }
        }

        public static Matrix operator *(Matrix a, Matrix b)
        {
            return Multiply(a, b);
        }

        public static Matrix operator *(Matrix a, float scale)
        {
            return new Matrix(
                a.M11 * scale, a.M12 * scale, a.M13 * scale, a.M14 * scale,
                a.M21 * scale, a.M22 * scale, a.M23 * scale, a.M24 * scale,
                a.M31 * scale, a.M32 * scale, a.M33 * scale, a.M34 * scale,
                a.M41 * scale, a.M42 * scale, a.M43 * scale, a.M44 * scale);
        }

        public static Matrix operator +(Matrix a, Matrix b)
        {
            return new Matrix(
                a.M11 + b.M11, a.M12 + b.M12, a.M13 + b.M13, a.M14 + b.M14,
                a.M21 + b.M21, a.M22 + b.M22, a.M23 + b.M23, a.M24 + b.M24,
                a.M31 + b.M31, a.M32 + b.M32, a.M33 + b.M33, a.M34 + b.M34,
                a.M41 + b.M41, a.M42 + b.M42, a.M43 + b.M43, a.M44 + b.M44);
        }

        public static bool operator ==(Matrix a, Matrix b)
        {
            return a.M11 == b.M11 && a.M12 == b.M12 && a.M13 == b.M13 && a.M14 == b.M14 &&
                   a.M21 == b.M21 && a.M22 == b.M22 && a.M23 == b.M23 && a.M24 == b.M24 &&
                   a.M31 == b.M31 && a.M32 == b.M32 && a.M33 == b.M33 && a.M34 == b.M34 &&
                   a.M41 == b.M41 && a.M42 == b.M42 && a.M43 == b.M43 && a.M44 == b.M44;
        }

        public static bool operator !=(Matrix a, Matrix b)
        {
            return !(a == b);
        }

        /// <summary>
        /// <c>a</c> then <c>b</c>, in row-vector terms.
        ///
        /// The expressions are grouped by column rather than written as sixteen
        /// dot products so the JIT keeps them in registers; this is the hottest
        /// function in the engine during a timeline scrub.
        /// </summary>
        public static Matrix Multiply(Matrix a, Matrix b)
        {
            return new Matrix(
                a.M11 * b.M11 + a.M12 * b.M21 + a.M13 * b.M31 + a.M14 * b.M41,
                a.M11 * b.M12 + a.M12 * b.M22 + a.M13 * b.M32 + a.M14 * b.M42,
                a.M11 * b.M13 + a.M12 * b.M23 + a.M13 * b.M33 + a.M14 * b.M43,
                a.M11 * b.M14 + a.M12 * b.M24 + a.M13 * b.M34 + a.M14 * b.M44,

                a.M21 * b.M11 + a.M22 * b.M21 + a.M23 * b.M31 + a.M24 * b.M41,
                a.M21 * b.M12 + a.M22 * b.M22 + a.M23 * b.M32 + a.M24 * b.M42,
                a.M21 * b.M13 + a.M22 * b.M23 + a.M23 * b.M33 + a.M24 * b.M43,
                a.M21 * b.M14 + a.M22 * b.M24 + a.M23 * b.M34 + a.M24 * b.M44,

                a.M31 * b.M11 + a.M32 * b.M21 + a.M33 * b.M31 + a.M34 * b.M41,
                a.M31 * b.M12 + a.M32 * b.M22 + a.M33 * b.M32 + a.M34 * b.M42,
                a.M31 * b.M13 + a.M32 * b.M23 + a.M33 * b.M33 + a.M34 * b.M43,
                a.M31 * b.M14 + a.M32 * b.M24 + a.M33 * b.M34 + a.M34 * b.M44,

                a.M41 * b.M11 + a.M42 * b.M21 + a.M43 * b.M31 + a.M44 * b.M41,
                a.M41 * b.M12 + a.M42 * b.M22 + a.M43 * b.M32 + a.M44 * b.M42,
                a.M41 * b.M13 + a.M42 * b.M23 + a.M43 * b.M33 + a.M44 * b.M43,
                a.M41 * b.M14 + a.M42 * b.M24 + a.M43 * b.M34 + a.M44 * b.M44);
        }

        public static Matrix Transpose(Matrix m)
        {
            return new Matrix(
                m.M11, m.M21, m.M31, m.M41,
                m.M12, m.M22, m.M32, m.M42,
                m.M13, m.M23, m.M33, m.M43,
                m.M14, m.M24, m.M34, m.M44);
        }

        public static Matrix CreateTranslation(Vector3 position)
        {
            return CreateTranslation(position.X, position.Y, position.Z);
        }

        public static Matrix CreateTranslation(float x, float y, float z)
        {
            var m = Identity;
            m.M41 = x;
            m.M42 = y;
            m.M43 = z;
            return m;
        }

        public static Matrix CreateScale(float scale)
        {
            return CreateScale(scale, scale, scale);
        }

        public static Matrix CreateScale(Vector3 scale)
        {
            return CreateScale(scale.X, scale.Y, scale.Z);
        }

        public static Matrix CreateScale(float x, float y, float z)
        {
            var m = Identity;
            m.M11 = x;
            m.M22 = y;
            m.M33 = z;
            return m;
        }

        public static Matrix CreateRotationX(float radians)
        {
            var m = Identity;
            var c = (float)Math.Cos(radians);
            var s = (float)Math.Sin(radians);

            m.M22 = c;
            m.M23 = s;
            m.M32 = -s;
            m.M33 = c;
            return m;
        }

        public static Matrix CreateRotationY(float radians)
        {
            var m = Identity;
            var c = (float)Math.Cos(radians);
            var s = (float)Math.Sin(radians);

            m.M11 = c;
            m.M13 = -s;
            m.M31 = s;
            m.M33 = c;
            return m;
        }

        public static Matrix CreateRotationZ(float radians)
        {
            var m = Identity;
            var c = (float)Math.Cos(radians);
            var s = (float)Math.Sin(radians);

            m.M11 = c;
            m.M12 = s;
            m.M21 = -s;
            m.M22 = c;
            return m;
        }

        public static Matrix CreateFromAxisAngle(Vector3 axis, float radians)
        {
            // Normalise defensively: the particle system passes a cross product
            // that can be zero when the emitter direction is parallel to a random
            // vector, and a non-unit axis produces a non-rotation matrix.
            axis.Normalize();

            var x = axis.X;
            var y = axis.Y;
            var z = axis.Z;
            var s = (float)Math.Sin(radians);
            var c = (float)Math.Cos(radians);
            var t = 1f - c;

            return new Matrix(
                x * x * t + c, x * y * t + z * s, x * z * t - y * s, 0f,
                x * y * t - z * s, y * y * t + c, y * z * t + x * s, 0f,
                x * z * t + y * s, y * z * t - x * s, z * z * t + c, 0f,
                0f, 0f, 0f, 1f);
        }

        public static Matrix CreateFromQuaternion(Quaternion quaternion)
        {
            var x = quaternion.X;
            var y = quaternion.Y;
            var z = quaternion.Z;
            var w = quaternion.W;

            var xx = x * x;
            var yy = y * y;
            var zz = z * z;
            var xy = x * y;
            var zw = z * w;
            var zx = z * x;
            var yw = y * w;
            var yz = y * z;
            var xw = x * w;

            return new Matrix(
                1f - 2f * (yy + zz), 2f * (xy + zw), 2f * (zx - yw), 0f,
                2f * (xy - zw), 1f - 2f * (zz + xx), 2f * (yz + xw), 0f,
                2f * (zx + yw), 2f * (yz - xw), 1f - 2f * (yy + xx), 0f,
                0f, 0f, 0f, 1f);
        }

        public static Matrix CreateLookAt(Vector3 cameraPosition, Vector3 cameraTarget, Vector3 cameraUp)
        {
            var zAxis = Vector3.Normalize(cameraPosition - cameraTarget);

            var xAxis = Vector3.Cross(cameraUp, zAxis);
            xAxis.Normalize();

            var yAxis = Vector3.Cross(zAxis, xAxis);

            return new Matrix(
                xAxis.X, yAxis.X, zAxis.X, 0f,
                xAxis.Y, yAxis.Y, zAxis.Y, 0f,
                xAxis.Z, yAxis.Z, zAxis.Z, 0f,
                -Vector3.Dot(xAxis, cameraPosition),
                -Vector3.Dot(yAxis, cameraPosition),
                -Vector3.Dot(zAxis, cameraPosition),
                1f);
        }

        public static Matrix CreatePerspectiveFieldOfView(float fieldOfView, float aspectRatio,
            float nearPlaneDistance, float farPlaneDistance)
        {
            if (fieldOfView <= 0f || fieldOfView >= MathHelper.Pi)
            {
                throw new ArgumentOutOfRangeException("fieldOfView");
            }

            if (nearPlaneDistance <= 0f)
            {
                throw new ArgumentOutOfRangeException("nearPlaneDistance");
            }

            if (farPlaneDistance <= 0f)
            {
                throw new ArgumentOutOfRangeException("farPlaneDistance");
            }

            if (nearPlaneDistance >= farPlaneDistance)
            {
                throw new ArgumentOutOfRangeException("nearPlaneDistance");
            }

            var range = 1f / (float)Math.Tan(fieldOfView * 0.5f);

            var m = new Matrix();
            m.M11 = range / aspectRatio;
            m.M22 = range;
            m.M33 = farPlaneDistance / (nearPlaneDistance - farPlaneDistance);
            m.M34 = -1f;
            m.M43 = nearPlaneDistance * farPlaneDistance / (nearPlaneDistance - farPlaneDistance);
            return m;
        }

        public static Matrix CreateOrthographicOffCenter(float left, float right, float bottom, float top,
            float nearPlaneDistance, float farPlaneDistance)
        {
            var m = new Matrix();
            m.M11 = 2f / (right - left);
            m.M22 = 2f / (top - bottom);
            m.M33 = 1f / (nearPlaneDistance - farPlaneDistance);
            m.M41 = (left + right) / (left - right);
            m.M42 = (top + bottom) / (bottom - top);
            m.M43 = nearPlaneDistance / (nearPlaneDistance - farPlaneDistance);
            m.M44 = 1f;
            return m;
        }

        /// <summary>
        /// General inverse, via cofactors.
        ///
        /// Returns <see cref="Identity"/> for a singular matrix instead of
        /// throwing or producing infinities: a zero-scale object in a project
        /// would otherwise kill the frame it first appears in, and "invisible
        /// object" is the correct visual result.
        /// </summary>
        public static Matrix Invert(Matrix matrix)
        {
            var a = matrix;

            var num17 = a.M33 * a.M44 - a.M34 * a.M43;
            var num18 = a.M32 * a.M44 - a.M34 * a.M42;
            var num19 = a.M32 * a.M43 - a.M33 * a.M42;
            var num20 = a.M31 * a.M44 - a.M34 * a.M41;
            var num21 = a.M31 * a.M43 - a.M33 * a.M41;
            var num22 = a.M31 * a.M42 - a.M32 * a.M41;

            var determinant =
                a.M11 * (a.M22 * num17 - a.M23 * num18 + a.M24 * num19) -
                a.M12 * (a.M21 * num17 - a.M23 * num20 + a.M24 * num21) +
                a.M13 * (a.M21 * num18 - a.M22 * num20 + a.M24 * num22) -
                a.M14 * (a.M21 * num19 - a.M22 * num21 + a.M23 * num22);

            if (Math.Abs(determinant) < 1e-12f || float.IsNaN(determinant) || float.IsInfinity(determinant))
            {
                return Identity;
            }

            var inv = 1f / determinant;

            var r = new Matrix();

            r.M11 = (a.M22 * num17 - a.M23 * num18 + a.M24 * num19) * inv;
            r.M12 = (-a.M12 * num17 + a.M13 * num18 - a.M14 * num19) * inv;
            r.M13 = (a.M12 * (a.M23 * a.M44 - a.M24 * a.M43) - a.M13 * (a.M22 * a.M44 - a.M24 * a.M42) +
                     a.M14 * (a.M22 * a.M43 - a.M23 * a.M42)) * inv;
            r.M14 = (-a.M12 * (a.M23 * a.M34 - a.M24 * a.M33) + a.M13 * (a.M22 * a.M34 - a.M24 * a.M32) -
                     a.M14 * (a.M22 * a.M33 - a.M23 * a.M32)) * inv;

            r.M21 = (-a.M21 * num17 + a.M23 * num20 - a.M24 * num21) * inv;
            r.M22 = (a.M11 * num17 - a.M13 * num20 + a.M14 * num21) * inv;
            r.M23 = (-a.M11 * (a.M23 * a.M44 - a.M24 * a.M43) + a.M13 * (a.M21 * a.M44 - a.M24 * a.M41) -
                     a.M14 * (a.M21 * a.M43 - a.M23 * a.M41)) * inv;
            r.M24 = (a.M11 * (a.M23 * a.M34 - a.M24 * a.M33) - a.M13 * (a.M21 * a.M34 - a.M24 * a.M31) +
                     a.M14 * (a.M21 * a.M33 - a.M23 * a.M31)) * inv;

            r.M31 = (a.M21 * num18 - a.M22 * num20 + a.M24 * num22) * inv;
            r.M32 = (-a.M11 * num18 + a.M12 * num20 - a.M14 * num22) * inv;
            r.M33 = (a.M11 * (a.M22 * a.M44 - a.M24 * a.M42) - a.M12 * (a.M21 * a.M44 - a.M24 * a.M41) +
                     a.M14 * (a.M21 * a.M42 - a.M22 * a.M41)) * inv;
            r.M34 = (-a.M11 * (a.M22 * a.M34 - a.M24 * a.M32) + a.M12 * (a.M21 * a.M34 - a.M24 * a.M31) -
                     a.M14 * (a.M21 * a.M32 - a.M22 * a.M31)) * inv;

            r.M41 = (-a.M21 * num19 + a.M22 * num21 - a.M23 * num22) * inv;
            r.M42 = (a.M11 * num19 - a.M12 * num21 + a.M13 * num22) * inv;
            r.M43 = (-a.M11 * (a.M22 * a.M43 - a.M23 * a.M42) + a.M12 * (a.M21 * a.M43 - a.M23 * a.M41) -
                     a.M13 * (a.M21 * a.M42 - a.M22 * a.M41)) * inv;
            r.M44 = (a.M11 * (a.M22 * a.M33 - a.M23 * a.M32) - a.M12 * (a.M21 * a.M33 - a.M23 * a.M31) +
                     a.M13 * (a.M21 * a.M32 - a.M22 * a.M31)) * inv;

            return r;
        }

        public static Matrix Lerp(Matrix a, Matrix b, float amount)
        {
            return new Matrix(
                a.M11 + (b.M11 - a.M11) * amount, a.M12 + (b.M12 - a.M12) * amount,
                a.M13 + (b.M13 - a.M13) * amount, a.M14 + (b.M14 - a.M14) * amount,
                a.M21 + (b.M21 - a.M21) * amount, a.M22 + (b.M22 - a.M22) * amount,
                a.M23 + (b.M23 - a.M23) * amount, a.M24 + (b.M24 - a.M24) * amount,
                a.M31 + (b.M31 - a.M31) * amount, a.M32 + (b.M32 - a.M32) * amount,
                a.M33 + (b.M33 - a.M33) * amount, a.M34 + (b.M34 - a.M34) * amount,
                a.M41 + (b.M41 - a.M41) * amount, a.M42 + (b.M42 - a.M42) * amount,
                a.M43 + (b.M43 - a.M43) * amount, a.M44 + (b.M44 - a.M44) * amount);
        }

        /// <summary>True when no component is NaN or infinite.</summary>
        public bool IsFinite
        {
            get
            {
                return MathHelper.IsFinite(M11) && MathHelper.IsFinite(M12) &&
                       MathHelper.IsFinite(M13) && MathHelper.IsFinite(M14) &&
                       MathHelper.IsFinite(M21) && MathHelper.IsFinite(M22) &&
                       MathHelper.IsFinite(M23) && MathHelper.IsFinite(M24) &&
                       MathHelper.IsFinite(M31) && MathHelper.IsFinite(M32) &&
                       MathHelper.IsFinite(M33) && MathHelper.IsFinite(M34) &&
                       MathHelper.IsFinite(M41) && MathHelper.IsFinite(M42) &&
                       MathHelper.IsFinite(M43) && MathHelper.IsFinite(M44);
            }
        }

        public float[] ToArray()
        {
            return new[]
            {
                M11, M12, M13, M14,
                M21, M22, M23, M24,
                M31, M32, M33, M34,
                M41, M42, M43, M44
            };
        }

        public override bool Equals(object obj)
        {
            return obj is Matrix && Equals((Matrix)obj);
        }

        public bool Equals(Matrix other)
        {
            return this == other;
        }

        public override int GetHashCode()
        {
            var hash = M11.GetHashCode();
            hash = hash * 397 ^ M12.GetHashCode();
            hash = hash * 397 ^ M13.GetHashCode();
            hash = hash * 397 ^ M14.GetHashCode();
            hash = hash * 397 ^ M21.GetHashCode();
            hash = hash * 397 ^ M22.GetHashCode();
            hash = hash * 397 ^ M23.GetHashCode();
            hash = hash * 397 ^ M24.GetHashCode();
            hash = hash * 397 ^ M31.GetHashCode();
            hash = hash * 397 ^ M32.GetHashCode();
            hash = hash * 397 ^ M33.GetHashCode();
            hash = hash * 397 ^ M34.GetHashCode();
            hash = hash * 397 ^ M41.GetHashCode();
            hash = hash * 397 ^ M42.GetHashCode();
            hash = hash * 397 ^ M43.GetHashCode();
            hash = hash * 397 ^ M44.GetHashCode();
            return hash;
        }

        public override string ToString()
        {
            return "{M11:" + M11 + " M12:" + M12 + " M13:" + M13 + " M14:" + M14 +
                   " M21:" + M21 + " M22:" + M22 + " M23:" + M23 + " M24:" + M24 +
                   " M31:" + M31 + " M32:" + M32 + " M33:" + M33 + " M34:" + M34 +
                   " M41:" + M41 + " M42:" + M42 + " M43:" + M43 + " M44:" + M44 + "}";
        }
    }
}
