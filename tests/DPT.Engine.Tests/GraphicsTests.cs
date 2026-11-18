//
// DPT.Engine - Direct3D 9 interop tests.
// Copyright (C) 2026 DartPower Team. GPLv2.
//

using System;
using DPT.Engine.Graphics;
using DPT.Engine.Mathematics;
using DPT.Engine.Win32;

namespace DPT.Engine.Tests
{
    /// <summary>
    /// Tests the COM layer against a real device.
    ///
    /// These are the tests that earn their keep. A wrong vtable slot or a
    /// mis-sized struct compiles perfectly and then corrupts memory at runtime,
    /// usually as a crash somewhere unrelated. Every one of them creates a hidden
    /// window and a real device, draws something, and reads the pixels back.
    ///
    /// A machine with no Direct3D 9 driver at all - a bare VM, some CI
    /// containers - throws <see cref="GraphicsUnavailableException"/> and the
    /// runner reports the test as skipped rather than failed.
    /// </summary>
    internal sealed class GraphicsTests
    {
        private const int Width = 160;
        private const int Height = 120;

        /// <summary>Opens a hidden window and a device, and hands both to the body.</summary>
        private static void WithDevice(Action<Win32Window, D3D9Device> body)
        {
            using (var window = new Win32Window())
            {
                try
                {
                    window.CreateHidden("DPT.Engine test", Width, Height);
                }
                catch (Exception error)
                {
                    throw new GraphicsUnavailableException("no window: " + error.Message);
                }

                using (var device = new D3D9Device())
                {
                    try
                    {
                        device.Create(window.Handle, Width, Height, false, false);
                    }
                    catch (Exception error)
                    {
                        throw new GraphicsUnavailableException("no Direct3D 9 device: " + error.Message);
                    }

                    body(window, device);
                }
            }
        }

        [Test]
        [RequiresGraphics]
        internal void DeviceCreatesAndReportsItsMode()
        {
            WithDevice(delegate(Win32Window window, D3D9Device device)
            {
                Assert.NotNull(device.Description, "device description");
                Assert.Equal(Width, device.Width, "back buffer width");
                Assert.Equal(Height, device.Height, "back buffer height");
                Assert.False(device.IsLost, "a fresh device is not lost");

                Console.WriteLine("        device: " + device.Description);
                Console.WriteLine("        shader model: vs " + device.Caps.VertexShaderMajor +
                                  ".x, ps " + device.Caps.PixelShaderMajor + ".x");
            });
        }

        [Test]
        [RequiresGraphics]
        internal void ClearAndReadBackProducesTheClearColour()
        {
            WithDevice(delegate(Win32Window window, D3D9Device device)
            {
                // A distinctive colour, so a wrong channel order is obvious.
                const uint expected = 0xFF204080;

                device.BeginScene();
                device.Clear(expected, true, true, false);
                device.EndScene();

                var pixels = ReadBack(device);
                var first = BitConverter.ToUInt32(pixels, 0);

                // The readback is BGRA bytes, so the DWORD reads as 0xAARRGGBB
                // only after the byte order is accounted for.
                AssertRgb(expected, first, "cleared pixel");
            });
        }

        [Test]
        [RequiresGraphics]
        internal void ShaderCompilerProducesUsableBytecode()
        {
            WithDevice(delegate(Win32Window window, D3D9Device device)
            {
                string source;
                string entry;

                Assert.True(ShaderLibrary.TryGetVertex("poscolor", out source, out entry),
                    "the poscolor vertex shader exists");

                var shader = device.CompileVertexShader("test.poscolor", source, entry);
                Assert.NotNull(shader, "compiled vertex shader");
                shader.Dispose();

                Assert.True(ShaderLibrary.TryGetPixel("color", out source, out entry),
                    "the color pixel shader exists");

                var pixelShader = device.CompilePixelShader("test.color", source, entry);
                Assert.NotNull(pixelShader, "compiled pixel shader");
                pixelShader.Dispose();
            });
        }

