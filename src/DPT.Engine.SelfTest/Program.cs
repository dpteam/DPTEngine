//
// DPT.Engine - self-test.
// Copyright (C) 2026 DartPower Team. GPLv2.
//

using System;
using System.Collections.Generic;
using System.IO;

using DPT.Engine.Graphics;
using DPT.Engine.Mathematics;
using DPT.Engine.Rendering;
using DPT.Engine.Scenes;
using DPT.Engine.Shared;
using DPT.Engine.Shared.Diagnostics;
using DPT.Engine.Timeline;
using DPT.Engine.Win32;

namespace DPT.Engine.SelfTest
{
    /// <summary>
    /// Renders a real frame on the real device and checks the pixels.
    ///
    /// Unit tests prove the pieces work in isolation; this proves they work
    /// together, which is where an interop port usually fails. It runs a full
    /// frame - window, device, shaders, textures, scene graph, draw - and then
    /// reads the back buffer back and asserts on specific pixels.
    ///
    /// Runs headless: the window is never shown, so it is safe on a build server
    /// and does not steal focus.
    /// </summary>
    internal static class Program
    {
        private const int Width = 320;
        private const int Height = 240;

        private static int failures;

        /// <summary>
        /// Suppresses the Windows "this program has stopped working" dialog.
        ///
        /// Without this a native fault blocks the process on a modal message box,
        /// so an automated run appears to hang instead of reporting the fault. The
        /// engine's own entry point sets the same mode: a full-screen demo that
        /// crashes on somebody else's machine should leave a crash report and exit,
        /// not wait for a click that never comes.
        /// </summary>
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern uint SetErrorMode(uint mode);

        private const uint SemFailCriticalErrors = 0x0001;
        private const uint SemNoGpfaultErrorBox = 0x0002;
        private const uint SemNoAlignmentFaultExcept = 0x0004;
        private const uint SemNoOpenFileErrorBox = 0x8000;

        private static int Main(string[] args)
        {
            SetErrorMode(SemFailCriticalErrors | SemNoGpfaultErrorBox |
                         SemNoAlignmentFaultExcept | SemNoOpenFileErrorBox);

            Console.WriteLine("DPT.Engine self-test");
            Console.WriteLine();

            // Logging first, so a failure below is explained rather than silent.
            var logPath = Path.Combine(PortablePaths.DataDirectory, "selftest.log");
            EngineLog.Initialize(logPath, LogLevel.Verbose, 1024 * 1024, 1);
            EngineLog.Info("selftest", "Starting.");

            Run("splines", TestSplines);
            Run("json", TestJson);
            Run("mesh", TestMesh);
            Run("scene graph", TestSceneGraph);
            Run("shader path", TestShaderPath);
            Run("render", TestRender);
            Run("animation", TestAnimation);
            Run("readback", TestReadback);

            Console.WriteLine();

            if (failures == 0)
            {
                Console.WriteLine("self-test passed");
                EngineLog.Info("selftest", "Passed.");
                return 0;
            }

            Console.WriteLine("self-test FAILED: " + failures + " check(s)");
            EngineLog.Error("selftest", failures + " check(s) failed.");
            return 1;
        }

