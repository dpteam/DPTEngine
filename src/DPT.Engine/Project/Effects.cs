//
// DPT.Engine - timeline effects.
// Copyright (C) 2026 DartPower Team. GPLv2.
//

using System;
using System.Collections.Generic;

using DPT.Engine.Graphics;
using DPT.Engine.Mathematics;
using DPT.Engine.Rendering;
using DPT.Engine.Scenes;
using DPT.Engine.Shared;
using DPT.Engine.Shared.Diagnostics;

namespace DPT.Engine.Project
{
    /// <summary>
    /// The state a timeline effect needs while it runs.
    ///
    /// Passed by reference rather than stored, because effects are stateless data:
    /// the same effect object renders at every frame of its span, and everything
    /// that changes between frames lives here.
    /// </summary>
    public sealed class EffectContext
    {
        internal D3D9Device Device;
        internal SceneRenderer Renderer;
        internal QuadRenderer Quads;
        internal ProjectFile Project;

        /// <summary>Off-screen buffers the render-to-texture effects write into.</summary>
        internal D3D9RenderTarget[] Slots;

        /// <summary>
        /// The buffer the project is currently rendering into.
        ///
        /// Capture effects read from here rather than from the window's back buffer:
        /// the project has its own resolution and its own buffer, and the back
        /// buffer holds a previous frame.
        /// </summary>
        internal D3D9RenderTarget FrameTarget;

        internal int Frame;
        internal float TimePosition;

        /// <summary>Screen rectangle this effect occupies, in device pixels.</summary>
        internal int X1, Y1, X2, Y2;

        /// <summary>Camera offsets accumulated by shake effects, in world units.</summary>
        internal Vector3 EyeShake;
        internal Vector3 TargetShake;

        internal int Width;
        internal int Height;

        /// <summary>
        /// The virtual coordinate space timeline rectangles are authored in.
        ///
        /// The original engine used 800x600 for every event rectangle, regardless
        /// of the actual render size, and scaled to fit with the project's aspect
        /// correction. Keeping that means an existing project's rectangles land in
        /// the same place at any resolution.
        /// </summary>
        internal const float VirtualWidth = 800f;
        internal const float VirtualHeight = 600f;
    }

    /// <summary>
    /// One entry on the timeline.
    ///
    /// An effect is active while the playhead is inside its span, and draws
    /// itself into a rectangle that can animate over that span - which is what
    /// makes a wipe, a zoom or a moving spotlight possible without a keyframe
    /// system for the rectangle itself.
    /// </summary>
    public abstract class Effect
    {
        public int StartFrame;
        public int EndFrame;

        /// <summary>Rectangle at the start of the effect, in the 800x600 virtual space.</summary>
        public Rect StartRect = Rect.FullScreen;

        /// <summary>Rectangle at the end of the effect. Equal to <see cref="StartRect"/> for a static effect.</summary>
        public Rect EndRect = Rect.FullScreen;

        /// <summary>The name the editor and the file format use.</summary>
        public abstract string TypeName { get; }

        /// <summary>True when the effect is still drawing; false when it is a pure state change.</summary>
        public virtual bool Draws
        {
            get { return true; }
        }

        public bool IsActive(int frame)
        {
            return frame >= StartFrame && frame <= EndFrame;
        }

        /// <summary>0 at the start of the span, 1 at the end. Zero-length spans read as 0.</summary>
        public float Progress(int frame)
        {
            var span = EndFrame - StartFrame;

            if (span <= 0) return 0f;

            var value = (float)(frame - StartFrame) / span;
            return value < 0f ? 0f : (value > 1f ? 1f : value);
        }