        [Test]
        [RequiresGraphics]
        internal void DrawPrimitiveUpFillsTheExpectedPixels()
        {
            WithDevice(delegate(Win32Window window, D3D9Device device)
            {
                // A full-screen triangle in clip space, red. If the transform or
                // the vertex layout is wrong, the centre pixel stays black.
                var vertices = new[]
                {
                    new VertexPositionColor { X = -1f, Y = -1f, Z = 0.5f, Color = 0xFFFF0000 },
                    new VertexPositionColor { X = 3f, Y = -1f, Z = 0.5f, Color = 0xFFFF0000 },
                    new VertexPositionColor { X = -1f, Y = 3f, Z = 0.5f, Color = 0xFFFF0000 }
                };

                var identity = Matrix.Identity;

                device.BeginScene();
                device.Clear(0xFF000000, true, true, false);
                device.SetRenderState(D3D9.RenderState.Lighting, 0u);
                device.SetRenderState(D3D9.RenderState.CullMode, (uint)D3D9.Cull.None);
                device.SetRenderState(D3D9.RenderState.ZEnable, 0u);
                device.SetRenderState(D3D9.RenderState.AlphaBlendEnable, 0u);
                device.SetFvf(VertexPositionColor.Fvf);
                device.SetTransform(D3D9.Transform.World, ref identity);
                device.SetTransform(D3D9.Transform.View, ref identity);
                device.SetTransform(D3D9.Transform.Projection, ref identity);

                device.DrawPrimitiveUp(D3D9.PrimitiveType.TriangleList, 1, vertices,
                    (uint)VertexPositionColor.SizeInBytes);

                device.EndScene();

                var pixels = ReadBack(device);
                var centre = ReadPixel(pixels, Width / 2, Height / 2);

                AssertRgb(0xFFFF0000u, centre, "centre pixel is the triangle's colour");
            });
        }

        [Test]
        [RequiresGraphics]
        internal void TexturedQuadDrawsWithTheTextureColour()
        {
            WithDevice(delegate(Win32Window window, D3D9Device device)
            {
                const int size = 4;

                // A solid green texture: any sampling error shows up as a
                // different channel or as black.
                var bgra = new byte[size * size * 4];
                for (var i = 0; i < size * size; i++)
                {
                    bgra[i * 4 + 0] = 0x00; // B
                    bgra[i * 4 + 1] = 0xFF; // G
                    bgra[i * 4 + 2] = 0x00; // R
                    bgra[i * 4 + 3] = 0xFF; // A
                }

                using (var texture = device.CreateTexture(size, size, 1, false))
                {
                    texture.UploadBgra(0, bgra);

                    var vertices = new[]
                    {
                        new VertexPositionRhwColorTexture
                        {
                            X = 0f, Y = 0f, Z = 0f, Rhw = 1f, Color = 0xFFFFFFFF, U = 0f, V = 0f
                        },
                        new VertexPositionRhwColorTexture
                        {
                            X = Width, Y = 0f, Z = 0f, Rhw = 1f, Color = 0xFFFFFFFF, U = 1f, V = 0f
                        },
                        new VertexPositionRhwColorTexture
                        {
                            X = 0f, Y = Height, Z = 0f, Rhw = 1f, Color = 0xFFFFFFFF, U = 0f, V = 1f
                        },
                        new VertexPositionRhwColorTexture
                        {
                            X = Width, Y = Height, Z = 0f, Rhw = 1f, Color = 0xFFFFFFFF, U = 1f, V = 1f
                        }
                    };

                    string source;
                    string entry;

                    Assert.True(ShaderLibrary.TryGetPixel("texture", out source, out entry),
                        "the texture pixel shader exists");

                    using (var pixelShader = device.CompilePixelShader("test.texture", source, entry))
                    {
                        device.BeginScene();
                        device.Clear(0xFF000000, true, true, false);

                        device.SetRenderState(D3D9.RenderState.Lighting, 0u);
                        device.SetRenderState(D3D9.RenderState.CullMode, (uint)D3D9.Cull.None);
                        device.SetRenderState(D3D9.RenderState.ZEnable, 0u);
                        device.SetRenderState(D3D9.RenderState.AlphaBlendEnable, 0u);

                        device.SetTexture(0, texture);
                        device.SetPixelShader(pixelShader);
                        device.SetFvf(VertexPositionRhwColorTexture.Fvf);

                        device.DrawPrimitiveUp(D3D9.PrimitiveType.TriangleStrip, 2, vertices,
                            (uint)VertexPositionRhwColorTexture.SizeInBytes);

                        device.EndScene();
                    }

                    var pixels = ReadBack(device);
                    var centre = ReadPixel(pixels, Width / 2, Height / 2);

                    AssertRgb(0xFF00FF00u, centre, "centre pixel is the texture's green");
                }
            });
        }