        private static void Run(string name, Action test)
        {            try
            {
                test();
                Console.WriteLine("  PASS  " + name);
            }
            catch (Exception error)
            {
                failures++;
                Console.WriteLine("  FAIL  " + name);
                Console.WriteLine("        " + error.GetType().Name + ": " + error.Message);
                EngineLog.Exception("selftest." + name, error);
            }
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void CheckNear(float expected, float actual, float tolerance, string message)
        {
            if (Math.Abs(expected - actual) > tolerance)
            {
                throw new InvalidOperationException(message + ": expected " + expected +
                                                    ", got " + actual);
            }
        }

        // ---------------------------------------------------------------- splines

        private static void TestSplines()
        {
            var ramp = Spline.Ramp(0f, 10f, 2f);

            CheckNear(0f, ramp.Sample(0f), 1e-4f, "ramp at start");
            CheckNear(5f, ramp.Sample(1f), 1e-4f, "ramp at middle");
            CheckNear(10f, ramp.Sample(2f), 1e-4f, "ramp at end");

            // Outside the key range the curve holds, it does not extrapolate.
            CheckNear(0f, ramp.Sample(-5f), 1e-4f, "clamped before the first key");
            CheckNear(10f, ramp.Sample(50f), 1e-4f, "clamped after the last key");

            var smooth = new Spline();
            smooth.Interpolation = Interpolation.Hermite;
            smooth.SetKeys(new[]
            {
                new Key { Time = 0f, Value = 0f },
                new Key { Time = 1f, Value = 1f },
                new Key { Time = 2f, Value = 0f }
            });

            // A Hermite through these keys peaks at the middle key and stays
            // within a sane band - no NaN, no runaway overshoot.
            var peak = smooth.Sample(1f);
            CheckNear(1f, peak, 0.05f, "hermite passes through its key");

            for (var t = 0f; t <= 2f; t += 0.05f)
            {
                var value = smooth.Sample(t);
                Check(!float.IsNaN(value), "hermite sample at " + t + " is not NaN");
                Check(value > -1f && value < 2f, "hermite sample at " + t + " stays bounded");
            }

            // Degenerate curves must not throw or produce NaN.
            var empty = new Spline();
            CheckNear(0f, empty.Sample(1f), 1e-6f, "an empty spline samples zero");

            var single = Spline.Constant(7f);
            CheckNear(7f, single.Sample(123f), 1e-6f, "a single-key spline holds its value");
        }

        // ---------------------------------------------------------------- json

        private static void TestJson()
        {
            var root = JsonValue.NewObject();
            root.Set("name", "self-test");
            root.Set("frames", 300);
            root.Set("loop", true);

            var array = JsonValue.NewArray();
            array.Add(JsonValue.NewObject().Set("x", 1).Set("y", 2));
            root.Set("objects", array);

            var text = root.ToJson(true);
            var parsed = JsonValue.Parse(text);

            Check(parsed["name"].AsString() == "self-test", "string round-trips");
            Check(parsed["frames"].AsInt(0) == 300, "number round-trips");
            Check(parsed["loop"].AsBool(false), "bool round-trips");
            Check(parsed["objects"][0]["y"].AsInt(0) == 2, "nested value round-trips");

            // Missing members read as defaults rather than throwing.
            Check(parsed["absent"].AsInt(-1) == -1, "missing member falls back");
        }

        // ---------------------------------------------------------------- mesh

        private static void TestMesh()
        {
            var box = Mesh.CreateBox(2f);

            Check(box.Vertices.Length == 8, "box has 8 vertices");
            Check(box.TriangleCount == 12, "box has 12 triangles");

            // A box is one unit from the origin on every axis.
            CheckNear(-1f, box.BoundsMin.X, 1e-4f, "box bounds min");
            CheckNear(1f, box.BoundsMax.Z, 1e-4f, "box bounds max");

            // Every normal must be unit length, or lighting is wrong everywhere.
            for (var i = 0; i < box.Vertices.Length; i++)
            {
                CheckNear(1f, box.Vertices[i].Normal.Length, 1e-3f, "vertex " + i + " normal is unit length");
            }

            var sphere = Mesh.CreateSphere(1f, 16, 8);
            Check(sphere.Vertices.Length > 0, "sphere has vertices");
            Check(sphere.TriangleCount > 0, "sphere has triangles");

            for (var i = 0; i < sphere.Vertices.Length; i++)
            {
                CheckNear(1f, sphere.Vertices[i].Position.Length, 1e-3f, "sphere vertex " + i + " is on the radius");
            }

            var plane = Mesh.CreatePlane(10f, 10f, 4);
            Check(plane.TriangleCount == 32, "a 4x4 plane has 32 triangles");
        }

        // ---------------------------------------------------------------- scene

        private static void TestSceneGraph()
        {
            var scene = new Scene();

            var parent = new SceneNode();
            parent.Id = 1;
            parent.Position = new Vector3(10f, 0f, 0f);

            var child = new SceneNode();
            child.Id = 2;
            child.ParentId = 1;
            child.Position = new Vector3(0f, 5f, 0f);

            scene.Nodes.Add(parent);
            scene.Nodes.Add(child);
            scene.RebuildHierarchy();

            Check(scene.RootNodes.Count == 1, "one root");
            Check(parent.Children.Count == 1, "the parent has one child");

            scene.UpdateTransforms();

            // The child's world position is its parent's plus its own.
            CheckNear(10f, child.World.M41, 1e-4f, "child inherits the parent's X");
            CheckNear(5f, child.World.M42, 1e-4f, "child adds its own Y");

            // A dangling parent must not crash; the node becomes a root.
            var orphan = new SceneNode();
            orphan.Id = 3;
            orphan.ParentId = 999;
            scene.Nodes.Add(orphan);
            scene.RebuildHierarchy();

            Check(scene.RootNodes.Count == 2, "an orphan becomes a root");
            Check(orphan.ParentId == -1, "the orphan's parent link is cleared");
        }

        // ---------------------------------------------------------------- shader path

        /// <summary>
        /// Draws one triangle through the same vertex and pixel shader the
        /// renderer uses, with identity matrices, and checks the pixel.
        ///
        /// This isolates the shader path from the camera and the scene graph: if
        /// this fails, the problem is the shader, the constant registers or the
        /// vertex layout. If it passes, the problem is upstream of it.
        /// </summary>
        private static void TestShaderPath()
        {
            using (var window = new Win32Window())
            {
                window.CreateHidden("DPT.Engine shader probe", Width, Height);

                using (var device = new D3D9Device())
                {
                    device.Create(window.Handle, Width, Height, false, false);

                    string vertexSource;
                    string vertexEntry;
                    string pixelSource;
                    string pixelEntry;

                    ShaderLibrary.TryGetVertex("posnormaltex", out vertexSource, out vertexEntry);
                    ShaderLibrary.TryGetPixel("color", out pixelSource, out pixelEntry);

                    var vertexShader = device.CompileVertexShader("probe.vs", vertexSource, vertexEntry);
                    var pixelShader = device.CompilePixelShader("probe.ps", pixelSource, pixelEntry);

                    // A triangle covering the whole clip volume.
                    var vertices = new VertexPositionNormalTexture[3];

                    vertices[0] = new VertexPositionNormalTexture(-1f, -1f, 0.5f, 0f, 0f);
                    vertices[1] = new VertexPositionNormalTexture(3f, -1f, 0.5f, 1f, 0f);
                    vertices[2] = new VertexPositionNormalTexture(-1f, 3f, 0.5f, 0f, 1f);

                    for (var i = 0; i < 3; i++)
                    {
                        vertices[i].NX = 0f;
                        vertices[i].NY = 0f;
                        vertices[i].NZ = -1f;
                    }

                    var vertexBytes = SceneRenderer.StructsToBytes(vertices);

                    var vertexBuffer = device.CreateVertexBuffer(vertexBytes.Length,
                        VertexPositionNormalTexture.Fvf, false);
                    vertexBuffer.Upload(vertexBytes, 0);

                    var indices = new ushort[] { 0, 1, 2 };
                    var indexBuffer = device.CreateIndexBuffer(6, false);
                    indexBuffer.Upload(indices);

                    var identity = Matrix.Identity;

                    device.BeginScene();
                    device.Clear(0xFF000000, true, true, false);

                    device.SetRenderState(D3D9.RenderState.Lighting, 0u);
                    device.SetRenderState(D3D9.RenderState.CullMode, (uint)D3D9.Cull.None);
                    device.SetRenderState(D3D9.RenderState.ZEnable, 0u);
                    device.SetRenderState(D3D9.RenderState.AlphaBlendEnable, 0u);

                    device.SetFvf(VertexPositionNormalTexture.Fvf);
                    device.SetVertexShader(vertexShader);
                    device.SetPixelShader(pixelShader);

                    device.SetVertexShaderConstant(D3D9.Registers.World, ref identity);
                    device.SetVertexShaderConstant(D3D9.Registers.View, ref identity);
                    device.SetVertexShaderConstant(D3D9.Registers.Projection, ref identity);

                    device.SetPixelShaderConstant(D3D9.Registers.PixelColor,
                        new Vector4(1f, 0f, 1f, 1f));

                    device.SetStreamSource(0, vertexBuffer, VertexPositionNormalTexture.SizeInBytes);
                    device.SetIndices(indexBuffer);

                    device.DrawIndexedPrimitive(D3D9.PrimitiveType.TriangleList, 0, 0, 3, 0, 1);

                    device.EndScene();

                    var pixels = device.ReadBackBuffer(Width, Height);
                    var centre = ReadPixel(pixels, Width / 2, Height / 2);

                    Console.WriteLine("        probe pixel: 0x" + centre.ToString("X8"));

                    Check(centre != 0xFF000000, "the shader path drew something");

                    var colour = ColorRgba.FromArgb(centre);
                    Check(colour.R > 200 && colour.B > 200, "the pixel is magenta, got " + colour.ToHex());

                    indexBuffer.Dispose();
                    vertexBuffer.Dispose();
                    pixelShader.Dispose();
                    vertexShader.Dispose();
                }
            }
        }

        // ---------------------------------------------------------------- render

        private static void TestRender()
        {
            using (var window = new Win32Window())
            {
                window.CreateHidden("DPT.Engine self-test", Width, Height);

                using (var device = new D3D9Device())
                {
                    device.Create(window.Handle, Width, Height, false, false);
                    Console.WriteLine("        " + device.Description);
                    Console.WriteLine("        vs " + device.Caps.VertexShaderMajor + ".x, ps " +
                                      device.Caps.PixelShaderMajor + ".x");

                    using (var renderer = new SceneRenderer(device))
                    {
                        renderer.InitializeShaders();
                        Check(renderer.ShadersReady, "shaders compiled");

                        var scene = BuildScene(renderer);
                        RenderAndCheck(device, renderer, scene);
                    }
                }
            }
        }

        /// <summary>
        /// A scene with a textured floor and a coloured cube: enough that a
        /// broken texture path, a broken transform, or a broken depth test each
        /// produce a visibly wrong pixel.
        /// </summary>
        private static Scene BuildScene(SceneRenderer renderer)
        {
            var scene = new Scene();
            scene.Camera.Position = new Vector3(0f, 2f, -6f);
            scene.Camera.Target = Vector3.Zero;
            scene.Camera.FieldOfView = 45f;
            scene.Ambient = ColorRgba.White;

            // A red checkerboard, so a texture that samples the wrong texel or
            // the wrong channel is immediately obvious.
            const int textureSize = 32;
            var pixels = new uint[textureSize * textureSize];

            for (var y = 0; y < textureSize; y++)
            {
                for (var x = 0; x < textureSize; x++)
                {
                    var dark = ((x / 8) + (y / 8)) % 2 == 0;
                    pixels[y * textureSize + x] = dark
                        ? new ColorRgba(200, 30, 30, 255).ToArgb()
                        : new ColorRgba(90, 10, 10, 255).ToArgb();
                }
            }

            renderer.UploadTexture(0, textureSize, textureSize, pixels);

            var floorMaterial = new Material();
            floorMaterial.TextureIndex = 0;
            floorMaterial.Shading = ShadingMode.Unlit;
            floorMaterial.SourceBlend = BlendMode.One;
            floorMaterial.DestinationBlend = BlendMode.Zero;

            var floor = new SceneNode();
            floor.Id = 1;
            floor.Name = "floor";
            floor.Mesh = Mesh.CreatePlane(20f, 20f, 1);
            floor.Material = floorMaterial;
            floor.Position = new Vector3(0f, -1f, 0f);
            scene.Nodes.Add(floor);

            var cubeMaterial = new Material();
            cubeMaterial.Colour = new ColorRgba(0, 200, 255, 255);
            cubeMaterial.Shading = ShadingMode.Unlit;
            cubeMaterial.SourceBlend = BlendMode.One;
            cubeMaterial.DestinationBlend = BlendMode.Zero;

            var cube = new SceneNode();
            cube.Id = 2;
            cube.Name = "cube";
            cube.Mesh = Mesh.CreateBox(2f);
            cube.Material = cubeMaterial;
            scene.Nodes.Add(cube);

            scene.RebuildHierarchy();
            return scene;
        }

        private static void RenderAndCheck(D3D9Device device, SceneRenderer renderer, Scene scene)
        {
            device.BeginScene();
            device.Clear(0xFF000000, true, true, false);

            renderer.Render(scene, 0f);

            device.EndScene();

            Check(renderer.LastOpaqueCount == 2, "both nodes were submitted, got " +
                                                 renderer.LastOpaqueCount);

            var pixels = device.ReadBackBuffer(Width, Height);

            var centre = ReadPixel(pixels, Width / 2, Height / 2);
            var bottom = ReadPixel(pixels, Width / 2, Height - 20);

            Console.WriteLine("        centre pixel: 0x" + centre.ToString("X8"));
            Console.WriteLine("        floor pixel : 0x" + bottom.ToString("X8"));

            // The cube sits in the middle of the frame. If the transform chain or
            // the vertex layout is wrong it will not be there at all.
            Check(centre != 0xFF000000, "the cube drew something in the centre");

            // The cube is cyan, and the clear colour is black: any red channel in
            // the centre means the wrong texture or the wrong material was bound.
            var centreColour = ColorRgba.FromArgb(centre);
            Check(centreColour.B > 100, "the cube is blue-ish, got B=" + centreColour.B);
            Check(centreColour.G > 100, "the cube is cyan-ish, got G=" + centreColour.G);

            // The floor is red and covers the bottom of the frame.
            var floorColour = ColorRgba.FromArgb(bottom);
            Check(floorColour.R > 50, "the floor is red-ish, got R=" + floorColour.R);
            Check(floorColour.B < 80, "the floor is not blue, got B=" + floorColour.B);
        }

        private static uint ReadPixel(byte[] bgra, int x, int y)
        {
            var offset = (y * Width + x) * 4;
            return BitConverter.ToUInt32(bgra, offset);
        }

        // ---------------------------------------------------------------- animation

        /// <summary>
        /// Scene-level animation: tracks address a node by id, and the scene poses
        /// itself into an animation.
        /// </summary>
        private static void TestAnimation()
        {
            var scene = new Scene();

            var node = new SceneNode();
            node.Id = 1;
            scene.Nodes.Add(node);
            scene.RebuildHierarchy();

            var animation = new SceneAnimation();
            animation.Id = 0;

            var move = new AnimationTrack();
            move.Property = "1.x";
            move.Curve = Spline.Ramp(0f, 10f, 1f);
            animation.Tracks.Add(move);

            scene.Animations.Add(animation);

            scene.ApplyAnimation(0, 0f);
            CheckNear(0f, node.Position.X, 1e-4f, "animation at start");

            scene.ApplyAnimation(0, 0.5f);
            CheckNear(5f, node.Position.X, 1e-4f, "animation at middle");

            scene.ApplyAnimation(0, 1f);
            CheckNear(10f, node.Position.X, 1e-4f, "animation at end");

            // Rotation tracks must stay normalised, or the object scales.
            var rotation = new SceneNode();
            rotation.Id = 2;
            scene.Nodes.Add(rotation);
            scene.RebuildHierarchy();

            var spin = new SceneAnimation();
            spin.Id = 1;

            var rx = new AnimationTrack();
            rx.Property = "2.rx";
            rx.Curve = Spline.Constant(0.5f);
            spin.Tracks.Add(rx);

            var ry = new AnimationTrack();
            ry.Property = "2.ry";
            ry.Curve = Spline.Constant(0.5f);
            spin.Tracks.Add(ry);

            scene.Animations.Add(spin);

            scene.ApplyAnimation(1, 0f);
            CheckNear(1f, rotation.Rotation.Length, 1e-3f, "rotation stays unit length");

            // A zeroed quaternion must fall back to identity, not NaN.
            var broken = new SceneNode();
            broken.Id = 3;
            broken.Rotation = new Quaternion(0f, 0f, 0f, 0f);
            scene.Nodes.Add(broken);
            scene.RebuildHierarchy();

            var zero = new SceneAnimation();
            zero.Id = 2;

            var rw = new AnimationTrack();
            rw.Property = "3.rw";
            rw.Curve = Spline.Constant(0f);
            zero.Tracks.Add(rw);

            scene.Animations.Add(zero);

            scene.ApplyAnimation(2, 0f);
            CheckNear(1f, broken.Rotation.W, 1e-3f, "a zero quaternion becomes identity");

            // A track that addresses a node which does not exist must be skipped,
            // not throw: a project can reference a deleted object.
            var dangling = new SceneAnimation();
            dangling.Id = 3;

            var orphan = new AnimationTrack();
            orphan.Property = "999.x";
            orphan.Curve = Spline.Ramp(0f, 1f, 1f);
            dangling.Tracks.Add(orphan);

            scene.Animations.Add(dangling);
            scene.ApplyAnimation(3, 0.5f);
        }
        // ---------------------------------------------------------------- readback

        /// <summary>
        /// Verifies the readback contract.
        ///
        /// Readback is one-shot per device by design (see D3D9Device.ReadSurfaceBounded):
        /// the second GetRenderTargetData on this driver family wedges inside the
        /// driver, so the engine returns no pixels rather than freezing. What the test
        /// checks is that the contract holds - the first read returns real pixels and
        /// the second returns immediately with an empty array - because a violation of
        /// that contract is a frozen engine.
        /// </summary>
        private static void TestReadback()
        {
            using (var window = new Win32Window())
            {
                window.CreateHidden("DPT.Engine readback", Width, Height);

                using (var device = new D3D9Device())
                {
                    device.Create(window.Handle, Width, Height, false, false);

                    using (var target = device.CreateRenderTarget(Width, Height))
                    {
                        device.BeginScene();
                        device.Clear(0xFF204080, true, true, false);
                        device.EndScene();

                        // Flush before copying. A back-buffer copy is not ordered
                        // against a preceding Clear in the same batch, so without
                        // this the copy can run before the clear and capture the
                        // previous frame - which is exactly what made this test read
                        // black.
                        device.Flush();

                        device.CopyBackBufferTo(target, 0, 0, Width, Height);

                        var first = device.ReadRenderTarget(target);
                        Check(first.Length > 0, "the first readback returns pixels");

                        var firstPixel = BitConverter.ToUInt32(first, 0);
                        var firstColour = ColorRgba.FromArgb(firstPixel);

                        Check(firstColour.R == 0x20 && firstColour.G == 0x40 && firstColour.B == 0x80,
                            "the first readback returns the cleared colour, got " + firstColour.ToHex());

                        // The second read must return immediately rather than hang.
                        var started = Environment.TickCount;
                        var second = device.ReadRenderTarget(target);
                        var elapsed = Environment.TickCount - started;

                        Check(second.Length == 0, "the second readback returns nothing");
                        Check(elapsed < 1000, "the second readback returns immediately, took " + elapsed + " ms");
                    }
                }
            }
        }
    }
}