        /// <summary>Interpolates the rectangle and maps it to device pixels.</summary>
        internal void ComputeRect(EffectContext context)
        {
            var progress = Progress(context.Frame);

            var x1 = StartRect.X + (EndRect.X - StartRect.X) * progress;
            var y1 = StartRect.Y + (EndRect.Y - StartRect.Y) * progress;
            var x2 = StartRect.X2 + (EndRect.X2 - StartRect.X2) * progress;
            var y2 = StartRect.Y2 + (EndRect.Y2 - StartRect.Y2) * progress;

            var scaleX = context.Width / EffectContext.VirtualWidth;
            var scaleY = context.Height / EffectContext.VirtualHeight;

            context.X1 = (int)(x1 * scaleX);
            context.Y1 = (int)(y1 * scaleY);
            context.X2 = (int)(x2 * scaleX);
            context.Y2 = (int)(y2 * scaleY);

            // An inverted or empty rectangle would make the viewport invalid and
            // the driver would reject the draw; clamp instead.
            if (context.X2 <= context.X1) context.X2 = context.X1 + 1;
            if (context.Y2 <= context.Y1) context.Y2 = context.Y1 + 1;
        }

        public abstract void Render(EffectContext context);

        // ---------------------------------------------------------------- serialization

        public virtual void Write(JsonValue json)
        {
            json.Set("type", TypeName);
            json.Set("start", StartFrame);
            json.Set("end", EndFrame);
            json.Set("rect", StartRect.ToJson());
            json.Set("rectEnd", EndRect.ToJson());
        }

        public virtual void Read(JsonValue json)
        {
            StartFrame = json["start"].AsInt(0);
            EndFrame = json["end"].AsInt(0);
            StartRect = Rect.FromJson(json["rect"], Rect.FullScreen);
            EndRect = Rect.FromJson(json["rectEnd"], StartRect);
        }

        /// <summary>
        /// Creates the effect for a type name, or null when the name is unknown.
        ///
        /// Unknown types are skipped rather than fatal: a project written by a
        /// newer editor should still play its known effects on an older runtime.
        /// </summary>
        public static Effect Create(string typeName)
        {
            switch ((typeName ?? "").ToLowerInvariant())
            {
                case "clear": return new ClearEffect();
                case "layer2d": return new Layer2DEffect();
                case "display3d": return new Display3DEffect();
                case "rendertotexture": return new RenderToTextureEffect();
                case "blur": return new BlurEffect();
                case "feedback": return new FeedbackEffect();
                case "contrast": return new ContrastEffect();
                case "camerashake": return new CameraShakeEffect();
                case "particles": return new ParticleEffect();
                default: return null;
            }
        }

        public static Effect Load(JsonValue json)
        {
            var effect = Create(json["type"].AsString(""));

            if (effect == null)
            {
                EngineLog.Warn("timeline", "Skipping unknown effect type '" + json["type"].AsString("") + "'.");
                return null;
            }

            effect.Read(json);
            return effect;
        }

        // ---------------------------------------------------------------- shared helpers

        internal static ColorRgba LerpColour(ColorRgba start, ColorRgba end, float progress)
        {
            return ColorRgba.Lerp(start, end, progress);
        }

        internal static BlendMode ParseBlend(JsonValue value, BlendMode fallback)
        {
            var name = value.AsString("");

            switch (name.ToLowerInvariant())
            {
                case "zero": return BlendMode.Zero;
                case "one": return BlendMode.One;
                case "sourcecolour":
                case "sourcecolor": return BlendMode.SourceColour;
                case "inversesourcecolour":
                case "inversesourcecolor": return BlendMode.InverseSourceColour;
                case "sourcealpha": return BlendMode.SourceAlpha;
                case "inversesourcealpha": return BlendMode.InverseSourceAlpha;
                case "destinationalpha": return BlendMode.DestinationAlpha;
                case "inversedestinationalpha": return BlendMode.InverseDestinationAlpha;
                case "destinationcolour":
                case "destinationcolor": return BlendMode.DestinationColour;
                case "inversedestinationcolour":
                case "inversedestinationcolor": return BlendMode.InverseDestinationColour;
                case "sourcealphasaturation": return BlendMode.SourceAlphaSaturation;
                default: return fallback;
            }
        }

