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
    /// usually as a crash somewhere unrelated. Every one of them draws something
    /// real through the same interop the engine uses.
    ///
    /// Two constraints shape how they are written, and both come from the driver
    /// this was developed against rather than from taste:
    ///
    /// * <b>One device for the whole process.</b> Creating and destroying several
    ///   devices in one run is unreliable there; the tests were seen to hang or to
    ///   draw black depending on what ran before them. The device is created on
    ///   first use and deliberately never disposed - the test process exits
    ///   immediately afterwards, so the OS reclaims it.
    ///
    /// * <b>One pixel readback for the whole process.</b> Readback is a one-shot
    ///   diagnostic (see <see cref="D3D9Device.ReadbackUnavailable"/>), so the
    ///   single read asserts on several disjoint regions of one frame instead of
    ///   spreading the checks over several frames. That is more coverage per
    ///   readback, not less.
    ///
    /// A machine with no Direct3D 9 driver at all - a bare VM, some CI containers -
    /// throws <see cref="GraphicsUnavailableException"/> and the runner reports the
    /// test as skipped rather than failed.
    /// </summary>
    internal sealed class GraphicsTests
    {
        private const int Width = 160;
        private const int Height = 120;

        private static Win32Window sharedWindow;
        private static D3D9Device sharedDevice;

        /// <summary>
        /// Opens the process-wide hidden window and device, once, and hands them to
        /// the body. See the class comment for why they are shared and never disposed.
        /// </summary>
        private static void WithDevice(Action<Win32Window, D3D9Device> body)
        {
            if (sharedDevice == null)
            {
                var window = new Win32Window();

                try
                {
                    window.CreateHidden("DPT.Engine test", Width, Height);
                }
                catch (Exception error)
                {
                    window.Dispose();
                    throw new GraphicsUnavailableException("no window: " + error.Message);
                }

                var device = new D3D9Device();

                try
                {
                    device.Create(window.Handle, Width, Height, false, false);
                }
                catch (Exception error)
                {
                    device.Dispose();
                    window.Dispose();
                    throw new GraphicsUnavailableException("no Direct3D 9 device: " + error.Message);
                }

                sharedWindow = window;
                sharedDevice = device;
            }

            body(sharedWindow, sharedDevice);
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

        /// <summary>
        /// Draws one frame that exercises the clear, the render-target round trip, an
        /// immediate-mode triangle and a textured quad, then reads it back once and
        /// checks a pixel in each region.
        ///
        /// The regions are deliberately disjoint so that one readback can verify all
        /// four paths. Layout, in a 160x120 buffer:
        ///
        ///   (0,0)-(64,64)      the image that went through a render target
        ///   (64,0)-(160,60)    a red immediate-mode triangle
        ///   (64,60)-(160,120)  a green textured quad
        ///
        /// A wrong transform, a wrong vertex layout, a wrong texture binding or a
        /// broken surface copy each turn one of those regions black or the wrong
        /// colour, and nothing else does.
        /// </summary>
        [Test]
        [RequiresGraphics]
        internal void FrameRendersClearTargetTriangleAndTexture()
        {
            WithDevice(delegate(Win32Window window, D3D9Device device)
            {
                const uint targetColour = 0xFF112233;
                const uint triangleColour = 0xFFFF0000;

                // --- 1. the render-target round trip, parked in the top-left corner.
                using (var target = device.CreateRenderTarget(64, 64))
                {
                    Assert.Equal(64, target.Width, "render target width");

                    // Fill the back buffer, copy it into the target, then clear the
                    // back buffer to a different colour. If the copy worked, the
                    // target still holds the first colour and pasting it back shows
                    // up in the corner only.
                    device.BeginScene();
                    device.Clear(targetColour, true, true, false);
                    device.EndScene();

                    device.CopyBackBufferTo(target, 0, 0, 64, 64);

                    device.BeginScene();
                    device.Clear(0xFF000000, true, true, false);
                    device.EndScene();

                    device.CopyRenderTargetToBackBuffer(target, 0, 0, 64, 64);
                }

                var identity = Matrix.Identity;

                device.BeginScene();

                device.SetRenderState(D3D9.RenderState.Lighting, 0u);
                device.SetRenderState(D3D9.RenderState.CullMode, (uint)D3D9.Cull.None);
                device.SetRenderState(D3D9.RenderState.ZEnable, 0u);
                device.SetRenderState(D3D9.RenderState.AlphaBlendEnable, 0u);

                // --- 3. a red triangle over the right half of the frame.
                // Clip space maps x [-1,1] to [0,Width] and y to the full height,
                // so this covers everything right of x = 64.
                var triangle = new[]
                {
                    new VertexPositionColor { X = -0.2f, Y = 1f, Z = 0.5f, Color = triangleColour },
                    new VertexPositionColor { X = 1f, Y = 1f, Z = 0.5f, Color = triangleColour },
                    new VertexPositionColor { X = -0.2f, Y = -1f, Z = 0.5f, Color = triangleColour }
                };

                device.SetFvf(VertexPositionColor.Fvf);
                device.SetTransform(D3D9.Transform.World, ref identity);
                device.SetTransform(D3D9.Transform.View, ref identity);
                device.SetTransform(D3D9.Transform.Projection, ref identity);

                device.DrawPrimitiveUp(D3D9.PrimitiveType.TriangleList, 1, triangle,
                    (uint)VertexPositionColor.SizeInBytes);

                // --- 4. a solid green textured quad in the bottom-right region.
                const int textureSize = 4;
                var bgra = new byte[textureSize * textureSize * 4];

                for (var i = 0; i < textureSize * textureSize; i++)
                {
                    bgra[i * 4 + 0] = 0x00; // B
                    bgra[i * 4 + 1] = 0xFF; // G
                    bgra[i * 4 + 2] = 0x00; // R
                    bgra[i * 4 + 3] = 0xFF; // A
                }

                using (var texture = device.CreateTexture(textureSize, textureSize, 1, false))
                {
                    texture.UploadBgra(0, bgra);

                    var quad = new[]
                    {
                        new VertexPositionRhwColorTexture
                        {
                            X = 64f, Y = 60f, Z = 0f, Rhw = 1f, Color = 0xFFFFFFFF, U = 0f, V = 0f
                        },
                        new VertexPositionRhwColorTexture
                        {
                            X = Width, Y = 60f, Z = 0f, Rhw = 1f, Color = 0xFFFFFFFF, U = 1f, V = 0f
                        },
                        new VertexPositionRhwColorTexture
                        {
                            X = 64f, Y = Height, Z = 0f, Rhw = 1f, Color = 0xFFFFFFFF, U = 0f, V = 1f
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
                        device.SetTexture(0, texture);
                        device.SetPixelShader(pixelShader);
                        device.SetFvf(VertexPositionRhwColorTexture.Fvf);

                        device.DrawPrimitiveUp(D3D9.PrimitiveType.TriangleStrip, 2, quad,
                            (uint)VertexPositionRhwColorTexture.SizeInBytes);
                    }
                }

                device.EndScene();

                // --- 5. the one readback this process gets.
                var pixels = device.ReadBackBuffer(Width, Height);

                Assert.True(pixels.Length > 0, "the readback returned pixels");

                AssertRgb(targetColour, ReadPixel(pixels, 32, 32),
                    "the render-target copy preserved its colour");

                AssertRgb(triangleColour, ReadPixel(pixels, 70, 55),
                    "the immediate-mode triangle drew its colour");

                AssertRgb(0xFF00FF00u, ReadPixel(pixels, 128, 90),
                    "the textured quad drew the texture's green");
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

        private static uint ReadPixel(byte[] bgra, int x, int y)
        {
            var offset = (y * Width + x) * 4;

            if (offset + 3 >= bgra.Length) return 0;

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
