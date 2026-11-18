//
// DPT.Engine - scene renderer.
// Copyright (C) 2026 DartPower Team. GPLv2.
//

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using DPT.Engine.Shared.Diagnostics;
using DPT.Engine.Graphics;
using DPT.Engine.Mathematics;
using DPT.Engine.Scenes;

namespace DPT.Engine.Rendering
{
    /// <summary>
    /// Draws a <see cref="Scene"/> with the Direct3D 9 device.
    ///
    /// The renderer owns the GPU-side mirror of every scene resource: vertex
    /// buffers, index buffers, textures and shader handles. Scene data itself is
    /// plain managed objects that the editor manipulates freely; the mirror is
    /// rebuilt when the scene is marked dirty.
    ///
    /// Two passes, in this order:
    ///
    ///   opaque   - depth write on, no blending. Front to back is not attempted:
    ///              early-z on any card from the last twenty years makes the
    ///              ordering worth less than the sort itself.
    ///
    ///   transparent - depth write off, blended, sorted back to front. Without
    ///              the sort, two overlapping additive glows combine in whatever
    ///              order the node list happens to be in, and a demo looks
    ///              different on every machine.
    ///
    /// Everything is wrapped in the fault guard: one mesh that fails to upload
    /// disables that mesh, not the frame.
    /// </summary>
    internal sealed class SceneRenderer : IDisposable
    {
        private readonly D3D9Device device;

        private readonly Dictionary<Mesh, MeshBuffers> meshes =
            new Dictionary<Mesh, MeshBuffers>();

        private readonly Dictionary<int, D3D9Texture> textures =
            new Dictionary<int, D3D9Texture>();

        private readonly List<DrawItem> opaque = new List<DrawItem>(256);
        private readonly List<DrawItem> transparent = new List<DrawItem>(64);

        private D3D9VertexShader objectVertexShader;
        private D3D9PixelShader unlitPixelShader;
        private D3D9PixelShader texturedPixelShader;
        private D3D9PixelShader envMapPixelShader;
        private D3D9PixelShader envMapFogPixelShader;

        private D3D9VertexShader particleVertexShader;
        private D3D9PixelShader particlePixelShader;
        private D3D9Texture particleTexture;
        private VertexPositionColorTexture[] particleVertices = new VertexPositionColorTexture[0];

        private bool shadersReady;
        private bool disposed;

        internal SceneRenderer(D3D9Device device)
        {
            if (device == null) throw new ArgumentNullException("device");
            this.device = device;
        }

        /// <summary>True when the shader path is usable; false means the fixed-function fallback.</summary>
        internal bool ShadersReady
        {
            get { return shadersReady; }
        }

        internal int LastOpaqueCount { get; private set; }
        internal int LastTransparentCount { get; private set; }

        // ---------------------------------------------------------------- resources

        /// <summary>
        /// Compiles the engine's shaders. Called once, after the device exists.
        ///
        /// A failure here is not fatal: the renderer falls back to fixed-function
        /// drawing, which renders untextured geometry correctly and textured
        /// geometry with modulation. The subsystem table records why.
        /// </summary>
        internal void InitializeShaders()
        {
            if (shadersReady) return;

            var ok = FaultGuard.Guard("renderer", "shaders", delegate
            {
                objectVertexShader = CompileVertex("posnormaltex");
                unlitPixelShader = CompilePixel("color");
                texturedPixelShader = CompilePixel("texturecolor");
                envMapPixelShader = CompilePixel("texturecolorenvmap");
                envMapFogPixelShader = CompilePixel("texturecolorenvmapfog");
            });

            shadersReady = ok;

            if (ok)
            {
                FaultGuard.MarkRunning("renderer");
                EngineLog.Info("renderer", "Shader path ready.");
            }
            else
            {
                FaultGuard.MarkDegraded("renderer", "shader compilation failed; using fixed function");
                EngineLog.Warn("renderer", "Falling back to fixed-function rendering.");
            }
        }

        private D3D9VertexShader CompileVertex(string name)
        {
            string source;
            string entry;

            if (!ShaderLibrary.TryGetVertex(name, out source, out entry))
            {
                throw new InvalidOperationException("No vertex shader named '" + name + "'.");
            }

            return device.CompileVertexShader(name, source, entry);
        }

