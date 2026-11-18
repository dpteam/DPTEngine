//
// DPT.Engine - the runtime host.
// Copyright (C) 2026 DartPower Team. GPLv2.
//

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

using DPT.Engine.Engine;
using DPT.Engine.Graphics;
using DPT.Engine.Mathematics;
using DPT.Engine.Project;
using DPT.Engine.Rendering;
using DPT.Engine.Scenes;
using DPT.Engine.Shared;
using DPT.Engine.Shared.Diagnostics;
using DPT.Engine.Win32;

namespace DPT.Engine.Runtime
{
    /// <summary>
    /// The engine's main loop.
    ///
    /// Owns the window, the device and the player, and does nothing else: the
    /// timeline drives the picture, so this class is a scheduler and an input
    /// handler rather than a game.
    ///
    /// Timing is fixed-step at the project's frame rate, with a wall-clock
    /// accumulator. A demo has to play at the speed its author saw, not at
    /// whatever the machine can manage: dropping frames is better than slowing the
    /// music and the animation down together.
    /// </summary>
    internal sealed class EngineApp : IDisposable
    {
        private readonly string projectPath;

        private Win32Window window;
        private D3D9Device device;
        private SceneRenderer renderer;
        private QuadRenderer quads;
        private TimelinePlayer player;
        private TextRenderer text;

        private ProjectFile project;
        private int width = 1024;
        private int height = 768;

        private bool running;
        private bool showOverlay = true;
        private bool disposed;

        private long frequency;
        private long previousTicks;
        private double accumulator;

        private float framesPerSecond;
        private int framesThisSecond;
        private double secondStart;

        private int lastFrame;

        internal EngineApp(string projectPath)
        {
            this.projectPath = projectPath;
        }

        // ---------------------------------------------------------------- startup

        internal int Run()
        {
            FaultGuard.InstallCrashHandler(null);

            try
            {
                if (!Initialize())
                {
                    return 1;
                }

                Loop();
                return 0;
            }
            catch (Exception error)
            {
                EngineLog.Fatal("runtime", "Unhandled failure in the main loop", error);
                FaultGuard.WriteCrashReport(error, "runtime");

                ShowError("DPT.Engine could not start:\n\n" + error.Message +
                          "\n\nDetails were written to the log:\n" + EngineLog.FilePath);

                return 1;
            }
            finally
            {
                FaultGuard.BeginShutdown();
                Dispose();
            }
        }

        private bool Initialize()
        {
            project = LoadProject();

            if (project == null) return false;

            // The window opens at the project's size, clamped to what the desktop
            // can actually show. A 1920x1080 project on a 1366x768 laptop should
            // still be playable rather than partly off-screen.
            width = project.Width;
            height = project.Height;

            var screenWidth = NativeMethods.GetSystemMetrics(NativeMethods.SmCxScreen);
            var screenHeight = NativeMethods.GetSystemMetrics(NativeMethods.SmCyScreen);

            if (screenWidth > 0 && screenHeight > 0)
            {
                var maxWidth = screenWidth - 80;
                var maxHeight = screenHeight - 120;

                if (maxWidth > 320 && height > 0)
                {
                    var scale = Math.Min(1.0, Math.Min((double)maxWidth / width, (double)maxHeight / height));

                    if (scale < 1.0)
                    {
                        width = Math.Max(320, (int)(width * scale));
                        height = Math.Max(240, (int)(height * scale));
                    }
                }
            }

            window = new Win32Window();
            window.Create(Branding.Title, width, height, false);
            window.CloseRequested += delegate { running = false; };
            window.Resized += OnResized;

            device = new D3D9Device();
            device.Create(window.Handle, width, height, false, true);

            renderer = new SceneRenderer(device);
            quads = new QuadRenderer(device);
            text = new TextRenderer(device, quads);
            player = new TimelinePlayer(device, renderer, quads);

            renderer.InitializeShaders();
            quads.Initialize();
            player.SetProject(project);

            NativeMethods.TimeBeginPeriod(NativeMethods.TimerPeriodMs);
            NativeMethods.QueryPerformanceFrequency(out frequency);

            if (frequency <= 0) frequency = Stopwatch.Frequency;

            previousTicks = Now();

            EngineLog.Info("runtime", "Started " + Branding.Title + " at " + width + "x" + height + ".");
            FaultGuard.MarkRunning("runtime");

            return true;
        }

