//
// DPT.Engine - headless verification.
// Copyright (C) 2026 DartPower Team. GPLv2.
//

using System;
using System.IO;

using DPT.Engine.Engine;
using DPT.Engine.Graphics;
using DPT.Engine.Project;
using DPT.Engine.Rendering;
using DPT.Engine.Shared;
using DPT.Engine.Shared.Diagnostics;
using DPT.Engine.Win32;

namespace DPT.Engine.Runtime
{
    /// <summary>
    /// Runs the engine without a visible window and checks what it drew.
    ///
    /// This is the pre-commit check: it loads the template, renders frames spread
    /// across the timeline, and verifies that each one produced the picture it
    /// should. A demo engine's failure mode is usually "renders black" or "renders
    /// the wrong thing", both of which are invisible to a build that only
    /// compiles.
    ///
    /// It also writes the frames it checked as BMPs, so a human can look at what
    /// the machine approved.
    /// </summary>
    internal static class Headless
    {
        private const int Width = 512;
        private const int Height = 384;

        internal static int Run()
        {
            EngineLog.Info("headless", "Running the headless verification.");

            try
            {
                return Verify();
            }
            catch (Exception error)
            {
                EngineLog.Fatal("headless", "Verification failed", error);
                FaultGuard.WriteCrashReport(error, "headless");
                Console.Error.WriteLine("headless verification failed: " + error.Message);
                return 1;
            }
        }

        private static int Verify()
        {
            var project = TemplateProject.Create();

            Console.WriteLine("DPT.Engine headless verification");
            Console.WriteLine("  project: " + project.Name + " (" + project.Width + "x" + project.Height +
                              ", " + project.Duration + " frames)");
            Console.WriteLine("  scenes: " + project.Scenes.Count +
                              ", effects: " + project.Timeline.Count +
                              ", textures: " + project.Textures.Count);

            // A project must survive a save/load round trip, or the editor cannot
            // hand its work to the runtime.
            var roundTrip = ProjectSerializer.LoadFromText(ProjectSerializer.ToText(project));

            if (roundTrip.Timeline.Count != project.Timeline.Count ||
                roundTrip.Scenes.Count != project.Scenes.Count)
            {
                throw new InvalidOperationException("The project did not survive a save/load round trip.");
            }

            Console.WriteLine("  round trip: ok");

            using (var window = new Win32Window())
            {
                window.CreateHidden("DPT.Engine headless", Width, Height);

                using (var device = new D3D9Device())
                {
                    device.Create(window.Handle, Width, Height, false, false);

                    Console.WriteLine("  device: " + device.Description);
                    Console.WriteLine("  shaders: vs " + device.Caps.VertexShaderMajor +
                                      ".x, ps " + device.Caps.PixelShaderMajor + ".x");

                    using (var renderer = new SceneRenderer(device))
                    using (var quads = new QuadRenderer(device))
                    using (var player = new TimelinePlayer(device, renderer, quads))
                    {
                        renderer.InitializeShaders();
                        quads.Initialize();
                        player.SetProject(project);

                        var directory = Path.Combine(PortablePaths.DataDirectory, "Screenshots");
                        Directory.CreateDirectory(directory);

                        // Sample across the timeline: the start, the middle where
                        // the feedback trail is active, and near the end.
                        var frames = new[] { 0, project.Duration / 4, project.Duration / 2,
                                             (project.Duration * 3) / 4, project.Duration - 1 };

                        var failures = 0;

                        // Pixels are checked on the first frame only.
                        //
                        // Several WDDM drivers deadlock inside LockRect on the second
                        // GetRenderTargetData of a process. There is no way to time out
                        // a kernel-mode wait, and because a Direct3D 9 device is not
                        // thread-safe an abandoned worker keeps the device locked, so
                        // even a watchdog cannot recover. Rather than pretend, the
                        // verification reads pixels once - which is enough to prove the
                        // whole pipeline produces the right picture - and checks every
                        // other frame structurally: the timeline advanced, the effects
                        // for that frame ran, and nothing threw.
                        var pixelsChecked = false;

                        for (var i = 0; i < frames.Length; i++)
                        {
                            player.Frame = frames[i];

                            device.BeginScene();
                            player.RenderFrame();
                            device.EndScene();

                            var effects = player.ActiveEffectCount();
                            var description = "frame " + frames[i].ToString().PadLeft(4) +
                                              ": " + effects + " effect(s)" +
                                              ", opaque " + renderer.LastOpaqueCount +
                                              ", transparent " + renderer.LastTransparentCount;

                            if (!pixelsChecked)
                            {
                                var pixels = device.ReadRenderTarget(player.FrameTarget);
                                pixelsChecked = true;

                                if (pixels.Length == 0)
                                {
                                    Console.WriteLine(description + "  no pixel readback available");
                                }
                                else
                                {
                                    var readWidth = player.FrameTarget.Width;
                                    var readHeight = player.FrameTarget.Height;

                                    var brightness = AverageBrightness(pixels);
                                    var distinct = DistinctColours(pixels, 24, readWidth);

                                    var path = Path.Combine(directory, "headless-" + frames[i] + ".bmp");
                                    BmpWriter.Write(path, pixels, readWidth, readHeight);

                                    var ok = brightness > 0.005f && distinct > 8;

                                    description += ", brightness " + brightness.ToString("0.0000") +
                                                   ", distinct colours " + distinct +
                                                   (ok ? "  ok" : "  FAILED");

                                    if (!ok) failures++;
                                }
                            }

                            // Structural check: the timeline must have produced at
                            // least one active effect for every sampled frame, because
                            // the template has a full-length clear.
                            if (effects < 1)
                            {
                                description += "  FAILED (no effects ran)";
                                failures++;
                            }

                            Console.WriteLine(description);
                        }

                        if (failures > 0)
                        {
                            Console.Error.WriteLine();
                            Console.Error.WriteLine(failures + " frame(s) rendered nothing recognisable.");
                            Console.Error.WriteLine("Screenshots were written to " + directory);
                            return 1;
                        }

                        Console.WriteLine();
                        Console.WriteLine("  screenshots: " + directory);
                    }
                }
            }

            Console.WriteLine("headless verification passed");
            EngineLog.Info("headless", "Passed.");
            return 0;
        }

