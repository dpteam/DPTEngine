//
// DPT.Engine - the built-in template project.
// Copyright (C) 2026 DartPower Team. GPLv2.
//

using System;

using DPT.Engine.Mathematics;
using DPT.Engine.Project;
using DPT.Engine.Scenes;
using DPT.Engine.Timeline;

namespace DPT.Engine.Engine
{
    /// <summary>
    /// The demo the engine starts with when no project is given.
    ///
    /// Built in code rather than shipped as a data file, so a fresh checkout
    /// always has something to run: the first thing anyone does with a new engine
    /// is start it, and a missing-data error at that moment is a bad first
    /// impression. It is also the reference for the file format - everything it
    /// uses can be written by the editor.
    ///
    /// What it demonstrates, in order: a 3D scene with fog, animated objects,
    /// particles, a camera move, a feedback trail and a title overlay. That is one
    /// of each feature the timeline has.
    /// </summary>
    public static class TemplateProject
    {
        public static ProjectFile Create()
        {
            var project = new ProjectFile();

            project.Name = "DPT.Engine Template";
            project.Author = "DartPower Team";
            project.Width = 1024;
            project.Height = 768;
            project.FramesPerSecond = 100;
            project.Duration = 900; // nine seconds

            var textures = CreateTextures(project);
            var scene = CreateScene(textures);

            project.Scenes.Add(scene);

            AddTimeline(project);

            return project;
        }

        // ---------------------------------------------------------------- textures

        private struct TextureIds
        {
            internal int Grid;
            internal int Glow;
        }

        private static TextureIds CreateTextures(ProjectFile project)
        {
            var ids = new TextureIds();

            // A blue grid, matching the brand colour. A grid reads as "space"
            // immediately and shows texture filtering and mip mapping honestly.
            ids.Grid = 1;
            project.Textures.Add(CreateGridTexture(ids.Grid, 128, new ColorRgba(0, 136, 255, 255)));

            // A soft dot for particles and for the glow.
            ids.Glow = 2;
            project.Textures.Add(CreateGlowTexture(ids.Glow, 64));

            return ids;
        }

        private static ProjectTexture CreateGridTexture(int id, int size, ColorRgba line)
        {
            var texture = new ProjectTexture();
            texture.Id = id;
            texture.Width = size;
            texture.Height = size;
            texture.Pixels = new uint[size * size];

            var background = new ColorRgba(10, 16, 30, 255);

            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    // Two-pixel lines: at one pixel the grid disappears as soon as
                    // the texture is tiled and viewed at an angle.
                    var onLine = (x % 16) < 2 || (y % 16) < 2;
                    var onEdge = (x % 16) < 2 && (y % 16) < 2;

                    var colour = onEdge
                        ? new ColorRgba(200, 240, 255, 255)
                        : (onLine ? line : background);

                    texture.Pixels[y * size + x] = colour.ToArgb();
                }
            }

