//
// DPT.Engine - timeline playback.
// Copyright (C) 2026 DartPower Team. GPLv2.
//

using System;
using System.Collections.Generic;

using DPT.Engine.Graphics;
using DPT.Engine.Mathematics;
using DPT.Engine.Project;
using DPT.Engine.Rendering;
using DPT.Engine.Scenes;
using DPT.Engine.Shared.Diagnostics;

namespace DPT.Engine.Engine
{
    /// <summary>
    /// Plays a project's timeline.
    ///
    /// The project renders into an off-screen buffer at its own resolution and the
    /// result is stretched to the window with letterboxing. That one decision
    /// removes a whole class of bugs: every effect, every 3D viewport and every
    /// pixel-space rectangle is authored against one fixed size, so a demo looks
    /// identical on a 1024x768 laptop and a 4K monitor. Scaling at the end is also
    /// what lets the engine run a 320x240 project for speed on old hardware and
    /// still fill the screen.
    ///
    /// Effects are evaluated in timeline order every frame, and the render targets
    /// are shared between them, which is what makes the feedback and blur effects
    /// work: each one reads what the previous one wrote.
    /// </summary>
    internal sealed class TimelinePlayer : IDisposable
    {
        /// <summary>Off-screen slots available to the render-to-texture effects.</summary>
        internal const int SlotCount = 6;

        private readonly D3D9Device device;
        private readonly SceneRenderer renderer;
        private readonly QuadRenderer quads;

        private D3D9RenderTarget frameTarget;
        private readonly D3D9RenderTarget[] slots = new D3D9RenderTarget[SlotCount];

        private ProjectFile project;
        private EffectContext context;

        private bool disposed;

        /// <summary>Frame the playhead is on. Set by the host each frame.</summary>
        internal int Frame;

        /// <summary>When true the timeline loops at the end.</summary>
        internal bool Loop = true;

        internal TimelinePlayer(D3D9Device device, SceneRenderer renderer, QuadRenderer quads)
        {
            if (device == null) throw new ArgumentNullException("device");
            if (renderer == null) throw new ArgumentNullException("renderer");
            if (quads == null) throw new ArgumentNullException("quads");

            this.device = device;
            this.renderer = renderer;
            this.quads = quads;

            context = new EffectContext();
            context.Device = device;
            context.Renderer = renderer;
            context.Quads = quads;
            context.Slots = slots;
        }

        internal ProjectFile Project
        {
            get { return project; }
        }

        /// <summary>
        /// The off-screen buffer the project renders into.
        ///
        /// Exposed so verification and screenshots can read the frame without going
        /// through the swap chain, which stalls on a window that is not presenting.
        /// </summary>
        internal D3D9RenderTarget FrameTarget
        {
            get { return frameTarget; }
        }

        /// <summary>
        /// Installs a project and allocates its buffers.
        ///
        /// Safe to call with a different project at runtime: the editor uses that
        /// to reload after a save without restarting the process.
        /// </summary>
        internal void SetProject(ProjectFile value)
        {
            if (value == null) throw new ArgumentNullException("value");

            project = value;
            context.Project = value;

            RecreateTargets();
            UploadTextures();

            Frame = 0;

            for (var i = 0; i < value.Scenes.Count; i++)
            {
                value.Scenes[i].ResetParticles();
            }

            EngineLog.Info("player", "Project '" + value.Name + "' installed: " + value.Width + "x" +
                                     value.Height + ", " + value.Duration + " frames at " +
                                     value.FramesPerSecond + " fps.");
        }

        /// <summary>
        /// Uploads every texture the project carries into the renderer.
        ///
        /// Without this a scene's materials reference texture ids that the renderer
        /// has never seen, so every textured surface draws untextured - which is how
        /// the template's floor first rendered as a flat unlit quad instead of a
        /// grid. Uploading is done once per project, not per frame.
        /// </summary>
        private void UploadTextures()
        {
            if (project == null) return;

            var uploaded = 0;

            for (var i = 0; i < project.Textures.Count; i++)
            {
                var texture = project.Textures[i];

                if (!texture.IsValid) continue;

                FaultGuard.Guard("player", "texture:" + texture.Id, delegate
                {
                    renderer.UploadTexture(texture.Id, texture.Width, texture.Height, texture.Pixels);
                });

                uploaded++;
            }

            EngineLog.Info("player", "Uploaded " + uploaded + " texture(s).");
        }

        private void RecreateTargets()
        {
            ReleaseTargets();

            if (project == null) return;

            var width = Math.Max(1, project.Width);
            var height = Math.Max(1, project.Height);

            frameTarget = device.CreateRenderTarget(width, height);

            for (var i = 0; i < SlotCount; i++)
            {
                // Each slot is a quarter of the frame: the post-processes sample
                // them at their own resolution anyway, and six full-size targets
                // would be 24 MB at 1024x768.
                slots[i] = device.CreateRenderTarget(
                    Math.Max(1, width / 2), Math.Max(1, height / 2));
            }

            context.Width = width;
            context.Height = height;
        }