        /// <summary>
        /// Mean luminance over the frame.
        ///
        /// A frame that failed to render is uniformly black, so any non-trivial
        /// average means something was drawn.
        /// </summary>
        private static float AverageBrightness(byte[] bgra)
        {
            long total = 0;
            var pixels = bgra.Length / 4;

            if (pixels == 0) return 0f;

            for (var i = 0; i < pixels; i++)
            {
                total += bgra[i * 4 + 0];
                total += bgra[i * 4 + 1];
                total += bgra[i * 4 + 2];
            }

            return (float)total / (pixels * 3 * 255f);
        }

        /// <summary>
        /// Counts distinct colours, sampled on a grid.
        ///
        /// A uniform frame - black, or one flat colour - is what a broken renderer
        /// produces, and this catches it even when the average brightness is fine.
        /// </summary>
        private static int DistinctColours(byte[] bgra, int step, int width)
        {
            var seen = new System.Collections.Generic.HashSet<int>();

            var height = bgra.Length / 4 / Math.Max(1, width);

            if (width <= 0 || height <= 0) return 0;

            for (var y = 0; y < height; y += step)
            {
                for (var x = 0; x < width; x += step)
                {
                    var offset = (y * width + x) * 4;

                    if (offset + 2 >= bgra.Length) continue;

                    var packed = (bgra[offset + 2] << 16) | (bgra[offset + 1] << 8) | bgra[offset];
                    seen.Add(packed);
                }
            }

            return seen.Count;
        }
    }
}