        private D3D9PixelShader CompilePixel(string name)
        {
            string source;
            string entry;

            if (!ShaderLibrary.TryGetPixel(name, out source, out entry))
            {
                throw new InvalidOperationException("No pixel shader named '" + name + "'.");
            }

            return device.CompilePixelShader(name, source, entry);
        }

        /// <summary>
        /// Uploads a texture from the project's pixel data.
        ///
        /// The project stores ARGB words with the top mip only; mip levels are
        /// generated here by box filtering, because a managed D3D9 texture without
        /// mips aliases badly the moment anything is smaller than a screen pixel -
        /// which, in a demo full of receding tunnel walls, is always.
        /// </summary>
        internal D3D9Texture UploadTexture(int key, int width, int height, uint[] argb)
        {
            if (argb == null) throw new ArgumentNullException("argb");
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException("width");
            if (argb.Length < width * height) throw new ArgumentException("Pixel data is too short.", "argb");

            D3D9Texture existing;
            if (textures.TryGetValue(key, out existing) && !existing.IsDisposed) return existing;

            var levels = CountMipLevels(width, height);
            var texture = device.CreateTexture(width, height, levels, false);

            try
            {
                var levelWidth = width;
                var levelHeight = height;
                var current = argb;

                for (var level = 0; level < levels; level++)
                {
                    var bgra = ToBgra(current, levelWidth, levelHeight);
                    texture.UploadBgra(level, bgra);

                    if (level + 1 >= levels) break;

                    current = Downsample(current, levelWidth, levelHeight);
                    levelWidth = Math.Max(1, levelWidth / 2);
                    levelHeight = Math.Max(1, levelHeight / 2);
                }
            }
            catch
            {
                texture.Dispose();
                throw;
            }

            if (existing != null) existing.Dispose();
            textures[key] = texture;
            return texture;
        }

        private static int CountMipLevels(int width, int height)
        {
            var levels = 1;

            while (width > 1 || height > 1)
            {
                width = Math.Max(1, width / 2);
                height = Math.Max(1, height / 2);
                levels++;
            }

            return levels;
        }

        private static byte[] ToBgra(uint[] argb, int width, int height)
        {
            var bytes = new byte[width * height * 4];

            for (var i = 0; i < width * height; i++)
            {
                var colour = ColorRgba.FromArgb(argb[i]);
                bytes[i * 4 + 0] = colour.B;
                bytes[i * 4 + 1] = colour.G;
                bytes[i * 4 + 2] = colour.R;
                bytes[i * 4 + 3] = colour.A;
            }

            return bytes;
        }

        /// <summary>Halves an image by averaging each 2x2 block.</summary>
        private static uint[] Downsample(uint[] source, int width, int height)
        {
            var halfWidth = Math.Max(1, width / 2);
            var halfHeight = Math.Max(1, height / 2);
            var result = new uint[halfWidth * halfHeight];

            for (var y = 0; y < halfHeight; y++)
            {
                for (var x = 0; x < halfWidth; x++)
                {
                    var x0 = Math.Min(width - 1, x * 2);
                    var y0 = Math.Min(height - 1, y * 2);
                    var x1 = Math.Min(width - 1, x0 + 1);
                    var y1 = Math.Min(height - 1, y0 + 1);

                    var a = ColorRgba.FromArgb(source[y0 * width + x0]);
                    var b = ColorRgba.FromArgb(source[y0 * width + x1]);
                    var c = ColorRgba.FromArgb(source[y1 * width + x0]);
                    var d = ColorRgba.FromArgb(source[y1 * width + x1]);

                    var averaged = new ColorRgba(
                        (byte)((a.R + b.R + c.R + d.R) / 4),
                        (byte)((a.G + b.G + c.G + d.G) / 4),
                        (byte)((a.B + b.B + c.B + d.B) / 4),
                        (byte)((a.A + b.A + c.A + d.A) / 4));

                    result[y * halfWidth + x] = averaged.ToArgb();
                }
            }

            return result;
        }

