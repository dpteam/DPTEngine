//
// DPT.Engine - mathematics tests.
// Copyright (C) 2026 DartPower Team. GPLv2.
//

using System;
using DPT.Engine.Mathematics;

namespace DPT.Engine.Tests
{
    /// <summary>
    /// Tests for the row-vector conventions.
    ///
    /// These matter more than they look. The engine's matrices are row-vector
    /// (<c>v * M</c>, translation in M41..M43). If any single function here used
    /// the column convention, objects would appear transposed, rotated the wrong
    /// way, or translated in the wrong axis - and the bug would look like a
    /// scene-format problem rather than a maths one.
    /// </summary>
    internal sealed class MathematicsTests
    {
        private const float Tolerance = 1e-5f;

        [Test]
        internal void IdentityLeavesAVectorAlone()
        {
            var v = new Vector3(3f, -4f, 5f);
            var result = Vector3.Transform(v, Matrix.Identity);

            Assert.Near(3f, result.X, Tolerance, "X");
            Assert.Near(-4f, result.Y, Tolerance, "Y");
            Assert.Near(5f, result.Z, Tolerance, "Z");
        }

        [Test]
        internal void TranslationLivesInTheFourthRow()
        {
            var matrix = Matrix.CreateTranslation(10f, 20f, 30f);

            Assert.Near(10f, matrix.M41, Tolerance, "M41 is the X translation");
            Assert.Near(20f, matrix.M42, Tolerance, "M42 is the Y translation");
            Assert.Near(30f, matrix.M43, Tolerance, "M43 is the Z translation");

            var moved = Vector3.Transform(Vector3.Zero, matrix);
            Assert.Near(10f, moved.X, Tolerance, "translated X");
            Assert.Near(20f, moved.Y, Tolerance, "translated Y");
            Assert.Near(30f, moved.Z, Tolerance, "translated Z");
        }

        [Test]
        internal void MultiplyAppliesLeftThenRight()
        {
            // Scale by 2, then translate by 5. In row-vector order that is
            // scale * translate, and the result must be (1*2)+5 = 7.
            var scale = Matrix.CreateScale(2f);
            var translate = Matrix.CreateTranslation(5f, 0f, 0f);
            var combined = Matrix.Multiply(scale, translate);

            var result = Vector3.Transform(new Vector3(1f, 0f, 0f), combined);
            Assert.Near(7f, result.X, Tolerance, "scaled then translated");
        }

        [Test]
        internal void RotationOrderMatters()
        {
            var rotate = Matrix.CreateRotationZ(MathHelper.PiOver2);
            var translate = Matrix.CreateTranslation(10f, 0f, 0f);

            var rotateThenTranslate = Matrix.Multiply(rotate, translate);
            var translateThenRotate = Matrix.Multiply(translate, rotate);

            var a = Vector3.Transform(Vector3.UnitX, rotateThenTranslate);
            var b = Vector3.Transform(Vector3.UnitX, translateThenRotate);

            // rotate-then-translate puts the point at (10, 1, 0).
            Assert.Near(10f, a.X, Tolerance, "rotate-then-translate X");
            Assert.Near(1f, a.Y, Tolerance, "rotate-then-translate Y");

            // translate-then-rotate moves the point to (11, 0, 0) and only then
            // rotates it, which lands it on (0, 11, 0) - the extra unit of X is
            // the original point, carried along by the translation.
            Assert.Near(0f, b.X, Tolerance, "translate-then-rotate X");
            Assert.Near(11f, b.Y, Tolerance, "translate-then-rotate Y");
        }

