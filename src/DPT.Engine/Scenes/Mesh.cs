//
// DPT.Engine - mesh geometry.
// Copyright (C) 2026 DartPower Team. GPLv2.
//

using System;
using System.Collections.Generic;

using DPT.Engine.Mathematics;

namespace DPT.Engine.Scenes
{
    /// <summary>
    /// One vertex: position, normal, texture coordinate.
    ///
    /// A struct with three vectors rather than the flat float layout the GPU
    /// wants, because the editor manipulates vertices and the renderer converts
    /// once at upload. The conversion lives in <see cref="Mesh"/>.
    /// </summary>
    public struct Vertex
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Vector2 TextureCoordinate;

        public Vertex(Vector3 position, Vector3 normal, Vector2 textureCoordinate)
        {
            Position = position;
            Normal = normal;
            TextureCoordinate = textureCoordinate;
        }
    }

    /// <summary>Which procedural recipe built a mesh, so it can be saved as parameters.</summary>
    public enum MeshPrimitive
    {
        Box = 0,
        Sphere = 1,
        Plane = 2,

        /// <summary>An explicit vertex list, used for imported geometry.</summary>
        Custom = 3
    }

    /// <summary>
    /// A triangle mesh.
    ///
    /// Indices are 32-bit here and narrowed to 16-bit at upload when the vertex
    /// count allows it: a mesh with 40 000 vertices is rare but legal, and silently
    /// wrapping its indices would draw garbage.
    /// </summary>
    public sealed class Mesh
    {
        public Vertex[] Vertices = new Vertex[0];
        public int[] Indices = new int[0];

        /// <summary>How this mesh was built. Stored in the project so it can be rebuilt from parameters.</summary>
        public MeshPrimitive Primitive = MeshPrimitive.Custom;

        public float PrimitiveSize = 2f;
        public int PrimitiveSegments = 16;
        public int PrimitiveRings = 8;

        /// <summary>
        /// How many times the texture repeats across the primitive.
        ///
        /// Stored separately from the vertex data because a primitive mesh is saved
        /// as its recipe, not as baked vertices: without this a tiled floor comes
        /// back from a save with one texture repeat stretched over the whole surface,
        /// which is how the template's grid disappeared on reload.
        /// </summary>
        public float TextureScale = 1f;

        /// <summary>Axis-aligned bounds in local space, used for culling.</summary>
        public Vector3 BoundsMin;
        public Vector3 BoundsMax;

        public int TriangleCount
        {
            get { return Indices.Length / 3; }
        }

        public bool IsEmpty
        {
            get { return Vertices.Length == 0 || Indices.Length < 3; }
        }

        /// <summary>
        /// Recomputes vertex normals from the faces.
        ///
        /// Area-weighted by construction: each face normal is added unnormalised
        /// to its three vertices, so a large triangle contributes more than a
        /// sliver. That is what makes a low-poly sphere shade smoothly without
        /// visible seams.
        /// </summary>
        public void RecalculateNormals()
        {
            for (var i = 0; i < Vertices.Length; i++)
            {
                Vertices[i].Normal = Vector3.Zero;
            }

            for (var i = 0; i + 2 < Indices.Length; i += 3)
            {
                var a = Indices[i];
                var b = Indices[i + 1];
                var c = Indices[i + 2];

                if (a < 0 || b < 0 || c < 0) continue;
                if (a >= Vertices.Length || b >= Vertices.Length || c >= Vertices.Length) continue;

                var edge1 = Vertices[c].Position - Vertices[a].Position;
                var edge2 = Vertices[b].Position - Vertices[a].Position;
                var normal = Vector3.Cross(edge1, edge2);

                Vertices[a].Normal += normal;
                Vertices[b].Normal += normal;
                Vertices[c].Normal += normal;
            }

            for (var i = 0; i < Vertices.Length; i++)
            {
                Vertices[i].Normal.Normalize();
            }
        }

        public void RecalculateBounds()
        {
            if (Vertices.Length == 0)
            {
                BoundsMin = Vector3.Zero;
                BoundsMax = Vector3.Zero;
                return;
            }

            var min = Vertices[0].Position;
            var max = Vertices[0].Position;

            for (var i = 1; i < Vertices.Length; i++)
            {
                min = Vector3.Min(min, Vertices[i].Position);
                max = Vector3.Max(max, Vertices[i].Position);
            }

            BoundsMin = min;
            BoundsMax = max;
        }

        public Vector3 BoundsCentre
        {
            get { return (BoundsMin + BoundsMax) * 0.5f; }
        }

        /// <summary>
        /// Multiplies every texture coordinate.
        ///
        /// A tiled floor is one quad with UVs running 0..40 rather than 0..1: the
        /// sampler wraps, so one small texture covers a large surface. Without this
        /// a 128-pixel grid stretched over sixty world units has lines narrower than
        /// a pixel and the floor renders as flat black - which is exactly how the
        /// template's first version looked.
        /// </summary>
        public void ScaleTextureCoordinates(float factor)
        {
            if (factor <= 0f || !MathHelper.IsFinite(factor)) return;

            for (var i = 0; i < Vertices.Length; i++)
            {
                Vertices[i].TextureCoordinate.X *= factor;
                Vertices[i].TextureCoordinate.Y *= factor;
            }

            TextureScale *= factor;
        }

        /// <summary>
        /// True when the mesh's bounding sphere is behind the camera plane.
        ///
        /// A sphere rather than a box: one dot product per object, and a false
        /// positive only costs a draw call that the GPU would have culled anyway.
        /// </summary>
        public bool IsBehind(Vector3 cameraPosition, Vector3 cameraForward, Matrix world)
        {
            var centre = Vector3.Transform(BoundsCentre, world);
            var radius = (BoundsMax - BoundsMin).Length * 0.5f;

            var toCentre = centre - cameraPosition;
            return Vector3.Dot(toCentre, cameraForward) < -radius;
        }

        // ---------------------------------------------------------------- primitives

        /// <summary>An axis-aligned box centred on the origin, one unit on a side.</summary>
        public static Mesh CreateBox(float size)
        {
            var half = size * 0.5f;

            var positions = new[]
            {
                new Vector3(-half, -half, -half), new Vector3(half, -half, -half),
                new Vector3(half, half, -half), new Vector3(-half, half, -half),
                new Vector3(-half, -half, half), new Vector3(half, -half, half),
                new Vector3(half, half, half), new Vector3(-half, half, half)
            };

            var indices = new[]
            {
                0, 1, 2, 0, 2, 3, // back
                4, 6, 5, 4, 7, 6, // front
                0, 3, 7, 0, 7, 4, // left
                1, 5, 6, 1, 6, 2, // right
                3, 2, 6, 3, 6, 7, // top
                0, 4, 5, 0, 5, 1  // bottom
            };

            var mesh = new Mesh();
            mesh.Primitive = MeshPrimitive.Box;
            mesh.PrimitiveSize = size;
            mesh.Vertices = new Vertex[positions.Length];

            for (var i = 0; i < positions.Length; i++)
            {
                mesh.Vertices[i] = new Vertex(positions[i], Vector3.Zero,
                    new Vector2(positions[i].X + half, positions[i].Y + half) / size);
            }

            mesh.Indices = indices;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>A UV sphere, as a stack of rings.</summary>
        public static Mesh CreateSphere(float radius, int segments, int rings)
        {
            if (segments < 3) segments = 3;
            if (rings < 2) rings = 2;

            var vertices = new List<Vertex>((segments + 1) * (rings + 1));
            var indices = new List<int>(segments * rings * 6);

            for (var ring = 0; ring <= rings; ring++)
            {
                var v = (float)ring / rings;
                var phi = v * MathHelper.Pi;
                var y = (float)Math.Cos(phi);
                var r = (float)Math.Sin(phi);

                for (var segment = 0; segment <= segments; segment++)
                {
                    var u = (float)segment / segments;
                    var theta = u * MathHelper.TwoPi;

                    var normal = new Vector3(
                        (float)Math.Cos(theta) * r,
                        y,
                        (float)Math.Sin(theta) * r);

                    vertices.Add(new Vertex(normal * radius, normal, new Vector2(u, v)));
                }
            }

            for (var ring = 0; ring < rings; ring++)
            {
                for (var segment = 0; segment < segments; segment++)
                {
                    var a = ring * (segments + 1) + segment;
                    var b = a + segments + 1;

                    indices.Add(a); indices.Add(b); indices.Add(a + 1);
                    indices.Add(b); indices.Add(b + 1); indices.Add(a + 1);
                }
            }

            var mesh = new Mesh();
            mesh.Primitive = MeshPrimitive.Sphere;
            mesh.PrimitiveSize = radius * 2f;
            mesh.PrimitiveSegments = segments;
            mesh.PrimitiveRings = rings;
            mesh.Vertices = vertices.ToArray();
            mesh.Indices = indices.ToArray();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>A plane on the XZ axis, facing up.</summary>
        public static Mesh CreatePlane(float width, float depth, int divisions)
        {
            if (divisions < 1) divisions = 1;

            var vertices = new List<Vertex>((divisions + 1) * (divisions + 1));
            var indices = new List<int>(divisions * divisions * 6);

            for (var z = 0; z <= divisions; z++)
            {
                for (var x = 0; x <= divisions; x++)
                {
                    var u = (float)x / divisions;
                    var v = (float)z / divisions;

                    var position = new Vector3((u - 0.5f) * width, 0f, (v - 0.5f) * depth);
                    vertices.Add(new Vertex(position, Vector3.Up, new Vector2(u, v)));
                }
            }

            for (var z = 0; z < divisions; z++)
            {
                for (var x = 0; x < divisions; x++)
                {
                    var a = z * (divisions + 1) + x;
                    var b = a + divisions + 1;

                    indices.Add(a); indices.Add(a + 1); indices.Add(b);
                    indices.Add(a + 1); indices.Add(b + 1); indices.Add(b);
                }
            }

            var mesh = new Mesh();
            mesh.Primitive = MeshPrimitive.Plane;
            mesh.PrimitiveSize = width;
            mesh.PrimitiveSegments = divisions;
            mesh.Vertices = vertices.ToArray();
            mesh.Indices = indices.ToArray();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