        /// <summary>
        /// Copies an array of blittable structs into a byte array.
        ///
        /// Buffer.BlockCopy looks like the obvious choice and is wrong: it only
        /// accepts arrays of primitives, and throws for an array of structs. The
        /// exception then disappears into the fault guard and the mesh silently
        /// never draws - which is exactly the failure this engine's self-test was
        /// written to catch.
        /// </summary>
        internal static byte[] StructsToBytes<T>(T[] values) where T : struct
        {
            if (values == null) throw new ArgumentNullException("values");
            if (values.Length == 0) return new byte[0];

            var size = Marshal.SizeOf(typeof(T));
            var bytes = new byte[values.Length * size];

            var handle = GCHandle.Alloc(values, GCHandleType.Pinned);

            try
            {
                Marshal.Copy(handle.AddrOfPinnedObject(), bytes, 0, bytes.Length);
            }
            finally
            {
                handle.Free();
            }

            return bytes;
        }

        private MeshBuffers GetMeshBuffers(Mesh mesh)
        {
            MeshBuffers buffers;

            if (meshes.TryGetValue(mesh, out buffers)) return buffers;

            var vertexCount = mesh.Vertices.Length;
            var indexCount = mesh.Indices.Length;

            var vertices = new VertexPositionNormalTexture[vertexCount];

            for (var i = 0; i < vertexCount; i++)
            {
                var source = mesh.Vertices[i];
                var target = new VertexPositionNormalTexture(source.Position.X, source.Position.Y,
                    source.Position.Z, source.TextureCoordinate.X, source.TextureCoordinate.Y);

                target.NX = source.Normal.X;
                target.NY = source.Normal.Y;
                target.NZ = source.Normal.Z;

                vertices[i] = target;
            }

            var vertexBytes = StructsToBytes(vertices);

            var vertexBuffer = device.CreateVertexBuffer(vertexBytes.Length,
                VertexPositionNormalTexture.Fvf, false);
            vertexBuffer.Upload(vertexBytes, 0);

            // Sixteen-bit indices whenever they fit: half the bandwidth, and every
            // card from this era is faster with them.
            var thirtyTwoBit = vertexCount > 65535;
            D3D9IndexBuffer indexBuffer;
            int triangleCount;

            if (thirtyTwoBit)
            {
                var indices = new uint[indexCount];

                for (var i = 0; i < indexCount; i++)
                {
                    indices[i] = (uint)mesh.Indices[i];
                }

                indexBuffer = device.CreateIndexBuffer(indexCount * 4, true);
                indexBuffer.Upload(indices);
                triangleCount = indexCount / 3;
            }
            else
            {
                var indices = new ushort[indexCount];

                for (var i = 0; i < indexCount; i++)
                {
                    indices[i] = (ushort)mesh.Indices[i];
                }

                indexBuffer = device.CreateIndexBuffer(indexCount * 2, false);
                indexBuffer.Upload(indices);
                triangleCount = indexCount / 3;
            }

            buffers = new MeshBuffers(vertexBuffer, indexBuffer, vertexCount, triangleCount);
            meshes[mesh] = buffers;

            return buffers;
        }

        // ---------------------------------------------------------------- frame

        /// <summary>
        /// Draws one frame with the scene's own camera.
        ///
        /// The caller is responsible for BeginScene/EndScene and for the final
        /// Present; the renderer only issues the geometry.
        /// </summary>
        internal void Render(Scene scene, float time)
        {
            if (disposed || scene == null) return;

            var camera = scene.Camera ?? new Camera();
            var aspect = device.Height > 0 ? (float)device.Width / device.Height : 1f;

            device.SetViewport(0, 0, device.Width, device.Height);

            RenderInternal(scene, camera, aspect);
        }

        /// <summary>
        /// Draws one frame through a caller-supplied camera and aspect ratio.
        ///
        /// The timeline uses this: a 3D event renders into its own rectangle, so
        /// the aspect ratio comes from the rectangle rather than the window, and
        /// the camera may be animated by the event.
        /// </summary>
        internal void RenderWithCamera(Scene scene, Camera camera, float aspect)
        {
            if (disposed || scene == null) return;

            RenderInternal(scene, camera ?? new Camera(), aspect);
        }