        internal static string BlendName(BlendMode mode)
        {
            switch (mode)
            {
                case BlendMode.Zero: return "zero";
                case BlendMode.One: return "one";
                case BlendMode.SourceColour: return "sourceColour";
                case BlendMode.InverseSourceColour: return "inverseSourceColour";
                case BlendMode.SourceAlpha: return "sourceAlpha";
                case BlendMode.InverseSourceAlpha: return "inverseSourceAlpha";
                case BlendMode.DestinationAlpha: return "destinationAlpha";
                case BlendMode.InverseDestinationAlpha: return "inverseDestinationAlpha";
                case BlendMode.DestinationColour: return "destinationColour";
                case BlendMode.InverseDestinationColour: return "inverseDestinationColour";
                default: return "sourceAlphaSaturation";
            }
        }
    }

    /// <summary>
    /// A rectangle in the timeline's virtual space, in pixels.
    ///
    /// The fields are the two corners, not an origin and a size, and they are named
    /// accordingly: <see cref="X"/>..<see cref="X2"/> and <see cref="Y"/>..<see
    /// cref="Y2"/>. They were called Width and Height originally, which read like a
    /// size while behaving like a right and bottom edge - the sort of mismatch that
    /// produces a rectangle three times too large and no obvious cause.
    ///
    /// On the wire it is a four-element array: x1, y1, x2, y2.
    /// </summary>
    public struct Rect
    {
        public float X;
        public float Y;
        public float X2;
        public float Y2;

        public static Rect FullScreen
        {
            get { return new Rect { X = 0f, Y = 0f, X2 = 800f, Y2 = 600f }; }
        }

        public Rect(float x, float y, float x2, float y2)
        {
            X = x;
            Y = y;
            X2 = x2;
            Y2 = y2;
        }

        public float Width
        {
            get { return X2 - X; }
        }

        public float Height
        {
            get { return Y2 - Y; }
        }

        public JsonValue ToJson()
        {
            return JsonValue.NewArray().Add(X).Add(Y).Add(X2).Add(Y2);
        }

        public static Rect FromJson(JsonValue value, Rect fallback)
        {
            if (value.Type != JsonValue.Kind.Array || value.Count < 4) return fallback;

            return new Rect(value[0].AsFloat(fallback.X), value[1].AsFloat(fallback.Y),
                value[2].AsFloat(fallback.X2), value[3].AsFloat(fallback.Y2));
        }
    }

    // -------------------------------------------------------------------- clear

    /// <summary>Clears the frame buffer, the depth buffer, or both.</summary>
    public sealed class ClearEffect : Effect
    {
        public ColorRgba Colour = ColorRgba.Black;
        public bool ClearScreen = true;
        public bool ClearDepth = true;

        public override string TypeName
        {
            get { return "clear"; }
        }

        public override void Render(EffectContext context)
        {
            context.Device.Clear(Colour.ToD3DColor(), ClearScreen, ClearDepth, false);
        }

        public override void Write(JsonValue json)
        {
            base.Write(json);
            json.Set("colour", Colour.ToHex());
            json.Set("screen", ClearScreen);
            json.Set("depth", ClearDepth);
        }

        public override void Read(JsonValue json)
        {
            base.Read(json);
            Colour = ColorRgba.Parse(json["colour"].AsString("#000000"));
            ClearScreen = json["screen"].AsBool(true);
            ClearDepth = json["depth"].AsBool(true);
        }
    }

    // -------------------------------------------------------------------- layer2d

    /// <summary>A coloured or textured rectangle drawn straight into the frame.</summary>
    public sealed class Layer2DEffect : Effect
    {
        public ColorRgba StartColour = ColorRgba.White;
        public ColorRgba EndColour = ColorRgba.White;
        public BlendMode SourceBlend = BlendMode.SourceAlpha;
        public BlendMode DestinationBlend = BlendMode.InverseSourceAlpha;
        public int TextureId = -1;

        public override string TypeName
        {
            get { return "layer2d"; }
        }