        [Test]
        [RequiresGraphics]
        internal void RenderTargetRoundTripKeepsTheImage()
        {
            WithDevice(delegate(Win32Window window, D3D9Device device)
            {
                using (var target = device.CreateRenderTarget(64, 64))
                {
                    Assert.Equal(64, target.Width, "render target width");

                    // Fill the back buffer, copy it into the target, then clear the
                    // back buffer to a different colour. If the copy worked, the
                    // target still holds the first colour.
                    device.BeginScene();
                    device.Clear(0xFF112233, true, true, false);
                    device.EndScene();

                    device.CopyBackBufferTo(target, 0, 0, 64, 64);

                    device.BeginScene();
                    device.Clear(0xFF445566, true, true, false);
                    device.EndScene();

                    device.CopyRenderTargetToBackBuffer(target, 0, 0, 64, 64);

                    var pixels = ReadBack(device);
                    var centre = ReadPixel(pixels, 32, 32);

                    AssertRgb(0xFF112233u, centre, "the target preserved the copied colour");
                }
            });
        }

        [Test]
        internal void ShaderSourcesAreAllDiscoverable()
        {
            // A missing entry here would only surface as a black screen on the
            // machine that needed that particular shader.
            string source;
            string entry;

            var vertexShaders = new[] { "posnormaltex", "postex", "postexcolor", "poscolor" };
            foreach (var name in vertexShaders)
            {
                Assert.True(ShaderLibrary.TryGetVertex(name, out source, out entry), "vertex shader " + name);
                Assert.True(source.Length > 0, "vertex shader " + name + " has source");
            }

            var pixelShaders = new[]
            {
                "color", "texturecolor", "texturecolorenvmap", "texturecolorenvmapfog",
                "diffuse", "texturediffuse", "blur", "feedback", "contrast", "texture"
            };

            foreach (var name in pixelShaders)
            {
                Assert.True(ShaderLibrary.TryGetPixel(name, out source, out entry), "pixel shader " + name);
                Assert.True(source.Length > 0, "pixel shader " + name + " has source");
            }
        }

        // ---------------------------------------------------------------- helpers

        private static byte[] ReadBack(D3D9Device device)
        {
            // A separate device-level readback would be nicer, but the test only
            // needs the back buffer contents after EndScene, which is exactly what
            // the engine's screenshot path does.
            return device.ReadBackBuffer(Width, Height);
        }

        private static uint ReadPixel(byte[] bgra, int x, int y)
        {
            var offset = (y * Width + x) * 4;
            return BitConverter.ToUInt32(bgra, offset);
        }

        /// <summary>
        /// Compares only the colour channels.
        ///
        /// A windowed swap chain is created in X8R8G8B8 - the format the desktop
        /// uses - which has no alpha channel, so the alpha byte read back from it
        /// is meaningless (usually zero). Blending still works, because it reads
        /// the *source* alpha, not the destination's.
        /// </summary>
        private static void AssertRgb(uint expected, uint actual, string message)
        {
            Assert.Equal(expected & 0x00FFFFFF, actual & 0x00FFFFFF, message);
        }
    }
}