        private void RenderInternal(Scene scene, Camera camera, float aspect)
        {
            opaque.Clear();
            transparent.Clear();

            InitializeShaders();

            PrepareCamera(camera, aspect);
            PrepareEnvironment(scene);

            scene.UpdateTransforms();

            for (var i = 0; i < scene.RootNodes.Count; i++)
            {
                Collect(scene, scene.Find(scene.RootNodes[i]));
            }

            LastOpaqueCount = opaque.Count;
            LastTransparentCount = transparent.Count;

            DrawPass(opaque);

            if (transparent.Count > 0)
            {
                SortBackToFront(transparent, camera.Position);
                DrawPass(transparent);
            }

            DrawParticles(scene, camera);
        }

        private void PrepareCamera(Camera camera, float aspect)
        {
            var view = camera.CreateView();
            var projection = camera.CreateProjection(aspect);

            // Two paths, and both are needed.
            //
            // With a vertex shader bound, Direct3D 9 ignores SetTransform entirely:
            // the shader reads its own constant registers. With fixed function (the
            // fallback for a machine with no ps_2_0) the shader is unbound and the
            // transform state is what the hardware uses.
            //
            // Setting both costs four register writes per frame and saves the
            // renderer from branching on which path is active.
            device.SetTransform(D3D9.Transform.View, ref view);
            device.SetTransform(D3D9.Transform.Projection, ref projection);

            device.SetVertexShaderConstant(D3D9.Registers.View, ref view);
            device.SetVertexShaderConstant(D3D9.Registers.Projection, ref projection);

            // The viewport is deliberately NOT set here. The caller owns it: a 3D
            // event renders into its own rectangle of the project's buffer, and the
            // project's buffer is a different size from the window. Setting the
            // viewport to the window size here made every 3D event draw into the
            // top-left corner of the frame.
        }

        private void PrepareEnvironment(Scene scene)
        {
            device.SetRenderState(D3D9.RenderState.Lighting, 0u);
            device.SetRenderState(D3D9.RenderState.CullMode, (uint)D3D9.Cull.CounterClockwise);
            device.SetRenderState(D3D9.RenderState.ZFunc, (uint)D3D9.Compare.LessEqual);
            device.SetRenderState(D3D9.RenderState.AlphaFunc, (uint)D3D9.Compare.Greater);
            device.SetRenderState(D3D9.RenderState.AlphaRef, 0u);
            device.SetRenderState(D3D9.RenderState.SpecularEnable, 0u);
            device.SetRenderState(D3D9.RenderState.ScissorTestEnable, 0u);

            device.SetSamplerState(0, D3D9.SamplerState.AddressU, (uint)D3D9.TextureAddress.Wrap);
            device.SetSamplerState(0, D3D9.SamplerState.AddressV, (uint)D3D9.TextureAddress.Wrap);
            device.SetSamplerState(0, D3D9.SamplerState.MagFilter, (uint)D3D9.TextureFilter.Linear);
            device.SetSamplerState(0, D3D9.SamplerState.MinFilter, (uint)D3D9.TextureFilter.Linear);
            device.SetSamplerState(0, D3D9.SamplerState.MipFilter, (uint)D3D9.TextureFilter.Linear);

            if (shadersReady)
            {
                device.SetPixelShaderConstant(D3D9.Registers.PixelParams1,
                    new Vector4(scene.Fog.Colour.R / 255f, scene.Fog.Colour.G / 255f,
                        scene.Fog.Colour.B / 255f, scene.Fog.Colour.A / 255f));
            }
            else
            {
                // Fixed-function fog, as a fallback for machines with no ps_2_0.
                device.SetRenderState(D3D9.RenderState.FogEnable, scene.Fog.Enabled);
                device.SetRenderState(D3D9.RenderState.FogColor, scene.Fog.Colour.ToD3DColor());

                unsafe
                {
                    // FogStart/FogEnd are floats written through the same state
                    // slot; D3D9 takes them as a bit pattern in the DWORD.
                    var start = scene.Fog.Start;
                    var end = scene.Fog.End;

                    device.SetRenderState(D3D9.RenderState.FogStart, *(uint*)&start);
                    device.SetRenderState(D3D9.RenderState.FogEnd, *(uint*)&end);
                }
            }
        }