        public override void Render(EffectContext context)
        {
            if (!context.Quads.IsReady) context.Quads.Initialize();

            var colour = LerpColour(StartColour, EndColour, Progress(context.Frame));

            D3D9Texture texture = null;

            if (TextureId >= 0)
            {
                var projectTexture = context.Project.FindTexture(TextureId);

                if (projectTexture != null)
                {
                    texture = context.Renderer.UploadTexture(TextureId, projectTexture.Width,
                        projectTexture.Height, projectTexture.Pixels);
                }
            }

            context.Quads.BeginScreenSpace(context.Width, context.Height);
            context.Quads.Draw(texture, context.X1, context.Y1,
                context.X2 - context.X1, context.Y2 - context.Y1,
                colour, SourceBlend, DestinationBlend);
        }

        public override void Write(JsonValue json)
        {
            base.Write(json);
            json.Set("startColour", StartColour.ToHex());
            json.Set("endColour", EndColour.ToHex());
            json.Set("sourceBlend", BlendName(SourceBlend));
            json.Set("destinationBlend", BlendName(DestinationBlend));
            json.Set("texture", TextureId);
        }

        public override void Read(JsonValue json)
        {
            base.Read(json);
            StartColour = ColorRgba.Parse(json["startColour"].AsString("#FFFFFFFF"));
            EndColour = ColorRgba.Parse(json["endColour"].AsString("#FFFFFFFF"));
            SourceBlend = ParseBlend(json["sourceBlend"], BlendMode.SourceAlpha);
            DestinationBlend = ParseBlend(json["destinationBlend"], BlendMode.InverseSourceAlpha);
            TextureId = json["texture"].AsInt(-1);
        }
    }

    // -------------------------------------------------------------------- display3d

    /// <summary>Renders a scene through a camera, inside the effect's rectangle.</summary>
    public sealed class Display3DEffect : Effect
    {
        public int SceneId;
        public int CameraIndex;
        public int AnimationId = -1;

        public float CameraStart;
        public float CameraEnd;
        public float AnimationStart;
        public float AnimationEnd;

        /// <summary>When true, the camera offsets from shake effects are not stored back.</summary>
        public bool DontSaveCamera;

        public override string TypeName
        {
            get { return "display3d"; }
        }

        public override void Render(EffectContext context)
        {
            var scene = context.Project.FindScene(SceneId);

            if (scene == null)
            {
                // A project that references a deleted scene should still play the
                // rest of its timeline, so this is a warning rather than a throw.
                EngineLog.Warn("timeline", "Display3D references missing scene " + SceneId + ".");
                return;
            }

            var progress = Progress(context.Frame);

            var cameraTime = CameraStart + (CameraEnd - CameraStart) * progress;
            var animationTime = AnimationStart + (AnimationEnd - AnimationStart) * progress;

            // Camera animation, when the scene has one.
            var camera = scene.Camera;

            if (scene.CameraTracks.Count > 0)
            {
                var animated = scene.SampleCamera(cameraTime);
                camera.Position = animated.Position + context.EyeShake;
                camera.Target = animated.Target + context.TargetShake;
                camera.FieldOfView = animated.FieldOfView;
                camera.Roll = animated.Roll;
            }
            else
            {
                camera.Position += context.EyeShake;
                camera.Target += context.TargetShake;
            }

            if (!DontSaveCamera)
            {
                context.EyeShake = Vector3.Zero;
                context.TargetShake = Vector3.Zero;
            }

            if (AnimationId >= 0 && scene.HasAnimation(AnimationId))
            {
                scene.ApplyAnimation(AnimationId, animationTime);
            }

            var width = context.X2 - context.X1;
            var height = context.Y2 - context.Y1;

            if (width <= 0 || height <= 0) return;

            // Aspect comes from the rectangle, not the window: a 3D view squeezed
            // into a wide band must not stretch.
            var aspect = (float)width / height;

            context.Device.SetViewport(context.X1, context.Y1, width, height);
            context.Device.SetScissorRectangle(context.X1, context.Y1, width, height);
            context.Device.SetRenderState(D3D9.RenderState.ScissorTestEnable, 1u);

            context.Renderer.RenderWithCamera(scene, camera, aspect);

            context.Device.SetRenderState(D3D9.RenderState.ScissorTestEnable, 0u);
            context.Device.SetViewport(0, 0, context.Width, context.Height);
        }

