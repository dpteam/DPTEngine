//
// DPT.Engine - procedural primitive generation.
// Copyright (C) 2026 DartPower Team. GPLv2.
//

using System;
using System.Collections.Generic;

using DPT.Engine.Mathematics;

namespace DPT.Engine.Scenes
{
    /// <summary>
    /// One triangle of generated geometry.
    ///
    /// Addict2 stores one normal per polygon and one texture coordinate per
    /// polygon corner, so expanding to triangles is the faithful representation,
    /// not a simplification: the original never shared a normal between two
    /// polygons either.
    /// </summary>
    public sealed class GeneratedTriangle
    {
        public Vector3 P0;
        public Vector3 P1;
        public Vector3 P2;

        /// <summary>One normal per triangle, as Addict2 uses.</summary>
        public Vector3 Normal;

        public Vector2 T0;
        public Vector2 T1;
        public Vector2 T2;
    }

    /// <summary>
    /// Rebuilds the geometry Addict2 generated from a primitive recipe.
    ///
    /// A .dmo project stores most objects as a primitive id plus five integers
    /// (and, for a supershape, its parameter block), not as baked vertices. This
    /// class is the port of <c>ObjectGenerators.cpp: GeneratePrimitive</c> and
    /// its helpers, so an original demo can be rendered without the original
    /// binary.
    ///
    /// Two conventions are copied from the reference engine on purpose and are
    /// easy to "fix" by accident:
    ///
    ///  * The face normal is <c>normalize(cross(P2 - P0, P1 - P0))</c>. The
    ///    arguments are in that order in <c>OBJECT::CalculateNormals</c>; the
    ///    usual right-hand rule would give the negated vector and every object
    ///    would be lit from behind.
    ///  * Texture coordinates are the raw per-corner values the generator wrote.
    ///    <c>CalculateTextureCoordinates</c> only multiplies them by the object's
    ///    tiling and offset, which are identity on a freshly generated object;
    ///    the caller applies tiling when it builds its material.
    ///
    /// Per-polygon shading flags (flat or gouraud) are not geometry and are not
    /// carried here: the renderer decides how to shade a mesh.
    ///
    /// Nothing here throws and nothing here loops unbounded: a demo file from
    /// 2004 may hold a parameter combination the generator cannot build, and a
    /// broken object has to render as something or nothing, never take the
    /// engine down.
    /// </summary>
    public static class PrimitiveGenerator
    {
        // Primitive ids, verbatim from ObjectGenerators.h.
        public const int Box = 0;
        public const int Sphere = 1;
        public const int Cylinder = 2;
        public const int Cone = 3;
        public const int Plane = 4;
        public const int Arc = 5;
        public const int Line = 6;
        public const int Loft = 7;
        public const int Icosaeder = 8;
        public const int Dodecaeder = 9;
        public const int Circle = 10;
        public const int Clone = 11;
        public const int Stored = 12;
        public const int Fur = 13;
        public const int SuperShape = 14;

        /// <summary>
        /// Highest subdivision count accepted on one axis.
        ///
        /// The original had no limit: a corrupt Param1 of 2 000 000 in a .dmo
        /// would allocate until the machine gave up. 256 is far above anything
        /// the modeller's sliders could produce and keeps the worst case at
        /// roughly 130 000 triangles.
        /// </summary>
        private const int MaxResolution = 256;

        /// <summary>Hard ceiling on emitted triangles, as a last line of defence.</summary>
        private const int MaxTriangles = 262144;

        /// <summary>
        /// Squared cross-product length below which a triangle is dropped.
        ///
        /// A zero-area triangle has no normal - <c>V3_Normalize</c> would have
        /// produced NaN in the original - and contributes nothing to the image,
        /// so it is skipped rather than given an invented normal. The bound is
        /// tiny on purpose: fur planes shrink by a factor of forty per step and
        /// the last ones are legitimately microscopic, but they still have a
        /// well-defined direction.
        /// </summary>
        private const float DegenerateEpsilonSquared = 1e-30f;

        /// <summary>
        /// True when this primitive id is one this class can build into triangles.
        ///
        /// False for the ids that carry their own geometry (clone, stored), for
        /// the two wireframe primitives (arc, line - Addict2 gives them edges and
        /// no polygons, see <see cref="GeneratePolyline"/>), for the loft (it
        /// needs the vertex lists of two other objects, which an integer
        /// parameter cannot carry - see <see cref="GenerateLoft"/>), for the fur
        /// (it needs a host object, see <see cref="GenerateFur"/>) and for the
        /// icosaeder and dodecaeder, which the reference generator declares but
        /// never implements.
        /// </summary>
        public static bool CanGenerate(int primitive)
        {
            switch (primitive)
            {
                case Box:
                case Sphere:
                case Cylinder:
                case Cone:
                case Plane:
                case Circle:
                case SuperShape:
                    return true;

                default:
                    return false;
            }
        }

        /// <summary>
        /// Builds a primitive. Returns an empty list (never null, never throws)
        /// for an id it does not handle.
        ///
        /// Primitive ids match Addict2: 0 box, 1 sphere, 2 cylinder, 3 cone,
        /// 4 plane, 5 arc, 6 line, 7 loft, 8 icosaeder, 9 dodecaeder, 10 circle,
        /// 13 fur, 14 supershape. 11 clone and 12 stored carry their own geometry
        /// and are handled by the caller.
        /// </summary>
        /// <param name="supershape">
        /// The raw <c>SUPERSHAPE</c> parameter block for primitive 14, exactly as
        /// the .dmo file stores it (an MSVC-laid-out struct, 100 bytes). Ignored
        /// for every other primitive; may be null.
        /// </param>
        public static List<GeneratedTriangle> Generate(
            int primitive, int param1, int param2, int param3, int param4, int param5,
            byte[] supershape)
        {
            var builder = new Builder();

            try
            {
                switch (primitive)
                {
                    case Box:
                        BuildBox(builder);
                        break;

                    case Sphere:
                        BuildSphere(builder, param1, param2);
                        break;

                    case Cylinder:
                        BuildCylinder(builder, param1, param2, param3);
                        break;

                    case Cone:
                        BuildCone(builder, param1, param2, param3);
                        break;

                    case Plane:
                        BuildPlane(builder, param1, param2);
                        break;

                    case Circle:
                        BuildCircle(builder, param1, param2);
                        break;

                    case SuperShape:
                        BuildSuperShape(builder, supershape);
                        break;

                    // Arc, line: wireframe primitives, no polygons - see
                    // GeneratePolyline. Loft: needs two vertex lists, see
                    // GenerateLoft. Fur: needs a host object, see GenerateFur.
                    // Icosaeder and dodecaeder: the reference switch has no case
                    // for them, so the original engine produced an empty object
                    // too. Clone and stored: the caller owns the geometry.
                    default:
                        break;
                }
            }
            catch (Exception)
            {
                // Whatever was built before the failure is still valid geometry;
                // returning it beats losing the whole object, and it definitely
                // beats aborting a scene load.
            }

            return builder.Triangles;
        }