        private void Collect(Scene scene, SceneNode node)
        {
            if (node == null || !node.Visible) return;

            if (node.Mesh != null && !node.Mesh.IsEmpty)
            {
                var material = node.Material ?? new Material();
                var item = new DrawItem(node, node.Mesh, material);

                if (material.Transparent)
                {
                    transparent.Add(item);
                }
                else
                {
                    opaque.Add(item);
                }
            }

            for (var i = 0; i < node.Children.Count; i++)
            {
                Collect(scene, scene.Find(node.Children[i]));
            }
        }

        // ---------------------------------------------------------------- particles

        /// <summary>
        /// Draws every live particle as a camera-facing quad.
        ///
        /// Billboarded on the CPU rather than with a point sprite or a geometry
        /// shader: point sprites have a driver-dependent maximum size, and
        /// geometry shaders do not exist on the hardware this engine targets.
        ///
        /// Particles are blended and depth-tested but do not write depth, which is
        /// the only way overlapping sparks look like light rather than like
        /// cardboard cut-outs.
        /// </summary>
        private void DrawParticles(Scene scene, Camera camera)
        {
            if (!shadersReady || !scene.HasParticles) return;

            if (particleVertexShader == null)
            {
                string source;
                string entry;

                ShaderLibrary.TryGetVertex("postexcolor", out source, out entry);
                particleVertexShader = device.CompileVertexShader("particle.vs", source, entry);

                ShaderLibrary.TryGetPixel("texturediffuse", out source, out entry);
                particlePixelShader = device.CompilePixelShader("particle.ps", source, entry);

                particleTexture = CreateParticleTexture();
            }

            var right = Vector3.Cross(camera.Forward, camera.Up);
            right.Normalize();

            var up = Vector3.Cross(right, camera.Forward);
            up.Normalize();

            device.SetVertexShader(particleVertexShader);
            device.SetPixelShader(particlePixelShader);
            device.SetTexture(0, particleTexture);
            device.SetTexture(1, null);
            device.SetFvf(VertexPositionColorTexture.Fvf);

            device.SetRenderState(D3D9.RenderState.ZWriteEnable, 0u);
            device.SetRenderState(D3D9.RenderState.ZEnable, 1u);
            device.SetRenderState(D3D9.RenderState.AlphaBlendEnable, 1u);
            device.SetRenderState(D3D9.RenderState.SrcBlend, (uint)D3D9.Blend.SourceAlpha);
            device.SetRenderState(D3D9.RenderState.DestBlend, (uint)D3D9.Blend.One);
            device.SetRenderState(D3D9.RenderState.CullMode, (uint)D3D9.Cull.None);

            var identity = Matrix.Identity;
            device.SetVertexShaderConstant(D3D9.Registers.World, ref identity);

            for (var i = 0; i < scene.Nodes.Count; i++)
            {
                var emitter = scene.Nodes[i].Emitter;

                if (emitter == null) continue;

                var particles = emitter.Particles;

                if (particles.Length == 0) continue;

                if (particleVertices.Length < particles.Length * 6)
                {
                    particleVertices = new VertexPositionColorTexture[particles.Length * 6];
                }

                var count = 0;

                for (var p = 0; p < particles.Length; p++)
                {
                    if (!particles[p].Alive) continue;

                    ColorRgba colour;
                    float size;
                    emitter.Sample(ref particles[p], out colour, out size);

                    if (size <= 0.0001f || colour.A == 0) continue;

                    BuildQuad(particleVertices, count * 6, particles[p].Position,
                        right, up, size, colour, particles[p].Rotation);

                    count++;
                }

                if (count == 0) continue;

                device.DrawPrimitiveUp(D3D9.PrimitiveType.TriangleList, (uint)(count * 2),
                    particleVertices, VertexPositionColorTexture.SizeInBytes);
            }

            device.SetRenderState(D3D9.RenderState.ZWriteEnable, 1u);
            device.SetRenderState(D3D9.RenderState.AlphaBlendEnable, 0u);
        }