        public override void Write(JsonValue json)
        {
            base.Write(json);
            json.Set("scene", SceneId);
            json.Set("camera", CameraIndex);
            json.Set("animation", AnimationId);
            json.Set("cameraStart", CameraStart);
            json.Set("cameraEnd", CameraEnd);
            json.Set("animationStart", AnimationStart);
            json.Set("animationEnd", AnimationEnd);
            json.Set("dontSaveCamera", DontSaveCamera);
        }

        public override void Read(JsonValue json)
        {
            base.Read(json);
            SceneId = json["scene"].AsInt(0);
            CameraIndex = json["camera"].AsInt(0);
            AnimationId = json["animation"].AsInt(-1);
            CameraStart = json["cameraStart"].AsFloat(0f);
            CameraEnd = json["cameraEnd"].AsFloat(1f);
            AnimationStart = json["animationStart"].AsFloat(0f);
            AnimationEnd = json["animationEnd"].AsFloat(1f);
            DontSaveCamera = json["dontSaveCamera"].AsBool(false);
        }
    }

    // -------------------------------------------------------------------- render to texture

    /// <summary>Captures the frame into an off-screen slot for a later effect.</summary>
    public sealed class RenderToTextureEffect : Effect
    {
        public int Slot;

        public override string TypeName
        {
            get { return "rendertotexture"; }
        }

        public override void Render(EffectContext context)
        {
            if (context.Slots == null || Slot < 0 || Slot >= context.Slots.Length) return;

            var target = context.Slots[Slot];

            if (target == null) return;

            var width = Math.Min(target.Width, context.X2 - context.X1);
            var height = Math.Min(target.Height, context.Y2 - context.Y1);

            if (width <= 0 || height <= 0) return;

            context.Device.CopyRenderTarget(context.FrameTarget, context.X1, context.Y1,
                width, height, target);
        }

        public override void Write(JsonValue json)
        {
            base.Write(json);
            json.Set("slot", Slot);
        }

        public override void Read(JsonValue json)
        {
            base.Read(json);
            Slot = json["slot"].AsInt(0);
        }
    }

    // -------------------------------------------------------------------- blur

    /// <summary>
    /// A layered blur.
    ///
    /// Each layer blurs the previous layer's result and composites it back, so
    /// the cost is linear in the layer count while the visual result is a wide,
    /// smooth blur - which is why the original used this rather than a real
    /// separable gaussian.
    /// </summary>
    public sealed class BlurEffect : Effect
    {
        public ColorRgba StartColour = new ColorRgba(255, 255, 255, 204);
        public ColorRgba EndColour = new ColorRgba(255, 255, 255, 204);
        public BlendMode SourceBlend = BlendMode.One;
        public BlendMode DestinationBlend = BlendMode.One;
        public int Slot;
        public int Layers = 4;
        public int Zoom = 0;

        public override string TypeName
        {
            get { return "blur"; }
        }

        public override void Render(EffectContext context)
        {
            if (context.Slots == null || Slot < 0 || Slot >= context.Slots.Length) return;

            var target = context.Slots[Slot];

            if (target == null || Layers <= 0) return;

            var progress = Progress(context.Frame);
            var colour = LerpColour(StartColour, EndColour, progress);

            // The alpha ramp is what makes the blur fade in rather than snap.
            colour.A = (byte)(colour.A * 0.8f);

            var width = Math.Min(target.Width, context.X2 - context.X1);
            var height = Math.Min(target.Height, context.Y2 - context.Y1);

            if (width <= 0 || height <= 0) return;

            context.Device.CopyRenderTarget(context.FrameTarget, context.X1, context.Y1,
                width, height, target);

            if (!context.Quads.IsReady) context.Quads.Initialize();

            context.Quads.BeginScreenSpace(context.Width, context.Height);

            for (var layer = 0; layer < Layers; layer++)
            {
                var step = (Zoom / 255f) / 100f * layer;

                context.Quads.SetEffectParameters(new Vector4(step, 0f, 0f, 0f), Vector4.Zero);
                context.Quads.Draw(target.Texture, context.X1, context.Y1, width, height,
                    colour, SourceBlend, DestinationBlend, QuadShader.Blur, Matrix.Identity);

                context.Device.CopyRenderTarget(context.FrameTarget, context.X1, context.Y1,
                width, height, target);
            }
        }