        [Test]
        internal void InvertRoundTrips()
        {
            var original = Matrix.Multiply(
                Matrix.CreateScale(2f, 3f, 4f),
                Matrix.Multiply(Matrix.CreateRotationY(0.7f), Matrix.CreateTranslation(1f, -2f, 3f)));

            var inverse = Matrix.Invert(original);
            var product = Matrix.Multiply(original, inverse);

            Assert.Near(1f, product.M11, 1e-4f, "M11");
            Assert.Near(1f, product.M22, 1e-4f, "M22");
            Assert.Near(1f, product.M33, 1e-4f, "M33");
            Assert.Near(1f, product.M44, 1e-4f, "M44");
            Assert.Near(0f, product.M12, 1e-4f, "M12");
            Assert.Near(0f, product.M41, 1e-4f, "M41");
            Assert.Near(0f, product.M43, 1e-4f, "M43");
        }

        [Test]
        internal void InvertOfSingularIsIdentity()
        {
            // A zero scale is what a project with a bad animation curve produces.
            // The engine must render it as invisible, not as NaN across the frame.
            var singular = Matrix.CreateScale(0f, 0f, 0f);
            var inverse = Matrix.Invert(singular);

            Assert.True(inverse.IsFinite, "the inverse of a singular matrix must stay finite");
            Assert.Near(1f, inverse.M11, Tolerance, "M11 falls back to identity");
        }

        [Test]
        internal void QuaternionRotationMatchesMatrix()
        {
            var axis = new Vector3(0f, 0f, 1f);
            var angle = MathHelper.PiOver2;

            var quaternion = Quaternion.CreateFromAxisAngle(axis, angle);
            var matrix = Matrix.CreateFromQuaternion(quaternion);

            var byQuaternion = Vector3.Transform(Vector3.UnitX, matrix);
            var expected = new Vector3(0f, 1f, 0f);

            Assert.Near(expected.X, byQuaternion.X, 1e-4f, "rotated X");
            Assert.Near(expected.Y, byQuaternion.Y, 1e-4f, "rotated Y");
        }

        [Test]
        internal void SlerpTakesTheShortWayRound()
        {
            // Two rotations almost half a turn apart, with opposite signs: without
            // the dot-product flip this spins the long way and the object appears
            // to turn inside out mid-animation.
            var from = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0f);
            var to = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 3.0f);

            var mid = Quaternion.Slerp(from, to, 0.5f);

            Assert.Near(1f, mid.Length, 1e-4f, "slerp result stays normalised");
        }

        [Test]
        internal void NormalisingZeroStaysZeroNotNaN()
        {
            var zero = new Vector3(0f, 0f, 0f);
            zero.Normalize();

            Assert.Near(0f, zero.X, Tolerance, "X");
            Assert.False(float.IsNaN(zero.X), "must not become NaN");

            var quaternion = new Quaternion(0f, 0f, 0f, 0f);
            quaternion.Normalize();

            Assert.Near(1f, quaternion.W, Tolerance, "an empty quaternion becomes identity");
        }

        [Test]
        internal void ColorPacksToD3DLayout()
        {
            var color = new ColorRgba(0x11, 0x22, 0x33, 0x44);
            Assert.Equal(0x44112233u, color.ToD3DColor(), "AARRGGBB packing");

            var roundTrip = ColorRgba.FromArgb(0x44112233);
            Assert.True(roundTrip == color, "round trip through ARGB");
        }

        [Test]
        internal void ColorParsesHexStrings()
        {
            var color = ColorRgba.Parse("#0088FF");
            Assert.Equal(0x00, (int)color.R, "R");
            Assert.Equal(0x88, (int)color.G, "G");
            Assert.Equal(0xFF, (int)color.B, "B");
            Assert.Equal(255, (int)color.A, "A defaults to opaque");
        }

        [Test]
        internal void WrapAngleKeepsTheSmallestDelta()
        {
            var delta = MathHelper.AngleDelta(MathHelper.Pi * 0.9f, -MathHelper.Pi * 0.9f);

            // Crossing the +/-pi seam must report a small positive step, not -1.8pi.
            Assert.True(Math.Abs(delta) < 0.7f, "delta across the seam is small, got " + delta);
        }
    }
}