        /// <summary>
        /// Advances one frame and renders it.
        ///
        /// The caller owns BeginScene/EndScene and Present, so the same player can
        /// be used for the visible window and for an off-screen editor preview.
        /// </summary>
        internal void RenderFrame()
        {
            if (project == null || frameTarget == null) return;

            if (project.Duration > 0)
            {
                if (Loop)
                {
                    Frame %= project.Duration;

                    if (Frame < 0) Frame += project.Duration;
                }
                else
                {
                    if (Frame < 0) Frame = 0;
                    if (Frame > project.Duration) Frame = project.Duration;
                }
            }

            // Render the whole project into its own buffer.
            device.SetRenderTarget(frameTarget);
            device.Clear(0xFF000000, true, true, false);

            context.Frame = Frame;
            context.TimePosition = Frame;
            context.EyeShake = Vector3.Zero;
            context.TargetShake = Vector3.Zero;
            context.FrameTarget = frameTarget;

            var timeline = project.Timeline;

            for (var i = 0; i < timeline.Count; i++)
            {
                var effect = timeline[i];

                if (!effect.IsActive(Frame)) continue;

                effect.ComputeRect(context);

                FaultGuard.Guard("player", "effect:" + effect.TypeName, delegate { effect.Render(context); });
            }

            // Back to the window and blit with letterboxing.
            device.SetRenderTarget(null);
            device.SetViewport(0, 0, device.Width, device.Height);

            PresentFrameToWindow();

            // The 3D pass sets its viewport to the event rectangle; leaving it
            // there would make the next frame's effects draw into a corner.
            device.SetViewport(0, 0, device.Width, device.Height);
        }

        /// <summary>
        /// Draws the project buffer to the window, preserving the project's aspect
        /// ratio and filling the bars with black.
        /// </summary>
        private void PresentFrameToWindow()
        {
            if (!quads.IsReady) quads.Initialize();

            var windowWidth = device.Width;
            var windowHeight = device.Height;

            if (windowWidth <= 0 || windowHeight <= 0) return;

            var projectAspect = project.AspectRatio;
            var windowAspect = (float)windowWidth / windowHeight;

            int width;
            int height;
            int x;
            int y;

            if (windowAspect > projectAspect)
            {
                // Window is wider: bars left and right.
                height = windowHeight;
                width = (int)(windowHeight * projectAspect);
                x = (windowWidth - width) / 2;
                y = 0;
            }
            else
            {
                // Window is taller: bars top and bottom.
                width = windowWidth;
                height = (int)(windowWidth / projectAspect);
                x = 0;
                y = (windowHeight - height) / 2;
            }

            if (width <= 0 || height <= 0) return;

            device.Clear(0xFF000000, true, false, false);

            quads.BeginScreenSpace(windowWidth, windowHeight);
            quads.Draw(frameTarget.Texture, x, y, width, height,
                ColorRgba.White, BlendMode.One, BlendMode.Zero);

            // Unbind the frame buffer again. GetRenderTargetData refuses a source
            // that is still bound to the device, and the verification path reads
            // this very target on the next line - leaving it bound made the
            // readback fail, and on some drivers stall.
            device.SetTexture(0, null);
        }

        /// <summary>Number of effects active on the current frame, for the overlay.</summary>
        internal int ActiveEffectCount()
        {
            if (project == null) return 0;

            var count = 0;

            for (var i = 0; i < project.Timeline.Count; i++)
            {
                if (project.Timeline[i].IsActive(Frame)) count++;
            }

            return count;
        }

        /// <summary>Restarts the timeline and every particle emitter.</summary>
        internal void Rewind()
        {
            Frame = 0;

            if (project == null) return;

            for (var i = 0; i < project.Scenes.Count; i++)
            {
                project.Scenes[i].ResetParticles();
            }
        }

        private void ReleaseTargets()
        {
            if (frameTarget != null)
            {
                frameTarget.Dispose();
                frameTarget = null;
            }

            for (var i = 0; i < slots.Length; i++)
            {
                if (slots[i] != null)
                {
                    slots[i].Dispose();
                    slots[i] = null;
                }
            }
        }

        /// <summary>Drops every device-owned buffer, before a device reset.</summary>
        internal void ReleaseDeviceResources()
        {
            ReleaseTargets();
            quads.ReleaseDeviceResources();
            renderer.ReleaseDeviceResources();
        }

        /// <summary>Rebuilds everything after a device reset.</summary>
        internal void RestoreDeviceResources()
        {
            renderer.InitializeShaders();
            quads.Initialize();
            RecreateTargets();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            ReleaseTargets();
        }
    }
}