        public override void Write(JsonValue json)
        {
            base.Write(json);
            json.Set("startColour", StartColour.ToHex());
            json.Set("endColour", EndColour.ToHex());
            json.Set("sourceBlend", BlendName(SourceBlend));
            json.Set("destinationBlend", BlendName(DestinationBlend));
            json.Set("slot", Slot);
            json.Set("layers", Layers);
            json.Set("zoom", Zoom);
        }

        public override void Read(JsonValue json)
        {
            base.Read(json);
            StartColour = ColorRgba.Parse(json["startColour"].AsString("#CCFFFFFF"));
            EndColour = ColorRgba.Parse(json["endColour"].AsString("#CCFFFFFF"));
            SourceBlend = ParseBlend(json["sourceBlend"], BlendMode.One);
            DestinationBlend = ParseBlend(json["destinationBlend"], BlendMode.One);
            Slot = json["slot"].AsInt(0);
            Layers = json["layers"].AsInt(4);
            Zoom = json["zoom"].AsInt(0);
        }
    }

    // -------------------------------------------------------------------- feedback

    /// <summary>
    /// Feeds the frame back into itself at a slightly different scale.
    ///
    /// This is the engine's signature effect: each layer samples the previous
    /// frame slightly zoomed, so the result smears outward from the centre and
    /// leaves trails behind anything that moves.
    /// </summary>
    public sealed class FeedbackEffect : Effect
    {
        public ColorRgba StartColour = new ColorRgba(255, 255, 255, 230);
        public ColorRgba EndColour = new ColorRgba(255, 255, 255, 230);
        public BlendMode SourceBlend = BlendMode.One;
        public BlendMode DestinationBlend = BlendMode.One;
        public int Slot;
        public int Layers = 6;
        public int Zoom = 8;

        public override string TypeName
        {
            get { return "feedback"; }
        }

        public override void Render(EffectContext context)
        {
            if (context.Slots == null || Slot < 0 || Slot >= context.Slots.Length) return;

            var target = context.Slots[Slot];

            if (target == null || Layers <= 0) return;

            var progress = Progress(context.Frame);
            var colour = LerpColour(StartColour, EndColour, progress);

            var width = Math.Min(target.Width, context.X2 - context.X1);
            var height = Math.Min(target.Height, context.Y2 - context.Y1);

            if (width <= 0 || height <= 0) return;

            context.Device.CopyRenderTarget(context.FrameTarget, context.X1, context.Y1,
                width, height, target);

            if (!context.Quads.IsReady) context.Quads.Initialize();

            context.Quads.BeginScreenSpace(context.Width, context.Height);

            for (var layer = 0; layer < Layers; layer++)
            {
                var zoom = (Zoom / 255f) / 100f * layer;

                // A texture matrix that shrinks the sampled region around the
                // centre: this is what makes the trail expand outward.
                var textureMatrix = Matrix.Identity;
                textureMatrix.M11 = 1f - zoom;
                textureMatrix.M22 = 1f - zoom;
                textureMatrix.M41 = zoom * 0.5f;
                textureMatrix.M42 = zoom * 0.5f;

                context.Quads.Draw(target.Texture, context.X1, context.Y1, width, height,
                    colour, SourceBlend, DestinationBlend, QuadShader.Feedback, textureMatrix);

                context.Device.CopyRenderTarget(context.FrameTarget, context.X1, context.Y1,
                width, height, target);
            }
        }

        public override void Write(JsonValue json)
        {
            base.Write(json);
            json.Set("startColour", StartColour.ToHex());
            json.Set("endColour", EndColour.ToHex());
            json.Set("sourceBlend", BlendName(SourceBlend));
            json.Set("destinationBlend", BlendName(DestinationBlend));
            json.Set("slot", Slot);
            json.Set("layers", Layers);
            json.Set("zoom", Zoom);
        }