        private static void BuildQuad(VertexPositionColorTexture[] target, int offset, Vector3 centre,
            Vector3 right, Vector3 up, float size, ColorRgba colour, float rotationDegrees)
        {
            var rotation = MathHelper.ToRadians(rotationDegrees);
            var cos = (float)Math.Cos(rotation);
            var sin = (float)Math.Sin(rotation);

            var axisX = (right * cos + up * sin) * size;
            var axisY = (up * cos - right * sin) * size;

            var packed = colour.ToD3DColor();

            var topLeft = centre - axisX + axisY;
            var topRight = centre + axisX + axisY;
            var bottomRight = centre + axisX - axisY;
            var bottomLeft = centre - axisX - axisY;

            SetVertex(ref target[offset + 0], topLeft, packed, 0f, 0f);
            SetVertex(ref target[offset + 1], topRight, packed, 1f, 0f);
            SetVertex(ref target[offset + 2], bottomLeft, packed, 0f, 1f);

            SetVertex(ref target[offset + 3], topRight, packed, 1f, 0f);
            SetVertex(ref target[offset + 4], bottomRight, packed, 1f, 1f);
            SetVertex(ref target[offset + 5], bottomLeft, packed, 0f, 1f);
        }

        private static void SetVertex(ref VertexPositionColorTexture vertex, Vector3 position,
            uint colour, float u, float v)
        {
            vertex.X = position.X;
            vertex.Y = position.Y;
            vertex.Z = position.Z;
            vertex.Color = colour;
            vertex.U = u;
            vertex.V = v;
        }

        /// <summary>
        /// A soft radial blob, generated rather than shipped as a file.
        ///
        /// White with an alpha falloff, so a particle's colour comes from its
        /// vertex colour and the texture only shapes the edge.
        /// </summary>
        private D3D9Texture CreateParticleTexture()
        {
            const int size = 32;

            var pixels = new uint[size * size];
            var centre = (size - 1) * 0.5f;

            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var dx = (x - centre) / centre;
                    var dy = (y - centre) / centre;
                    var distance = (float)Math.Sqrt(dx * dx + dy * dy);

                    var alpha = distance >= 1f ? 0f : 1f - distance;
                    alpha = alpha * alpha;

                    pixels[y * size + x] = new ColorRgba(255, 255, 255, (byte)(alpha * 255f)).ToArgb();
                }
            }