            return texture;
        }

        private static ProjectTexture CreateGlowTexture(int id, int size)
        {
            var texture = new ProjectTexture();
            texture.Id = id;
            texture.Width = size;
            texture.Height = size;
            texture.Pixels = new uint[size * size];

            var centre = (size - 1) * 0.5f;

            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var dx = (x - centre) / centre;
                    var dy = (y - centre) / centre;
                    var distance = (float)Math.Sqrt(dx * dx + dy * dy);

                    var alpha = distance >= 1f ? 0f : 1f - distance;
                    alpha = alpha * alpha * alpha;

                    texture.Pixels[y * size + x] =
                        new ColorRgba(180, 230, 255, (byte)(alpha * 255f)).ToArgb();
                }
            }

            return texture;
        }

        // ---------------------------------------------------------------- scene

        private static Scene CreateScene(TextureIds textures)
        {
            var scene = new Scene();

            scene.Id = 1;
            scene.Name = "corridor";
            scene.Ambient = new ColorRgba(120, 150, 190, 255);
            scene.LightDirection = new Vector3(0.3f, -0.5f, 0.8f);

            scene.Fog.Enabled = true;
            scene.Fog.Colour = new ColorRgba(4, 8, 16, 255);
            scene.Fog.Start = 6f;
            scene.Fog.End = 34f;

            // The camera looks down the corridor from just above head height. The
            // corridor runs away from the camera, so the floor fills the lower half
            // of the frame and the cubes recede toward the horizon.
            scene.Camera.Position = new Vector3(0f, 1.4f, -14f);
            scene.Camera.Target = new Vector3(0f, 0.2f, 6f);
            scene.Camera.FieldOfView = 60f;

            // The floor: one large plane with tiled texture coordinates, so a small
            // grid texture covers sixty world units without the lines thinning out.
            var floorMaterial = new Material();
            floorMaterial.TextureIndex = textures.Grid;
            floorMaterial.Shading = ShadingMode.Unlit;
            floorMaterial.SourceBlend = BlendMode.One;
            floorMaterial.DestinationBlend = BlendMode.Zero;
            floorMaterial.TwoSided = true;

            var floorMesh = Mesh.CreatePlane(60f, 60f, 1);
            floorMesh.ScaleTextureCoordinates(20f);

            var floor = new SceneNode();
            floor.Id = 1;
            floor.Name = "floor";
            floor.Mesh = floorMesh;
            floor.Material = floorMaterial;
            floor.Position = new Vector3(0f, -1.6f, 0f);
            scene.Nodes.Add(floor);

            // A receding row of spinning cubes. Each one is a separate node so the
            // editor can select and animate them individually.
            for (var i = 0; i < 9; i++)
            {
                var material = new Material();
                material.Colour = new ColorRgba(90, 190, 255, 255);
                material.Shading = ShadingMode.Unlit;
                material.SourceBlend = BlendMode.SourceAlpha;
                material.DestinationBlend = BlendMode.InverseSourceAlpha;

                var cube = new SceneNode();
                cube.Id = 10 + i;
                cube.Name = "cube" + i;
                cube.Mesh = Mesh.CreateBox(1.4f);
                cube.Material = material;

                var z = i * 3.2f;
                var side = i % 2 == 0 ? -1f : 1f;

                // Alternating sides and a widening spread: a corridor reads better
                // than a single file, and the near cubes stay large on screen.
                cube.Position = new Vector3(side * (2.4f + i * 0.15f), 0.4f + (i % 3) * 0.5f, z);
                cube.Rotation = Quaternion.CreateFromYawPitchRoll(i * 0.4f, i * 0.25f, 0f);
                scene.Nodes.Add(cube);
            }

            // A particle emitter in the middle of the corridor: sparks drifting up.
            var sparkMaterial = new Material();
            sparkMaterial.Shading = ShadingMode.Unlit;

            var sparks = new SceneNode();
            sparks.Id = 30;
            sparks.Name = "sparks";
            sparks.Material = sparkMaterial;
            sparks.Position = new Vector3(0f, -0.6f, 6f);
            sparks.Emitter = new ParticleEmitter();

            sparks.Emitter.Shape = EmitterShape.Disc;
            sparks.Emitter.Radius = 2.2f;
            sparks.Emitter.MaxParticles = 600;
            sparks.Emitter.Rate = 14f;
            sparks.Emitter.Lifetime = 2.4f;
            sparks.Emitter.LifetimeChaos = 0.8f;
            sparks.Emitter.Speed = 2.2f;
            sparks.Emitter.SpeedChaos = 1.4f;
            sparks.Emitter.Direction = new Vector3(0f, 1f, 0.1f);
            sparks.Emitter.Spread = 18f;
            sparks.Emitter.StartSize = 0.32f;
            sparks.Emitter.EndSize = 0.02f;
            sparks.Emitter.StartColour = new ColorRgba(150, 220, 255, 255);
            sparks.Emitter.EndColour = new ColorRgba(0, 90, 255, 0);
            sparks.Emitter.Gravity = new Vector3(0f, -0.6f, 0f);
            sparks.Emitter.Seed = 20260101;

            scene.Nodes.Add(sparks);

            // Animations: the cubes bob and spin, the camera drifts forward.
            AddCubeAnimations(scene);
            AddCameraAnimation(scene);

            scene.RebuildHierarchy();

            return scene;
        }

        private static void AddCubeAnimations(Scene scene)
        {
            var animation = new SceneAnimation();
            animation.Id = 0;
            animation.Name = "idle";

            for (var i = 0; i < 9; i++)
            {
                var id = 10 + i;
                var phase = i * 0.35f;

                // Vertical bob, two seconds, smooth and looping.
                var bob = new AnimationTrack();
                bob.Property = id + ".y";
                bob.Curve = MakeLoop(new[]
                {
                    new Key { Time = 0f, Value = 0.4f + (i % 3) * 0.5f },
                    new Key { Time = 1f, Value = 1.0f + (i % 3) * 0.5f },
                    new Key { Time = 2f, Value = 0.4f + (i % 3) * 0.5f }
                }, Interpolation.Hermite);

                animation.Tracks.Add(bob);

                // Spin: a full turn every four seconds, offset per cube.
                var spin = new AnimationTrack();
                spin.Property = id + ".ry";
                spin.Curve = MakeLoop(new[]
                {
                    new Key { Time = 0f, Value = phase },
                    new Key { Time = 2f, Value = phase + 1.6f },
                    new Key { Time = 4f, Value = phase + 3.14159f },
                    new Key { Time = 6f, Value = phase + 4.7f },
                    new Key { Time = 8f, Value = phase + 6.28318f }
                }, Interpolation.Linear);

                animation.Tracks.Add(spin);

                // Colour pulse, so the cubes breathe rather than sit still.
                var glow = new AnimationTrack();
                glow.Property = id + ".b";
                glow.Curve = MakeLoop(new[]
                {
                    new Key { Time = 0f, Value = 190f },
                    new Key { Time = 2f, Value = 255f },
                    new Key { Time = 4f, Value = 190f }
                }, Interpolation.Hermite);

                animation.Tracks.Add(glow);
            }

            scene.Animations.Add(animation);
        }

        private static void AddCameraAnimation(Scene scene)
        {
            // A slow forward dolly with a gentle rise: the camera moves through the
            // corridor rather than orbiting it, which keeps the receding cubes
            // readable.
            var eyeZ = new AnimationTrack();
            eyeZ.Property = "eyeZ";
            eyeZ.Curve = Spline.Ramp(-12f, -4f, 9f);
            scene.CameraTracks.Add(eyeZ);

            var eyeY = new AnimationTrack();
            eyeY.Property = "eyeY";
            eyeY.Curve = MakeLoop(new[]
            {
                new Key { Time = 0f, Value = 1.2f },
                new Key { Time = 4.5f, Value = 1.8f },
                new Key { Time = 9f, Value = 1.2f }
            }, Interpolation.Hermite);
            scene.CameraTracks.Add(eyeY);

            var fov = new AnimationTrack();
            fov.Property = "fov";
            fov.Curve = MakeLoop(new[]
            {
                new Key { Time = 0f, Value = 55f },
                new Key { Time = 4.5f, Value = 62f },
                new Key { Time = 9f, Value = 55f }
            }, Interpolation.Hermite);
            scene.CameraTracks.Add(fov);
        }

        /// <summary>
        /// Builds a curve whose last key equals its first, so the animation loops
        /// without a visible jump.
        /// </summary>
        private static Spline MakeLoop(Key[] keys, Interpolation interpolation)
        {
            var spline = new Spline();
            spline.Interpolation = interpolation;
            spline.Loop = true;
            spline.SetKeys(keys);
            return spline;
        }

        // ---------------------------------------------------------------- timeline

        private static void AddTimeline(ProjectFile project)
        {
            var clear = new ClearEffect();
            clear.StartFrame = 0;
            clear.EndFrame = project.Duration;
            clear.Colour = new ColorRgba(3, 6, 12, 255);
            project.Timeline.Add(clear);

            // Capture the empty frame, so the feedback trail has something to
            // smear when the demo starts.
            var capture = new RenderToTextureEffect();
            capture.StartFrame = 0;
            capture.EndFrame = project.Duration;
            capture.Slot = 0;
            project.Timeline.Add(capture);

            // Particles advance before the 3D pass draws them.
            var particles = new ParticleEffect();
            particles.StartFrame = 0;
            particles.EndFrame = project.Duration;
            particles.SceneId = 1;
            particles.AnimationId = 0;
            particles.AnimationStart = 0f;
            particles.AnimationEnd = 9f;
            project.Timeline.Add(particles);

            var display = new Display3DEffect();
            display.StartFrame = 0;
            display.EndFrame = project.Duration;
            display.SceneId = 1;
            display.AnimationId = 0;
            display.CameraStart = 0f;
            display.CameraEnd = 9f;
            display.AnimationStart = 0f;
            display.AnimationEnd = 9f;
            project.Timeline.Add(display);

            // A feedback trail over the second half: the cubes leave light behind
            // them as the camera moves.
            var feedback = new FeedbackEffect();
            feedback.StartFrame = 300;
            feedback.EndFrame = project.Duration;
            feedback.Slot = 0;
            feedback.Layers = 3;
            feedback.Zoom = 10;
            feedback.StartColour = new ColorRgba(255, 255, 255, 90);
            feedback.EndColour = new ColorRgba(255, 255, 255, 40);
            feedback.SourceBlend = BlendMode.One;
            feedback.DestinationBlend = BlendMode.One;
            project.Timeline.Add(feedback);

            // A dark band at the bottom for the title to sit on. Alpha zero, not
            // colour zero: an opaque black rectangle would hide the scene, and an
            // opaque white one - which is what "colour black, alpha defaulted to
            // 255" produced first - hides even more.
            var band = new Layer2DEffect();
            band.StartFrame = 0;
            band.EndFrame = project.Duration;
            band.StartRect = new Rect(0f, 520f, 800f, 80f);
            band.EndRect = band.StartRect;
            band.StartColour = new ColorRgba(0, 0, 0, 0);
            band.EndColour = new ColorRgba(0, 0, 0, 0);
            band.SourceBlend = BlendMode.SourceAlpha;
            band.DestinationBlend = BlendMode.InverseSourceAlpha;
            project.Timeline.Add(band);

            var shake = new CameraShakeEffect();
            shake.StartFrame = 600;
            shake.EndFrame = 640;
            shake.EyeAmount = 0.06f;
            shake.TargetAmount = 0.02f;
            shake.Start = 1f;
            shake.End = 0f;
            shake.Frequency = 8;
            project.Timeline.Add(shake);
        }
    }
}