        public override void Read(JsonValue json)
        {
            base.Read(json);
            StartColour = ColorRgba.Parse(json["startColour"].AsString("#E6FFFFFF"));
            EndColour = ColorRgba.Parse(json["endColour"].AsString("#E6FFFFFF"));
            SourceBlend = ParseBlend(json["sourceBlend"], BlendMode.One);
            DestinationBlend = ParseBlend(json["destinationBlend"], BlendMode.One);
            Slot = json["slot"].AsInt(0);
            Layers = json["layers"].AsInt(6);
            Zoom = json["zoom"].AsInt(8);
        }
    }

    // -------------------------------------------------------------------- contrast

    /// <summary>Pushes the frame's values away from mid-grey.</summary>
    public sealed class ContrastEffect : Effect
    {
        public ColorRgba StartColour = ColorRgba.White;
        public ColorRgba EndColour = ColorRgba.White;
        public BlendMode SourceBlend = BlendMode.SourceAlpha;
        public BlendMode DestinationBlend = BlendMode.InverseSourceAlpha;
        public int Slot;

        /// <summary>Multiplier, biased so that 0 means "no change".</summary>
        public int Multiply;

        /// <summary>Additive offset, biased the same way.</summary>
        public int Add;

        public override string TypeName
        {
            get { return "contrast"; }
        }

        public override void Render(EffectContext context)
        {
            if (context.Slots == null || Slot < 0 || Slot >= context.Slots.Length) return;

            var target = context.Slots[Slot];

            if (target == null) return;

            var progress = Progress(context.Frame);
            var colour = LerpColour(StartColour, EndColour, progress);

            var width = Math.Min(target.Width, context.X2 - context.X1);
            var height = Math.Min(target.Height, context.Y2 - context.Y1);

            if (width <= 0 || height <= 0) return;

            if (!context.Quads.IsReady) context.Quads.Initialize();

            context.Quads.BeginScreenSpace(context.Width, context.Height);

            // The file stores 0 as "unchanged", so the shader receives 1. The
            // multiply is scaled down because a raw 0..255 multiplier applied to
            // an 0..1 colour would saturate immediately.
            context.Quads.SetEffectParameters(
                new Vector4(1f + Multiply / 64f, 1f + Add / 255f, 0f, 0f), Vector4.Zero);

            context.Quads.Draw(target.Texture, context.X1, context.Y1, width, height,
                colour, SourceBlend, DestinationBlend, QuadShader.Contrast, Matrix.Identity);
        }

        public override void Write(JsonValue json)
        {
            base.Write(json);
            json.Set("startColour", StartColour.ToHex());
            json.Set("endColour", EndColour.ToHex());
            json.Set("sourceBlend", BlendName(SourceBlend));
            json.Set("destinationBlend", BlendName(DestinationBlend));
            json.Set("slot", Slot);
            json.Set("multiply", Multiply);
            json.Set("add", Add);
        }

        public override void Read(JsonValue json)
        {
            base.Read(json);
            StartColour = ColorRgba.Parse(json["startColour"].AsString("#FFFFFFFF"));
            EndColour = ColorRgba.Parse(json["endColour"].AsString("#FFFFFFFF"));
            SourceBlend = ParseBlend(json["sourceBlend"], BlendMode.SourceAlpha);
            DestinationBlend = ParseBlend(json["destinationBlend"], BlendMode.InverseSourceAlpha);
            Slot = json["slot"].AsInt(0);
            Multiply = json["multiply"].AsInt(0);
            Add = json["add"].AsInt(0);
        }
    }

    // -------------------------------------------------------------------- shake

    /// <summary>
    /// Offsets the camera for the following 3D effects.
    ///
    /// The offset is recomputed at a fixed frequency from a seeded random, so the
    /// shake is deterministic: the same frame always produces the same offset,
    /// which matters when the timeline is scrubbed in the editor.
    /// </summary>
    public sealed class CameraShakeEffect : Effect
    {
        public float EyeAmount = 0.1f;
        public float TargetAmount = 0.1f;
        public float Start = 1f;
        public float End = 1f;
        public int Frequency = 5;