            return UploadTexture(-9999, size, size, pixels);
        }

        private void DrawPass(List<DrawItem> items)
        {
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];

                // Guarded per item so one bad mesh cannot cost the whole frame, but
                // the failure is logged rather than swallowed: a mesh that silently
                // does not draw is the hardest kind of rendering bug to find.
                try
                {
                    DrawItemNow(item);
                }
                catch (Exception error)
                {
                    EngineLog.Error("renderer", "Drawing '" + item.Node.Name + "' failed: " + error.Message);
                    FaultGuard.Record("renderer", "draw:" + item.Node.Name, error);
                }
            }
        }

        private void DrawItemNow(DrawItem item)
        {
            var buffers = GetMeshBuffers(item.Mesh);

            var world = item.Node.World;

            EngineLog.Verbose("renderer", "draw '" + item.Node.Name + "' at (" +
                                          world.M41.ToString("0.00") + ", " +
                                          world.M42.ToString("0.00") + ", " +
                                          world.M43.ToString("0.00") + ") verts " +
                                          buffers.VertexCount + " tris " + buffers.TriangleCount);

            device.SetTransform(D3D9.Transform.World, ref world);
            device.SetVertexShaderConstant(D3D9.Registers.World, ref world);

            ApplyMaterial(item.Material);

            device.SetStreamSource(0, buffers.Vertices, VertexPositionNormalTexture.SizeInBytes);
            device.SetIndices(buffers.Indices);

            device.DrawIndexedPrimitive(D3D9.PrimitiveType.TriangleList, 0, 0,
                (uint)buffers.VertexCount, 0, (uint)buffers.TriangleCount);
        }

        private void ApplyMaterial(Material material)
        {
            // Depth.
            device.SetRenderState(D3D9.RenderState.ZEnable, material.DepthTest);
            device.SetRenderState(D3D9.RenderState.ZWriteEnable, material.DepthWrite);

            // Blending.
            var blended = material.SourceBlend != BlendMode.One ||
                          material.DestinationBlend != BlendMode.Zero;

            device.SetRenderState(D3D9.RenderState.AlphaBlendEnable, blended);
            device.SetRenderState(D3D9.RenderState.SrcBlend, ToD3DBlend(material.SourceBlend));
            device.SetRenderState(D3D9.RenderState.DestBlend, ToD3DBlend(material.DestinationBlend));

            // Culling. A two-sided material draws both faces, which is the only
            // way a single plane is visible from behind.
            device.SetRenderState(D3D9.RenderState.CullMode,
                material.TwoSided ? (uint)D3D9.Cull.None : (uint)D3D9.Cull.CounterClockwise);

            device.SetRenderState(D3D9.RenderState.FillMode,
                material.Wireframe ? (uint)D3D9.Fill.Solid : (uint)D3D9.Fill.Solid);

            if (material.Wireframe)
            {
                device.SetRenderState(D3D9.RenderState.FillMode, (uint)D3D9.Fill.Wireframe);
            }

            D3D9Texture texture = null;

            if (material.TextureIndex >= 0)
            {
                textures.TryGetValue(material.TextureIndex, out texture);
            }

            D3D9Texture environment = null;

            if (material.EnvironmentTextureIndex >= 0)
            {
                textures.TryGetValue(material.EnvironmentTextureIndex, out environment);
            }

            if (!shadersReady)
            {
                ApplyFixedFunction(material, texture);
                return;
            }

            device.SetFvf(VertexPositionNormalTexture.Fvf);
            device.SetVertexShader(objectVertexShader);

            device.SetTexture(0, texture);
            device.SetTexture(1, environment);

            if (environment != null)
            {
                device.SetPixelShader(envMapPixelShader);
            }
            else if (texture != null)
            {
                device.SetPixelShader(texturedPixelShader);
            }
            else
            {
                device.SetPixelShader(unlitPixelShader);
            }

            device.SetPixelShaderConstant(D3D9.Registers.PixelColor,
                new Vector4(material.Colour.R / 255f, material.Colour.G / 255f,
                    material.Colour.B / 255f, material.Colour.A / 255f));
        }

        private void ApplyFixedFunction(Material material, D3D9Texture texture)
        {
            device.SetFvf(VertexPositionNormalTexture.Fvf);
            device.SetVertexShader(null);
            device.SetPixelShader(null);

            device.SetTexture(0, texture);
            device.SetTexture(1, null);

            device.SetTextureStageState(0, D3D9.TextureStage.ColorOp,
                texture == null ? (uint)D3D9.TextureOp.SelectArg1 : (uint)D3D9.TextureOp.Modulate);
            device.SetTextureStageState(0, D3D9.TextureStage.ColorArg1,
                texture == null ? (uint)D3D9.TextureArgument.Diffuse : (uint)D3D9.TextureArgument.Texture);
            device.SetTextureStageState(0, D3D9.TextureStage.ColorArg2,
                (uint)D3D9.TextureArgument.Diffuse);
            device.SetTextureStageState(0, D3D9.TextureStage.AlphaOp,
                texture == null ? (uint)D3D9.TextureOp.SelectArg1 : (uint)D3D9.TextureOp.Modulate);
            device.SetTextureStageState(0, D3D9.TextureStage.AlphaArg1,
                texture == null ? (uint)D3D9.TextureArgument.Diffuse : (uint)D3D9.TextureArgument.Texture);
            device.SetTextureStageState(0, D3D9.TextureStage.AlphaArg2,
                (uint)D3D9.TextureArgument.Diffuse);
            device.SetTextureStageState(1, D3D9.TextureStage.ColorOp, (uint)D3D9.TextureOp.Disable);
        }

        private static uint ToD3DBlend(BlendMode mode)
        {
            switch (mode)
            {
                case BlendMode.Zero: return (uint)D3D9.Blend.Zero;
                case BlendMode.One: return (uint)D3D9.Blend.One;
                case BlendMode.SourceColour: return (uint)D3D9.Blend.SourceColor;
                case BlendMode.InverseSourceColour: return (uint)D3D9.Blend.InverseSourceColor;
                case BlendMode.DestinationAlpha: return (uint)D3D9.Blend.DestinationAlpha;
                case BlendMode.InverseDestinationAlpha: return (uint)D3D9.Blend.InverseDestinationAlpha;
                case BlendMode.DestinationColour: return (uint)D3D9.Blend.DestinationColor;
                case BlendMode.InverseDestinationColour: return (uint)D3D9.Blend.InverseDestinationColor;
                case BlendMode.SourceAlphaSaturation: return (uint)D3D9.Blend.SourceAlphaSaturation;
                default: return (uint)D3D9.Blend.SourceAlpha;
            }
        }

        /// <summary>
        /// Orders transparent geometry back to front.
        ///
        /// Insertion sort: the transparent list is small (tens of items in a
        /// demo, not thousands) and it is usually nearly sorted already, so the
        /// quadratic term never dominates - while avoiding a comparison delegate
        /// allocation per frame that List.Sort would need.
        /// </summary>
        private static void SortBackToFront(List<DrawItem> items, Vector3 cameraPosition)
        {
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                item.Depth = Vector3.DistanceSquared(
                    Vector3.Transform(item.Mesh.BoundsCentre, item.Node.World), cameraPosition);
                items[i] = item;
            }

            for (var i = 1; i < items.Count; i++)
            {
                var current = items[i];
                var j = i - 1;

                while (j >= 0 && items[j].Depth < current.Depth)
                {
                    items[j + 1] = items[j];
                    j--;
                }

                items[j + 1] = current;
            }
        }

        /// <summary>Drops the GPU mirror of a mesh, for the editor's live editing.</summary>
        internal void InvalidateMesh(Mesh mesh)
        {
            MeshBuffers buffers;

            if (!meshes.TryGetValue(mesh, out buffers)) return;

            buffers.Dispose();
            meshes.Remove(mesh);
        }

        internal void InvalidateTexture(int key)
        {
            D3D9Texture texture;

            if (!textures.TryGetValue(key, out texture)) return;

            texture.Dispose();
            textures.Remove(key);
        }

        /// <summary>
        /// Releases everything held in video memory.
        ///
        /// Called before a device reset: default-pool resources are destroyed by
        /// the loss, and keeping stale handles to them is how a reset turns into
        /// an access violation.
        /// </summary>
        internal void ReleaseDeviceResources()
        {
            foreach (var buffers in meshes.Values)
            {
                buffers.Dispose();
            }

            meshes.Clear();

            foreach (var texture in textures.Values)
            {
                texture.Dispose();
            }

            textures.Clear();

            if (objectVertexShader != null)
            {
                objectVertexShader.Dispose();
                objectVertexShader = null;
            }

            if (unlitPixelShader != null)
            {
                unlitPixelShader.Dispose();
                unlitPixelShader = null;
            }

            if (texturedPixelShader != null)
            {
                texturedPixelShader.Dispose();
                texturedPixelShader = null;
            }

            if (envMapPixelShader != null)
            {
                envMapPixelShader.Dispose();
                envMapPixelShader = null;
            }

            if (envMapFogPixelShader != null)
            {
                envMapFogPixelShader.Dispose();
                envMapFogPixelShader = null;
            }

            shadersReady = false;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            ReleaseDeviceResources();
        }

        // ---------------------------------------------------------------- types

        private struct MeshBuffers : IDisposable
        {
            internal readonly D3D9VertexBuffer Vertices;
            internal readonly D3D9IndexBuffer Indices;
            internal readonly int VertexCount;
            internal readonly int TriangleCount;

            internal MeshBuffers(D3D9VertexBuffer vertices, D3D9IndexBuffer indices,
                int vertexCount, int triangleCount)
            {
                Vertices = vertices;
                Indices = indices;
                VertexCount = vertexCount;
                TriangleCount = triangleCount;
            }

            public void Dispose()
            {
                if (Vertices != null) Vertices.Dispose();
                if (Indices != null) Indices.Dispose();
            }
        }

        private struct DrawItem
        {
            internal readonly SceneNode Node;
            internal readonly Mesh Mesh;
            internal readonly Material Material;
            internal float Depth;

            internal DrawItem(SceneNode node, Mesh mesh, Material material)
            {
                Node = node;
                Mesh = mesh;
                Material = material;
                Depth = 0f;
            }
        }
    }
}