        private ProjectFile LoadProject()
        {
            if (!string.IsNullOrEmpty(projectPath))
            {
                try
                {
                    if (!File.Exists(projectPath))
                    {
                        ShowError("Project not found:\n" + projectPath);
                        return null;
                    }

                    return ProjectSerializer.Load(projectPath);
                }
                catch (Exception error)
                {
                    EngineLog.Exception("project", error);

                    // A broken project is not a reason to show nothing: fall back
                    // to the template so the engine still demonstrates itself, and
                    // say so.
                    ShowError("Could not load the project:\n" + projectPath +
                              "\n\n" + error.Message +
                              "\n\nStarting the built-in template instead.");
                }
            }

            EngineLog.Info("project", "Using the built-in template project.");
            return TemplateProject.Create();
        }

        // ---------------------------------------------------------------- loop

        private void Loop()
        {
            running = true;

            var frameDuration = 1.0 / Math.Max(1, project.FramesPerSecond);

            while (running)
            {
                if (!window.PumpMessages()) break;

                HandleInput();

                var now = Now();
                var elapsed = (double)(now - previousTicks) / frequency;
                previousTicks = now;

                // A long stall - a breakpoint, a driver reset, a minimised window -
                // must not make the demo fast-forward through its timeline. Cap the
                // step at a quarter second.
                if (elapsed > 0.25) elapsed = 0.25;

                if (elapsed < 0) elapsed = 0;

                accumulator += elapsed;

                var advanced = false;

                while (accumulator >= frameDuration)
                {
                    accumulator -= frameDuration;
                    player.Frame++;
                    advanced = true;

                    // At most a few frames per pass: if the machine cannot keep up,
                    // the demo runs slow rather than skipping half of itself.
                    if (player.Frame - lastFrame > 4) break;
                }

                if (advanced) lastFrame = player.Frame;

                if (!RenderFrame()) break;

                UpdateStatistics(elapsed);

                window.Input.EndFrame();
            }
        }

        /// <summary>
        /// Renders one frame. Returns false when the device is gone and the loop
        /// should stop.
        /// </summary>
        private bool RenderFrame()
        {
            if (!device.CheckDevice())
            {
                if (!RecoverDevice()) return false;
            }

            if (window.IsResizing)
            {
                // Drawing into a window the shell is dragging produces tearing and
                // a stutter that looks like a performance problem; skipping is
                // what every Windows game does.
                return true;
            }

            try
            {
                device.BeginScene();

                player.RenderFrame();

                if (showOverlay) DrawOverlay();

                device.EndScene();
                device.Present();
            }
            catch (D3D9Exception error)
            {
                EngineLog.Error("graphics", error.Message);

                if (device.IsLost)
                {
                    return RecoverDevice();
                }

                // One bad frame is not a reason to stop: the fault guard has the
                // failure recorded and the next frame may well succeed.
                FaultGuard.Record("graphics", "frame", error);
                return !FaultGuard.IsDisabled("graphics");
            }

            return true;
        }

        /// <summary>
        /// Recreates everything the device owned after a loss.
        ///
        /// Default-pool resources are gone, so the renderer, the text cache and the
        /// player all drop theirs and rebuild. This is the path an alt-tab, a
        /// driver update and a resolution change all take.
        /// </summary>
        private bool RecoverDevice()
        {
            EngineLog.Info("graphics", "Device lost; recovering.");

            var deadline = Environment.TickCount + 5000;

            while (Environment.TickCount < deadline)
            {
                if (!device.CheckDevice())
                {
                    System.Threading.Thread.Sleep(50);
                    continue;
                }

                try
                {
                    text.ReleaseDeviceResources();
                    player.ReleaseDeviceResources();

                    device.Reset(window.ClientWidth, window.ClientHeight, false, true);

                    player.RestoreDeviceResources();

                    EngineLog.Info("graphics", "Device recovered.");
                    return true;
                }
                catch (Exception error)
                {
                    EngineLog.Warn("graphics", "Recovery attempt failed: " + error.Message);
                    System.Threading.Thread.Sleep(100);
                }
            }

            EngineLog.Error("graphics", "Could not recover the device; stopping.");
            return false;
        }

        private void OnResized(int newWidth, int newHeight)
        {
            if (device == null || newWidth <= 0 || newHeight <= 0) return;

            try
            {
                text.ReleaseDeviceResources();
                player.ReleaseDeviceResources();

                device.Reset(newWidth, newHeight, false, true);

                player.RestoreDeviceResources();
            }
            catch (Exception error)
            {
                EngineLog.Warn("graphics", "Resize failed: " + error.Message);
            }
        }