        public override string TypeName
        {
            get { return "camerashake"; }
        }

        public override void Render(EffectContext context)
        {
            var progress = Progress(context.Frame);
            var strength = Start + (End - Start) * progress;

            var frequency = Math.Max(1, Frequency);
            var tick = context.Frame / (frequency * 10);
            var phase = (context.Frame - tick * frequency * 10) / (float)(frequency * 10);

            // Two seeded samples, interpolated: continuous motion, deterministic
            // per frame, and no state carried between frames.
            var current = SeededOffset(tick, strength);
            var next = SeededOffset(tick + 1, strength);

            context.EyeShake = Vector3.Lerp(current.Eye, next.Eye, phase) * EyeAmount;
            context.TargetShake = Vector3.Lerp(current.Target, next.Target, phase) * TargetAmount;
        }

        private struct Offset
        {
            internal Vector3 Eye;
            internal Vector3 Target;
        }

        private static Offset SeededOffset(int seed, float strength)
        {
            // A small deterministic generator rather than System.Random: the
            // sequence must not depend on the runtime version, or a project would
            // shake differently on a different machine.
            var state = (uint)(seed * 2654435761u) | 1u;

            var offset = new Offset();

            offset.Eye = new Vector3(NextFloat(ref state), NextFloat(ref state), NextFloat(ref state)) * strength;
            offset.Target = new Vector3(NextFloat(ref state), NextFloat(ref state), NextFloat(ref state)) * strength;

            return offset;
        }

        private static float NextFloat(ref uint state)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;

            return (state & 0xFFFF) / 65535f - 0.5f;
        }

        public override void Write(JsonValue json)
        {
            base.Write(json);
            json.Set("eye", EyeAmount);
            json.Set("target", TargetAmount);
            json.Set("shakeStart", Start);
            json.Set("shakeEnd", End);
            json.Set("frequency", Frequency);
        }

        public override void Read(JsonValue json)
        {
            base.Read(json);
            EyeAmount = json["eye"].AsFloat(0.1f);
            TargetAmount = json["target"].AsFloat(0.1f);
            Start = json["shakeStart"].AsFloat(1f);
            End = json["shakeEnd"].AsFloat(1f);
            Frequency = json["frequency"].AsInt(5);
        }
    }

    // -------------------------------------------------------------------- particles

    /// <summary>Advances a scene's particle emitters for the current frame.</summary>
    public sealed class ParticleEffect : Effect
    {
        public int SceneId;
        public int AnimationId;
        public float AnimationStart;
        public float AnimationEnd;

        public override string TypeName
        {
            get { return "particles"; }
        }

        public override bool Draws
        {
            get { return false; }
        }

        public override void Render(EffectContext context)
        {
            var scene = context.Project.FindScene(SceneId);

            if (scene == null) return;

            var progress = Progress(context.Frame);
            var time = AnimationStart + (AnimationEnd - AnimationStart) * progress;

            if (AnimationId >= 0 && scene.HasAnimation(AnimationId))
            {
                scene.ApplyAnimation(AnimationId, time);
            }

            // Particle simulation runs on the frame number, not the frame time:
            // the original stepped emitters on a fixed 10 ms tick and the look of
            // an existing effect depends on that cadence.
            var deltaFrames = context.Frame - (int)context.TimePosition;

            scene.UpdateParticles(deltaFrames > 0 ? deltaFrames : 1);
        }

        public override void Write(JsonValue json)
        {
            base.Write(json);
            json.Set("scene", SceneId);
            json.Set("animation", AnimationId);
            json.Set("animationStart", AnimationStart);
            json.Set("animationEnd", AnimationEnd);
        }

        public override void Read(JsonValue json)
        {
            base.Read(json);
            SceneId = json["scene"].AsInt(0);
            AnimationId = json["animation"].AsInt(0);
            AnimationStart = json["animationStart"].AsFloat(0f);
            AnimationEnd = json["animationEnd"].AsFloat(1f);
        }
    }
}