        /// <summary>
        /// The vertex chain of a wireframe primitive (5 arc, 6 line).
        ///
        /// <c>ObjectGenerators.cpp: aDDict_LINE</c> and <c>aDDict_ARC</c> only
        /// call <c>AddVertex</c> and <c>AddEdge</c>, so there is no triangle to
        /// return from <see cref="Generate"/>.
        ///
        /// The reference loop runs x from -1 to Param1 inclusive and adds an edge
        /// only when 0 &lt; x &lt; Param1, so the connected chain is the run
        /// x = 1 .. Param1 - one step past the range the vertex arithmetic was
        /// clearly written for, which is why the original line ends at
        /// z = Param1/(Param1-1) - 0.5 rather than at 0.5. That off-by-one is
        /// reproduced here rather than corrected: an arc authored in 2004 was
        /// authored against it. The vertices outside that run (x = -1, 0 and
        /// Param1 + 1's neighbour) are never connected to anything and are
        /// dropped.
        ///
        /// Param1 below 2 would divide by zero in the original, so it is raised
        /// to 2.
        ///
        /// Returns an empty list for any other primitive id.
        /// </summary>
        public static List<Vector3> GeneratePolyline(int primitive, int param1, int param2)
        {
            var points = new List<Vector3>();

            try
            {
                if (primitive == Line || primitive == Arc)
                {
                    var count = MathHelper.Clamp(param1, 2, MaxResolution);

                    // x runs 1..count, exactly as the connected chain does.
                    for (var x = 1; x <= count; x++)
                    {
                        if (primitive == Line)
                        {
                            points.Add(new Vector3(0f, 0f, -0.5f + x / (float)(count - 1)));
                        }
                        else
                        {
                            var phase = Radians(x / (float)(count - 1) * param2);
                            points.Add(new Vector3(
                                0.5f * (float)Math.Cos(phase), 0f, 0.5f * (float)Math.Sin(phase)));
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Same reasoning as Generate: hand back the points we have.
            }

            return points;
        }

        /// <summary>
        /// Fur, grown from the polygons of a host object.
        ///
        /// <c>ObjectGenerators.cpp: aDDict_FUR</c> reads the host object through
        /// <c>(OBJECT*)Param5</c>, which a five-integer recipe cannot express, so
        /// <see cref="Generate"/> returns nothing for primitive 13. A caller that
        /// does have the host geometry can come through here instead.
        ///
        /// The host triangles must already be in the same space the fur is
        /// wanted in: the original transformed them by a copy of the host's
        /// ModelView rounded through <c>KillFloat(...,2)</c>, and that matrix is
        /// not part of this API.
        ///
        /// The random stream is a port of the MSVC CRT generator the original
        /// seeded with <c>srand(115)</c>, so the same host and parameters produce
        /// the same fur on every run and on every machine.
        /// </summary>
        /// <param name="host">Host geometry; each triangle spawns Param2 planes.</param>
        /// <param name="furSize">Addict2 Param1: half-width of a plane, 0..255.</param>
        /// <param name="furPlanes">Addict2 Param2: planes per host polygon.</param>
        /// <param name="furHeight">Addict2 Param3: length of the first plane, 0..255.</param>
        public static List<GeneratedTriangle> GenerateFur(
            IList<GeneratedTriangle> host, int furSize, int furPlanes, int furHeight)
        {
            var builder = new Builder();

            try
            {
                if (host == null || host.Count == 0) return builder.Triangles;

                var planes = MathHelper.Clamp(furPlanes, 0, MaxResolution);
                if (planes == 0) return builder.Triangles;

                var random = new MsvcRandom(115);

                for (var polygon = 0; polygon < host.Count; polygon++)
                {
                    // A stored host object can carry two million polygons and the
                    // ceiling would be hit long before the list ends; stopping
                    // here keeps the worst case bounded in time as well as memory.
                    if (builder.IsFull) break;

                    var ha = host[polygon].P0;
                    var hb = host[polygon].P1;
                    var hc = host[polygon].P2;

                    var origin = (ha + hb + hc) / 3f;

                    // C++: V3_Normalize(V3_Cross(V3_Sub(Hc,Ha),V3_Sub(Hb,Ha))).
                    var direction = NormalizeOrZero(Vector3.Cross(hc - ha, hb - ha));

                    for (var y = 0; y < planes; y++)
                    {
                        var randomVector = NormalizeOrZero(new Vector3(
                            random.Next(), random.Next(), random.Next()));

                        var j = Vector3.Cross(direction, randomVector);
                        var k = Vector3.Cross(direction, j) * 0.5f;

                        // The original computes a jitter value here
                        // (r = (rand()/RAND_MAX - 0.5) * 2) and never uses it.
                        // It still consumes one value from the stream, so
                        // dropping the call would change every later plane.
                        random.Next();

                        // Both vectors are shortened in place and the new length
                        // carries into the next plane - a quirk of the original
                        // that makes fur taper towards the tip. Reproduced as is.
                        direction = direction * (furHeight / 255f);
                        k = k * (furSize / 255f);

                        var a = origin + k;
                        var b = origin + k + direction;
                        var c = origin - k + direction;
                        var d = origin - k;

                        var first = builder.AddVertex(a, 0f, 0f);
                        builder.AddVertex(b, 0f, 1f);
                        builder.AddVertex(c, 1f, 1f);
                        builder.AddVertex(d, 1f, 0f);

                        builder.AddPolygon(first, first + 1, first + 2);
                        builder.AddPolygon(first, first + 2, first + 3);
                    }
                }
            }
            catch (Exception)
            {
                // Partial fur is still fur.
            }

            return builder.Triangles;
        }

        /// <summary>
        /// Loft: sweeps one object's profile along another object's path.
        ///
        /// <c>ObjectGenerators.cpp: aDDict_LOFT</c> reads the two objects through
        /// <c>(OBJECT*)Param1</c> and <c>(OBJECT*)Param2</c>, which a five-integer
        /// recipe cannot express, so <see cref="Generate"/> returns nothing for
        /// primitive 7. A caller that has both vertex lists comes through here.
        ///
        /// Both lists must already be in a common space: the original transformed
        /// them by their objects' ModelView first.
        /// </summary>
        /// <param name="track">Path vertices; the sweep walks the middle of this list.</param>
        /// <param name="trackClosed">True when the path wraps (the C++ test is Param2 == 360).</param>
        /// <param name="shape">Profile vertices, read in their XZ plane.</param>
        /// <param name="shapeClosed">True when the profile wraps (Param2 == 360 as well).</param>
        public static List<GeneratedTriangle> GenerateLoft(
            IList<Vector3> track, bool trackClosed, IList<Vector3> shape, bool shapeClosed)
        {
            var builder = new Builder();

            try
            {
                if (track == null || shape == null) return builder.Triangles;
                if (track.Count < 4 || shape.Count < 4) return builder.Triangles;

                BuildLoft(builder, track, trackClosed, shape, shapeClosed);
            }
            catch (Exception)
            {
                // A half-swept loft still renders as something.
            }

            return builder.Triangles;
        }

        // ----------------------------------------------------------------- loft

        // ObjectGenerators.cpp: case aDDict_LOFT.
        //
        // A loft sweeps one object's vertex ring along another object's vertex
        // path. The C++ reads both through object pointers (Param1 and Param2),
        // so <see cref="Generate"/> cannot build it from integers; a caller that
        // has the two vertex lists comes through <see cref="GenerateLoft"/>.
        //
        // The reference transforms both lists by their objects' ModelView before
        // sweeping; here the points are taken as they are handed in, so the
        // caller passes them already in a common space.
        private static void BuildLoft(
            Builder builder, IList<Vector3> track, bool trackClosed, IList<Vector3> shape, bool shapeClosed)
        {
            // v1v/v2v are one past the last usable index, exactly as the C++
            // derives them: every ring skips the first and last vertex because
            // the sweep needs a vertex on either side to build a frame from.
            var v1v = track.Count - 2;
            var v2v = shape.Count - 2;

            if (trackClosed) v1v--;
            if (shapeClosed) v2v--;

            if (v1v < 2 || v2v < 2) return;

            var up = new Vector3(0f, 1f, 0f);
            var xx = 0;

            for (var xa = 1; xa <= v1v; xa++)
            {
                var va = track[xa - 1];
                var vb = track[xa];
                var vc = track[xa + 1];

                var a1 = NormalizeOrZero(vb - va);
                var a2 = NormalizeOrZero(vc - vb);
                var direction = NormalizeOrZero(a1 + a2);

                // The C++ does not normalise the frame here, and neither does
                // this: the length of Cross(Up, dir) is the sine of the angle
                // between them, so a steep path produces a narrower sweep.
                // Normalising would quietly change the shape of every loft.
                var nx = Vector3.Cross(up, direction);
                var ny = Vector3.Cross(nx, direction);

                // A path that runs straight up or down makes the frame collapse
                // (the cross with Up is zero) and the original would have written
                // every ring on top of itself. Pick any perpendicular instead so
                // the sweep stays visible and finite.
                if (nx.LengthSquared < 1e-12f)
                {
                    var fallback = Math.Abs(direction.Y) > 0.9f
                        ? new Vector3(0f, 0f, 1f)
                        : new Vector3(1f, 0f, 0f);

                    nx = Vector3.Cross(fallback, direction);
                    ny = Vector3.Cross(nx, direction);
                }

                var yy = 0;

                for (var ya = 1; ya <= v2v; ya++)
                {
                    var yv = shape[ya];

                    // Only X and Z of the profile are used: the profile is
                    // authored flat in its own XZ plane, and its Y is ignored.
                    var nv = nx * yv.X + ny * yv.Z + vb;

                    // Note the order: the vertex gets (u along the path, v
                    // around the profile), while the polygons below get them
                    // the other way round. That mismatch is in the original.
                    builder.AddVertex(nv, xx / (float)(v1v - 1), yy / (float)(v2v - 1));

                    yy++;
                }

                xx++;
            }

            // The polygon loop works with the un-decremented counts, which is
            // why the C++ restores them here.
            if (trackClosed) v1v++;
            if (shapeClosed) v2v++;

            for (var gx = 0; gx <= v2v - 2; gx++)
            {
                for (var gy = 0; gy <= v1v - 2; gy++)
                {
                    var xa = gx;
                    var ya = gy;
                    var x1 = gx + 1;
                    var y1 = gy + 1;
                    var v2vs = v2v;

                    if (shapeClosed)
                    {
                        v2vs--;
                        xa %= v2v - 1;
                        x1 %= v2v - 1;
                    }

                    if (trackClosed)
                    {
                        ya %= v1v - 1;
                        y1 %= v1v - 1;
                    }

                    var u0 = gx / ((float)v2v - 1f);
                    var u1 = (gx + 1) / ((float)v2v - 1f);
                    var v0 = gy / ((float)v1v - 1f);
                    var v1 = (gy + 1) / ((float)v1v - 1f);

                    builder.AddPolygon(
                        ya * v2vs + xa, ya * v2vs + x1, y1 * v2vs + x1,
                        u0, v0, u1, v0, u1, v1);

                    builder.AddPolygon(
                        ya * v2vs + xa, y1 * v2vs + x1, y1 * v2vs + xa,
                        u0, v0, u1, v1, u0, v1);
                }
            }
        }

        // ------------------------------------------------------------------ box

        // ObjectGenerators.cpp: case aDDict_BOX.
        // Six faces, each split into four triangles around a centre vertex.
        // No parameters: the box is always one unit on a side, centred on the
        // origin, and the texture coordinates are the ones written out by hand.
        private static void BuildBox(Builder builder)
        {
            builder.AddVertex(-0.5f, -0.5f, -0.5f, 0f, 0f); // 0
            builder.AddVertex(0.5f, -0.5f, -0.5f, 1f, 0f);  // 1
            builder.AddVertex(0.5f, 0.5f, -0.5f, 1f, 1f);   // 2
            builder.AddVertex(-0.5f, 0.5f, -0.5f, 0f, 1f);  // 3
            builder.AddVertex(0f, 0f, -0.5f, 0.5f, 0.5f);   // 4 centre of -Z

            builder.AddVertex(0.5f, -0.5f, 0.5f, 0f, 0f);   // 5
            builder.AddVertex(0.5f, 0.5f, 0.5f, 0f, 1f);    // 6
            builder.AddVertex(0.5f, 0f, 0f, 0.5f, 0.5f);    // 7 centre of +X

            builder.AddVertex(-0.5f, 0.5f, 0.5f, 1f, 0f);   // 8
            builder.AddVertex(-0.5f, -0.5f, 0.5f, 1f, 1f);  // 9
            builder.AddVertex(0f, 0f, 0.5f, 0.5f, 0.5f);    // 10 centre of +Z

            builder.AddVertex(-0.5f, 0f, 0f, 0.5f, 0.5f);   // 11 centre of -X
            builder.AddVertex(0f, -0.5f, 0f, 0.5f, 0.5f);   // 12 centre of -Y
            builder.AddVertex(0f, 0.5f, 0f, 0.5f, 0.5f);    // 13 centre of +Y

            builder.AddPolygon(0, 1, 4, 0f, 0f, 1f, 0f, 0.5f, 0.5f);
            builder.AddPolygon(1, 2, 4, 1f, 0f, 1f, 1f, 0.5f, 0.5f);
            builder.AddPolygon(2, 3, 4, 1f, 1f, 0f, 1f, 0.5f, 0.5f);
            builder.AddPolygon(3, 0, 4, 0f, 1f, 0f, 0f, 0.5f, 0.5f);

            builder.AddPolygon(1, 5, 7, 0f, 0f, 1f, 0f, 0.5f, 0.5f);
            builder.AddPolygon(5, 6, 7, 1f, 0f, 1f, 1f, 0.5f, 0.5f);
            builder.AddPolygon(6, 2, 7, 1f, 1f, 0f, 1f, 0.5f, 0.5f);
            builder.AddPolygon(2, 1, 7, 0f, 1f, 0f, 0f, 0.5f, 0.5f);

            builder.AddPolygon(5, 9, 10, 0f, 0f, 1f, 0f, 0.5f, 0.5f);
            builder.AddPolygon(9, 8, 10, 1f, 0f, 1f, 1f, 0.5f, 0.5f);
            builder.AddPolygon(8, 6, 10, 1f, 1f, 0f, 1f, 0.5f, 0.5f);
            builder.AddPolygon(6, 5, 10, 0f, 1f, 0f, 0f, 0.5f, 0.5f);

            builder.AddPolygon(9, 0, 11, 0f, 0f, 1f, 0f, 0.5f, 0.5f);
            builder.AddPolygon(0, 3, 11, 1f, 0f, 1f, 1f, 0.5f, 0.5f);
            builder.AddPolygon(3, 8, 11, 1f, 1f, 0f, 1f, 0.5f, 0.5f);
            builder.AddPolygon(8, 9, 11, 0f, 1f, 0f, 0f, 0.5f, 0.5f);

            builder.AddPolygon(9, 5, 12, 0f, 0f, 1f, 0f, 0.5f, 0.5f);
            builder.AddPolygon(5, 1, 12, 1f, 0f, 1f, 1f, 0.5f, 0.5f);
            builder.AddPolygon(1, 0, 12, 1f, 1f, 0f, 1f, 0.5f, 0.5f);
            builder.AddPolygon(0, 9, 12, 0f, 1f, 0f, 0f, 0.5f, 0.5f);

            builder.AddPolygon(3, 2, 13, 0f, 0f, 1f, 0f, 0.5f, 0.5f);
            builder.AddPolygon(2, 6, 13, 1f, 0f, 1f, 1f, 0.5f, 0.5f);
            builder.AddPolygon(6, 8, 13, 1f, 1f, 0f, 1f, 0.5f, 0.5f);
            builder.AddPolygon(8, 3, 13, 0f, 1f, 0f, 0f, 0.5f, 0.5f);
        }

        // --------------------------------------------------------------- sphere

        // ObjectGenerators.cpp: case aDDict_SPHERE.
        // Param1 stacks (including the two poles), Param2 segments around the
        // Y axis. The C++ builds the sphere with Z up and then swaps the axes in
        // AddVertex, so the object comes out Y up; that swap is kept.
        private static void BuildSphere(Builder builder, int param1, int param2)
        {
            var stacks = MathHelper.Clamp(param1, 3, MaxResolution);
            var segments = MathHelper.Clamp(param2, 3, MaxResolution);

            builder.AddVertex(0f, -0.5f, 0f, 0.5f, 0f); // 0 south pole
            builder.AddVertex(0f, 0.5f, 0f, 0.5f, 0f);  // 1 north pole

            for (var x = 1; x < stacks - 1; x++)
            {
                var theta = Radians(x / (float)(stacks - 1) * 180f - 90f);

                for (var y = 0; y < segments; y++)
                {
                    var phi = Radians(y / (float)segments * 360f);

                    var vx = (float)Math.Cos(theta) * (float)Math.Cos(phi) * 0.5f;
                    var vy = (float)Math.Cos(theta) * (float)Math.Sin(phi) * 0.5f;
                    var vz = (float)Math.Sin(theta) * 0.5f;

                    builder.AddVertex(vx, vz, -vy, x / (float)stacks, y / (float)segments);
                }
            }

            for (var x = 0; x < stacks - 1; x++)
            {
                var x1 = (x - 1) * segments;
                var x2 = x * segments;

                for (var y = 0; y < segments; y++)
                {
                    var y1 = y + 2;
                    var y2 = (y + 1) % segments + 2;

                    if (x > 0 && x < stacks - 2)
                    {
                        builder.AddPolygon(
                            x1 + y1, x2 + y1, x2 + y2,
                            x / (float)stacks, y / (float)segments,
                            (x + 1) / (float)stacks, y / (float)segments,
                            (x + 1) / (float)stacks, (y + 1) / (float)segments);

                        builder.AddPolygon(
                            x1 + y1, x2 + y2, x1 + y2,
                            x / (float)stacks, y / (float)segments,
                            (x + 1) / (float)stacks, (y + 1) / (float)segments,
                            x / (float)stacks, (y + 1) / (float)segments);
                    }
                    else if (x == 0)
                    {
                        builder.AddPolygon(
                            y2, 0, y1,
                            1f / stacks, (y + 1) / (float)segments,
                            0f, (y + 1) / (float)segments,
                            1f / stacks, y / (float)segments);
                    }
                    else
                    {
                        builder.AddPolygon(
                            x1 + y1, 1, x1 + y2,
                            x / (float)stacks, y / (float)segments,
                            1f, (y + 1) / (float)segments,
                            x / (float)stacks, (y + 1) / (float)segments);
                    }
                }
            }
        }

        // ------------------------------------------------------------- cylinder

        // ObjectGenerators.cpp: case aDDict_CYLINDER.
        // Param1 segments, Param2 rings between the two caps, Param3 non-zero to
        // cap the ends. Vertex 0 is the bottom centre, vertex 1 the top centre.
        private static void BuildCylinder(Builder builder, int param1, int param2, int param3)
        {
            var segments = MathHelper.Clamp(param1, 3, MaxResolution);
            var rings = MathHelper.Clamp(param2, 1, MaxResolution);
            var caps = param3 != 0;

            builder.AddVertex(0f, -0.5f, 0f, 0.5f, 0.5f); // 0
            builder.AddVertex(0f, 0.5f, 0f, 0.5f, 0.5f);  // 1

            for (var y = 0; y < rings + 1; y++)
            {
                for (var x = 0; x < segments; x++)
                {
                    var theta = Radians(x / (float)segments * 360f);

                    var vx = (float)Math.Cos(theta) * 0.5f;
                    var vy = (float)Math.Sin(theta) * 0.5f;
                    var vz = -0.5f + y / (float)rings;

                    builder.AddVertex(vx, vz, -vy, x / (float)segments, vz + 0.5f);
                }
            }

            for (var x = 0; x < segments; x++)
            {
                var x1 = x;
                var x2 = (x + 1) % segments;

                if (caps)
                {
                    // The cap texture coordinates trace the circle itself, so the
                    // cap looks like a disc rather than a smeared square.
                    var su0 = (float)Math.Sin(Radians(x / (float)segments * 360f)) * 0.5f + 0.5f;
                    var cu0 = (float)Math.Cos(Radians(x / (float)segments * 360f)) * 0.5f + 0.5f;
                    var su1 = (float)Math.Sin(Radians((x + 1) / (float)segments * 360f)) * 0.5f + 0.5f;
                    var cu1 = (float)Math.Cos(Radians((x + 1) / (float)segments * 360f)) * 0.5f + 0.5f;

                    builder.AddPolygon(0, x1 + 2, x2 + 2, 0.5f, 0.5f, su0, cu0, su1, cu1);
                    builder.AddPolygon(x1 + segments * rings + 2, 1, x2 + segments * rings + 2,
                        su0, cu0, 0.5f, 0.5f, su1, cu1);
                }

                for (var y = 0; y < rings; y++)
                {
                    var y1 = y * segments + 2;
                    var y2 = (y + 1) * segments + 2;

                    builder.AddPolygon(
                        x2 + y1, x1 + y1, x2 + y2,
                        (x + 1) / (float)segments, y / (float)rings,
                        x / (float)segments, y / (float)rings,
                        (x + 1) / (float)segments, (y + 1) / (float)rings);

                    builder.AddPolygon(
                        x2 + y2, x1 + y1, x1 + y2,
                        (x + 1) / (float)segments, (y + 1) / (float)rings,
                        x / (float)segments, y / (float)rings,
                        x / (float)segments, (y + 1) / (float)rings);
                }
            }
        }

        // ----------------------------------------------------------------- cone

        // ObjectGenerators.cpp: case aDDict_CONE.
        // Param1 segments, Param2 non-zero to cap the base, Param3 rings from the
        // apex down. Vertex 0 is the apex, vertex 1 the base centre.
        private static void BuildCone(Builder builder, int param1, int param2, int param3)
        {
            var segments = MathHelper.Clamp(param1, 3, MaxResolution);
            var rings = MathHelper.Clamp(param3, 1, MaxResolution);
            var cap = param2 != 0;

            builder.AddVertex(0f, 0.5f, 0f, 0.5f, 0.5f);  // 0 apex
            builder.AddVertex(0f, -0.5f, 0f, 0.5f, 0.5f); // 1 base centre

            for (var y = 1; y <= rings; y++)
            {
                for (var x = 0; x < segments; x++)
                {
                    var theta = Radians(x / (float)segments * 360f);

                    var vx = (float)Math.Cos(theta) * 0.5f * y / (float)rings;
                    var vy = (float)Math.Sin(theta) * 0.5f * y / (float)rings;
                    var vz = -(-0.5f + y / (float)rings);

                    // The texture coordinates come from the circle the ring was
                    // built on, not from the final position - that is what makes
                    // the cone's texture run straight up from the base.
                    builder.AddVertex(vx, vz, -vy, vx + 0.5f, vy + 0.5f);
                }
            }

            for (var x = 0; x < segments; x++)
            {
                var x1 = x;
                var x2 = (x + 1) % segments;

                builder.AddPolygon(x1 + 2, 0, x2 + 2);

                if (cap)
                {
                    builder.AddPolygon(1, x1 + segments * (rings - 1) + 2, x2 + segments * (rings - 1) + 2);
                }

                for (var y = 0; y < rings - 1; y++)
                {
                    var y1 = y * segments + 2;
                    var y2 = (y + 1) * segments + 2;

                    builder.AddPolygon(x1 + y1, x2 + y1, x2 + y2);
                    builder.AddPolygon(x1 + y1, x2 + y2, x1 + y2);
                }
            }
        }

        // ---------------------------------------------------------------- plane

        // ObjectGenerators.cpp: case aDDict_PLANE.
        // Param1 by Param2 quads on the XZ plane, one unit across, facing up.
        private static void BuildPlane(Builder builder, int param1, int param2)
        {
            var columns = MathHelper.Clamp(param1, 1, MaxResolution);
            var rows = MathHelper.Clamp(param2, 1, MaxResolution);

            for (var x = 0; x <= columns; x++)
            {
                for (var y = 0; y <= rows; y++)
                {
                    builder.AddVertex(
                        x / (float)columns - 0.5f, 0f, y / (float)rows - 0.5f,
                        x / (float)columns, y / (float)rows);
                }
            }

            for (var x = 0; x < columns; x++)
            {
                var x1 = x * (rows + 1);
                var x2 = (x + 1) * (rows + 1);

                for (var y = 0; y < rows; y++)
                {
                    var y1 = y;
                    var y2 = (y + 1) % (rows + 1);

                    builder.AddPolygon(x1 + y1, x2 + y1, x2 + y2);
                    builder.AddPolygon(x1 + y1, x2 + y2, x1 + y2);
                }
            }
        }

        // --------------------------------------------------------------- circle

        // ObjectGenerators.cpp: case aDDict_CIRCLE.
        // A flat ring: Param1 segments, Param2 the inner radius as 0..255 of the
        // 0.5 outer radius.
        private static void BuildCircle(Builder builder, int param1, int param2)
        {
            var segments = MathHelper.Clamp(param1, 3, MaxResolution);
            var inner = param2 / 255f * 0.5f;

            for (var x = 0; x < segments; x++)
            {
                var theta = Radians(x / (float)segments * 360f);

                var vx = (float)Math.Cos(theta);
                var vy = (float)Math.Sin(theta);

                builder.AddVertex(vx * 0.5f, 0f, -vy * 0.5f, x / (float)segments, 0f);
                builder.AddVertex(vx * inner, 0f, -vy * inner, x / (float)segments, 1f);
            }

            for (var x = 0; x < segments; x++)
            {
                var x1 = x % segments * 2;
                var x2 = (x + 1) % segments * 2;

                builder.AddPolygon(
                    x1, x2, x2 + 1,
                    x / (float)segments, 0f,
                    (x + 1) / (float)segments, 0f,
                    (x + 1) / (float)segments, 1f);

                builder.AddPolygon(
                    x1, x2 + 1, x1 + 1,
                    x / (float)segments, 0f,
                    (x + 1) / (float)segments, 1f,
                    x / (float)segments, 1f);
            }
        }

        // ----------------------------------------------------------- supershape

        // ObjectGenerators.cpp: case aDDict_SUPERSHAPE (and SuperShapeRad).
        // Param1 is a pointer to a SUPERSHAPE block in the original; here it is
        // the raw bytes of that block as the .dmo file stores them.
        private static void BuildSuperShape(Builder builder, byte[] block)
        {
            var s = SuperShapeParams.Parse(block);
            if (s == null) return;
            if (s.XRes < 1 || s.YRes < 1) return;

            var xRes = s.XRes;
            var yRes = s.YRes;

            var sphere = s.Sphere;

            for (var x = 0; x <= xRes; x++)
            {
                // Note the constant: the original uses 3.1415 here, not
                // radtheta, and divides before multiplying. Copied exactly.
                var phi = Lerp(s.RangeX1, s.RangeX2, x / (float)xRes) / 180f * 3.1415f;

                var r0 = sphere ? 0f : s.Trad0;

                var r1a = SuperShapeRad(s.Arc1.M, s.Arc1.N1, s.Arc1.N2, s.Arc1.N3, s.Arc1.A, s.Arc1.B, phi) + r0;

                if (s.RadFunc == 1)
                {
                    r1a *= s.Rada * phi;
                }
                else if (s.RadFunc == 2)
                {
                    r1a *= (float)(s.Rada * Math.Exp(s.Radb * phi));
                }

                // Unknown radial functions leave r1a untouched. The original
                // would have used an uninitialised local there; reproducing
                // that is not possible and not desirable.
                var vertical = s.Verta * phi;

                for (var y = 0; y <= yRes; y++)
                {
                    var ro = Lerp(s.RangeY1, s.RangeY2, y / (float)yRes) / 180f * 3.1415f;

                    var r2a = SuperShapeRad(s.Arc2.M, s.Arc2.N1, s.Arc2.N2, s.Arc2.N3, s.Arc2.A, s.Arc2.B, ro);

                    var cosPhi = (float)Math.Cos(phi);
                    var sinPhi = (float)Math.Sin(phi);
                    var cosRo = (float)Math.Cos(ro);
                    var sinRo = (float)Math.Sin(ro);

                    float px, py, pz;

                    if (sphere)
                    {
                        px = r1a * cosPhi * r2a * cosRo;
                        py = r1a * sinPhi * r2a * cosRo;
                        pz = r2a * sinRo;
                    }
                    else
                    {
                        px = cosPhi * (r1a + r2a * cosRo);
                        py = sinPhi * (r1a + r2a * cosRo);
                        pz = r2a * sinRo;
                    }

                    if (s.VertFunc) pz *= vertical;

                    // A degenerate parameter set can make SuperShapeRad return
                    // infinity; the original would have written NaN vertices and
                    // lost the whole object. One flat vertex is the lesser evil.
                    if (!MathHelper.IsFinite(px) || !MathHelper.IsFinite(py) || !MathHelper.IsFinite(pz))
                    {
                        px = 0f;
                        py = 0f;
                        pz = 0f;
                    }

                    builder.AddVertex(px, py, pz, x / (float)xRes, y / (float)yRes);
                }
            }

            for (var x = 0; x < xRes; x++)
            {
                for (var y = 0; y < yRes; y++)
                {
                    builder.AddPolygon(
                        x * (yRes + 1) + y,
                        (x + 1) * (yRes + 1) + y + 1,
                        (x + 1) * (yRes + 1) + y);

                    builder.AddPolygon(
                        x * (yRes + 1) + y,
                        x * (yRes + 1) + y + 1,
                        (x + 1) * (yRes + 1) + y + 1);
                }
            }
        }

        // ObjectGenerators.cpp: SuperShapeRad.
        //
        // Kept in the original's precision: the angle argument is computed in
        // float before it widens to double for cos/sin, and the exponent is a
        // float -1/n1 widened to double. Doing all of it in double shifts the
        // silhouette of a spiky shape by a pixel or two, which is exactly the
        // kind of drift this port exists to avoid.
        private static float SuperShapeRad(float m, float n1, float n2, float n3, float a, float b, float phi)
        {
            var angle = m * phi / 4f;

            var t1 = Math.Cos(angle) / a;
            t1 = Math.Abs(t1);
            t1 = Math.Pow(t1, n2);

            var t2 = Math.Sin(angle) / b;
            t2 = Math.Abs(t2);
            t2 = Math.Pow(t2, n3);

            var exponent = -1f / n1;
            var result = Math.Pow(t1 + t2, exponent);

            return MathHelper.IsFinite((float)result) ? (float)result : 0f;
        }

        // ---------------------------------------------------------------- helpers

        /// <summary>
        /// Degrees to radians exactly as the <c>radtheta</c> macro expands.
        ///
        /// The macro is <c>3.141592654f/180.0f</c> without parentheses, so
        /// <c>a*radtheta</c> is <c>a*pi/180</c> in float, not
        /// <c>a*(pi/180)</c>. The difference is one rounding step; copying the
        /// order keeps generated vertices bit-identical to the original.
        /// </summary>
        private static float Radians(float degrees)
        {
            return degrees * MathHelper.Pi / 180f;
        }

        // Vectors.cpp: LinearInterpolate.
        private static float Lerp(float min, float max, float position)
        {
            return min + (max - min) * position;
        }

        // V3_Normalize, but a zero-length vector stays zero instead of turning
        // into NaN (the original would have divided by zero).
        private static Vector3 NormalizeOrZero(Vector3 value)
        {
            var lengthSquared = value.LengthSquared;

            if (!(lengthSquared > 0f) || float.IsInfinity(lengthSquared)) return Vector3.Zero;

            return value * (1f / (float)Math.Sqrt(lengthSquared));
        }

        /// <summary>
        /// Port of the MSVC CRT generator Addict2 called through
        /// <c>srand(115)</c> / <c>rand()</c>.
        ///
        /// <c>_holdrand = _holdrand * 214013 + 2531011; return (_holdrand &gt;&gt; 16) &amp; 0x7fff;</c>
        /// with 32-bit wraparound, so <see cref="Next"/> yields 0..RAND_MAX
        /// (0x7fff) exactly as the CRT did. <see cref="System.Random"/> is
        /// deliberately not used: its sequence is not guaranteed across runtime
        /// versions, and fur that scatters differently on a patched machine is a
        /// bug nobody can reproduce.
        /// </summary>
        private sealed class MsvcRandom
        {
            private uint state;

            public MsvcRandom(int seed)
            {
                state = (uint)seed;
            }

            public int Next()
            {
                // unchecked: the CRT generator relies on 32-bit wraparound, and a
                // project built with overflow checking must not turn that into an
                // exception on the tenth call.
                unchecked
                {
                    state = state * 214013u + 2531011u;
                }

                return (int)((state >> 16) & 0x7fff);
            }
        }

        /// <summary>
        /// The SUPERSHAPE parameter block, read out of its on-disk layout.
        ///
        /// The .dmo file writes <c>sizeof(SUPERSHAPE)</c> bytes of an MSVC
        /// struct, so the field offsets below include the compiler's padding.
        /// Reading them field by field instead of reinterpreting the whole block
        /// keeps the parser independent of the array's alignment.
        /// </summary>
        private sealed class SuperShapeParams
        {
            private const int Arc1Offset = 0;
            private const int Arc2Offset = 24;
            private const int SphereOffset = 48;
            private const int Trad0Offset = 52;
            private const int RadFuncOffset = 60;
            private const int RadaOffset = 64;
            private const int RadbOffset = 68;
            private const int VertFuncOffset = 72;
            private const int VertaOffset = 76;
            private const int RangeX1Offset = 80;
            private const int RangeX2Offset = 84;
            private const int RangeY1Offset = 88;
            private const int RangeY2Offset = 92;
            private const int XResOffset = 96;
            private const int YResOffset = 97;

            /// <summary>Bytes needed before the last field this parser reads.</summary>
            private const int MinimumLength = YResOffset + 1;

            public SuperShapeArc Arc1;
            public SuperShapeArc Arc2;

            public bool Sphere;
            public float Trad0;

            public int RadFunc;
            public float Rada;
            public float Radb;

            public bool VertFunc;
            public float Verta;

            // int in the reference struct: `int Rangex1,Rangex2,Rangey1,Rangey2;`
            public int RangeX1;
            public int RangeX2;
            public int RangeY1;
            public int RangeY2;

            public int XRes;
            public int YRes;

            /// <summary>Returns null when the block is missing or too short to use.</summary>
            public static SuperShapeParams Parse(byte[] block)
            {
                if (block == null || block.Length < MinimumLength) return null;

                var result = new SuperShapeParams();

                result.Arc1 = SuperShapeArc.Parse(block, Arc1Offset);
                result.Arc2 = SuperShapeArc.Parse(block, Arc2Offset);

                result.Sphere = block[SphereOffset] != 0;
                result.Trad0 = ReadFloat(block, Trad0Offset);

                result.RadFunc = block[RadFuncOffset];
                result.Rada = ReadFloat(block, RadaOffset);
                result.Radb = ReadFloat(block, RadbOffset);

                result.VertFunc = block[VertFuncOffset] != 0;
                result.Verta = ReadFloat(block, VertaOffset);

                result.RangeX1 = BitConverter.ToInt32(block, RangeX1Offset);
                result.RangeX2 = BitConverter.ToInt32(block, RangeX2Offset);
                result.RangeY1 = BitConverter.ToInt32(block, RangeY1Offset);
                result.RangeY2 = BitConverter.ToInt32(block, RangeY2Offset);

                result.XRes = block[XResOffset];
                result.YRes = block[YResOffset];

                return result;
            }

            private static float ReadFloat(byte[] block, int offset)
            {
                return BitConverter.ToSingle(block, offset);
            }
        }

        /// <summary>One SUPERSHAPEARC: the m/n/a/b set that shapes one axis.</summary>
        private struct SuperShapeArc
        {
            public float M;
            public float N1;
            public float N2;
            public float N3;
            public float A;
            public float B;

            public static SuperShapeArc Parse(byte[] block, int offset)
            {
                var arc = new SuperShapeArc();

                // unsigned char mint, unsigned char mfloat, two padding bytes.
                // The C++ builds m as mint + mfloat/255.0f at the call site.
                var mint = block[offset];
                var mfloat = block[offset + 1];

                arc.M = mint + mfloat / 255f;
                arc.N1 = BitConverter.ToSingle(block, offset + 4);
                arc.N2 = BitConverter.ToSingle(block, offset + 8);
                arc.N3 = BitConverter.ToSingle(block, offset + 12);
                arc.A = BitConverter.ToSingle(block, offset + 16);
                arc.B = BitConverter.ToSingle(block, offset + 20);

                return arc;
            }
        }

        /// <summary>
        /// Accumulates vertices and polygons the way <c>OBJECT</c> did, then
        /// hands out triangles.
        ///
        /// Kept private and deliberately dumb: AddVertex/AddPolygon mirror the
        /// original signatures so the ported loops read like the C++ they came
        /// from, and the normal is computed once, at the moment the polygon is
        /// closed, exactly like <c>OBJECT::CalculateNormals</c>.
        /// </summary>
        private sealed class Builder
        {
            private readonly List<Vector3> positions = new List<Vector3>();
            private readonly List<Vector2> textureCoordinates = new List<Vector2>();

            public readonly List<GeneratedTriangle> Triangles = new List<GeneratedTriangle>();

            /// <summary>True once the triangle ceiling is reached, so callers can stop early.</summary>
            public bool IsFull
            {
                get { return Triangles.Count >= MaxTriangles; }
            }

            public int AddVertex(float x, float y, float z, float u, float v)
            {
                positions.Add(new Vector3(x, y, z));
                textureCoordinates.Add(new Vector2(u, v));
                return positions.Count - 1;
            }

            public int AddVertex(Vector3 position, float u, float v)
            {
                return AddVertex(position.X, position.Y, position.Z, u, v);
            }

            /// <summary>OBJECT::AddPolygon: the corner coordinates come from the vertices.</summary>
            public void AddPolygon(int a, int b, int c)
            {
                if (!IsVertex(a) || !IsVertex(b) || !IsVertex(c)) return;

                AddPolygon(a, b, c, textureCoordinates[a], textureCoordinates[b], textureCoordinates[c]);
            }

            /// <summary>OBJECT::AddPolygon with explicit per-corner coordinates.</summary>
            public void AddPolygon(int a, int b, int c, float u0, float v0, float u1, float v1, float u2, float v2)
            {
                AddPolygon(a, b, c, new Vector2(u0, v0), new Vector2(u1, v1), new Vector2(u2, v2));
            }

            private void AddPolygon(int a, int b, int c, Vector2 t0, Vector2 t1, Vector2 t2)
            {
                if (Triangles.Count >= MaxTriangles) return;
                if (!IsVertex(a) || !IsVertex(b) || !IsVertex(c)) return;

                var p0 = positions[a];
                var p1 = positions[b];
                var p2 = positions[c];

                // OBJECT::CalculateNormals: Cross(v3 - v1, v2 - v1), in that
                // order. Swapping the arguments flips every normal in the scene.
                var normal = Vector3.Cross(p2 - p0, p1 - p0);
                var lengthSquared = normal.LengthSquared;

                if (!(lengthSquared > DegenerateEpsilonSquared)) return; // also rejects NaN
                if (float.IsInfinity(lengthSquared)) return;

                normal = normal * (1f / (float)Math.Sqrt(lengthSquared));

                var triangle = new GeneratedTriangle();
                triangle.P0 = p0;
                triangle.P1 = p1;
                triangle.P2 = p2;
                triangle.Normal = normal;
                triangle.T0 = t0;
                triangle.T1 = t1;
                triangle.T2 = t2;

                Triangles.Add(triangle);
            }

            private bool IsVertex(int index)
            {
                return index >= 0 && index < positions.Count;
            }
        }
    }
}