        // ---------------------------------------------------------------- input

        private void HandleInput()
        {
            var input = window.Input;

            if (input.WasPressed(Keys.Escape))
            {
                running = false;
                return;
            }

            if (input.WasPressed(Keys.F1))
            {
                showOverlay = !showOverlay;
            }

            if (input.WasPressed(Keys.F2))
            {
                player.Rewind();
            }

            if (input.WasPressed(Keys.F11) || (input.Alt && input.WasPressed(Keys.Enter)))
            {
                var fullScreen = !window.IsFullScreen;
                window.SetFullScreen(fullScreen, width, height);
            }

            if (input.WasPressed(Keys.F12))
            {
                SaveScreenshot();
            }
        }

        private void SaveScreenshot()
        {
            FaultGuard.Guard("runtime", "screenshot", delegate
            {
                var directory = Path.Combine(PortablePaths.DataDirectory, "Screenshots");
                Directory.CreateDirectory(directory);

                var name = "dptengine-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".bmp";
                var path = Path.Combine(directory, name);

                var pixels = device.ReadBackBuffer(device.Width, device.Height);
                BmpWriter.Write(path, pixels, device.Width, device.Height);

                EngineLog.Info("runtime", "Screenshot saved to " + path);
            });
        }

        // ---------------------------------------------------------------- overlay

        private void DrawOverlay()
        {
            if (text == null) return;

            var colour = new ColorRgba(230, 240, 255, 255);
            var dim = new ColorRgba(150, 170, 200, 255);

            var y = 8;
            const int lineHeight = 17;

            text.Draw(Branding.Title, 8, y, colour, true);
            y += lineHeight;

            text.Draw("FPS " + framesPerSecond.ToString("0", CultureInfo.InvariantCulture) +
                      "   frame " + player.Frame + "/" + project.Duration +
                      "   effects " + player.ActiveEffectCount(), 8, y, dim, true);
            y += lineHeight;

            text.Draw(device.Description + "   " + device.Width + "x" + device.Height, 8, y, dim, true);
            y += lineHeight;

            text.Draw("F1 overlay   F2 restart   F11 fullscreen   F12 screenshot   Esc quit",
                8, y, dim, true);
            y += lineHeight;

            // Only show the subsystem table when something is wrong; a healthy
            // engine should not spend screen space saying so.
            var subsystems = FaultGuard.Snapshot();

            for (var i = 0; i < subsystems.Length; i++)
            {
                var subsystem = subsystems[i];

                if (subsystem.State == SubsystemState.Running ||
                    subsystem.State == SubsystemState.Idle) continue;

                text.Draw(subsystem.Name + ": " + subsystem.State +
                          (string.IsNullOrEmpty(subsystem.LastError) ? "" : " - " + subsystem.LastError),
                    8, y, new ColorRgba(255, 200, 120, 255), true);
                y += lineHeight;
            }
        }

        private void UpdateStatistics(double elapsed)
        {
            framesThisSecond++;

            if (secondStart == 0)
            {
                secondStart = Now() / (double)frequency;
                return;
            }

            var nowSeconds = Now() / (double)frequency;

            if (nowSeconds - secondStart >= 1.0)
            {
                framesPerSecond = (float)(framesThisSecond / (nowSeconds - secondStart));
                framesThisSecond = 0;
                secondStart = nowSeconds;
            }
        }

        private long Now()
        {
            long counter;

            if (NativeMethods.QueryPerformanceCounter(out counter)) return counter;

            return Environment.TickCount;
        }

        // ---------------------------------------------------------------- errors

        private void ShowError(string message)
        {
            // A message box is the only channel guaranteed to exist when the engine
            // cannot start; the log may be the thing that is broken.
            try
            {
                Console.Error.WriteLine(message);
            }
            catch
            {
            }

            try
            {
                NativeMethods.MessageBeep(0x00000010); // MB_ICONHAND
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            try
            {
                NativeMethods.TimeEndPeriod(NativeMethods.TimerPeriodMs);
            }
            catch
            {
            }

            if (player != null) { player.Dispose(); player = null; }
            if (text != null) { text.Dispose(); text = null; }
            if (quads != null) { quads.Dispose(); quads = null; }
            if (renderer != null) { renderer.Dispose(); renderer = null; }
            if (device != null) { device.Dispose(); device = null; }
            if (window != null) { window.Dispose(); window = null; }

            EngineLog.Info("runtime", "Stopped.");
        }
    }
}
