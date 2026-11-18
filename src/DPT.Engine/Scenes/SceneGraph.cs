//
// DPT.Engine - materials, scene nodes, scenes.
// Copyright (C) 2026 DartPower Team. GPLv2.
//

using System;
using System.Collections.Generic;

using DPT.Engine.Mathematics;

namespace DPT.Engine.Scenes
{
    /// <summary>
    /// Blend factors, in the engine's own vocabulary.
    ///
    /// The names are the ones the original project format used, which came from
    /// OpenGL. They are mapped onto D3DBLEND at draw time; keeping the GL names
    /// means existing projects keep their look, and "SourceAlpha" reads the same
    /// in the editor as it did in the original tool.
    /// </summary>
    public enum BlendMode
    {
        Zero = 0,
        One = 1,
        SourceColour = 2,
        InverseSourceColour = 3,
        SourceAlpha = 4,
        InverseSourceAlpha = 5,
        DestinationAlpha = 6,
        InverseDestinationAlpha = 7,
        DestinationColour = 8,
        InverseDestinationColour = 9,
        SourceAlphaSaturation = 10
    }

    /// <summary>How a surface responds to light.</summary>
    public enum ShadingMode
    {
        /// <summary>Lighting disabled: texture and colour are the final pixel.</summary>
        Unlit = 0,

        /// <summary>One normal per face. Faceted, and the cheapest.</summary>
        Flat = 1,

        /// <summary>Interpolated vertex normals. Smooth.</summary>
        Gouraud = 2
    }

    /// <summary>
    /// How a mesh is drawn.
    ///
    /// A plain data object: the editor writes these fields from JSON and the
    /// renderer reads them. Every property has a default that renders something
    /// visible, so a hand-written project with a missing field still shows up
    /// instead of silently disappearing.
    /// </summary>
    public sealed class Material
    {
        /// <summary>Index into the project's textures, or -1 for an untextured surface.</summary>
        public int TextureIndex = -1;

        /// <summary>Second texture for environment mapping, or -1.</summary>
        public int EnvironmentTextureIndex = -1;

        public ColorRgba Colour = ColorRgba.White;

        public BlendMode SourceBlend = BlendMode.SourceAlpha;
        public BlendMode DestinationBlend = BlendMode.InverseSourceAlpha;

        public ShadingMode Shading = ShadingMode.Gouraud;

        public bool Wireframe;
        public bool TwoSided;

        /// <summary>Write depth. Off for additive glows that must not occlude.</summary>
        public bool DepthWrite = true;

        /// <summary>Test depth. Off for overlays drawn over everything.</summary>
        public bool DepthTest = true;

        /// <summary>Draw in the transparent pass, after all opaque geometry.</summary>
        public bool Transparent;

        public Material Clone()
        {
            return (Material)MemberwiseClone();
        }
    }

    /// <summary>
    /// One animated property.
    ///
    /// Tracks are keyed by name rather than by an enum so a project can animate a
    /// property the engine does not know about, and only the consumer that
    /// understands it will act on it.
    /// </summary>
    public sealed class AnimationTrack
    {
        public string Property = "";
        public Timeline.Spline Curve = new Timeline.Spline();

        public float Sample(float time)
        {
            return Curve.Sample(time);
        }
    }

    /// <summary>A named set of tracks: one animation a scene can be posed into.</summary>
    public sealed class SceneAnimation
    {
        public int Id;
        public string Name = "";
        public readonly List<AnimationTrack> Tracks = new List<AnimationTrack>();

        public AnimationTrack Find(string property)
        {
            for (var i = 0; i < Tracks.Count; i++)
            {
                if (string.Equals(Tracks[i].Property, property, StringComparison.OrdinalIgnoreCase))
                {
                    return Tracks[i];
                }
            }

            return null;
        }
    }

    /// <summary>
    /// A node in the scene graph.
    ///
    /// The transform is decomposed into position, rotation and scale rather than
    /// stored as a matrix, because the editor's gizmos and the animation system
    /// both work on the parts; <see cref="LocalMatrix"/> rebuilds the matrix once
    /// per frame.
    ///
    /// Children are ids, not references, so a project can be loaded in any order
    /// and a parent may be defined after its child.
    /// </summary>
    public sealed class SceneNode
    {
        public int Id;
        public int ParentId = -1;
        public string Name = "";

        public Mesh Mesh;
        public Material Material = new Material();
        public ParticleEmitter Emitter;

        public Vector3 Position = Vector3.Zero;
        public Quaternion Rotation = Quaternion.Identity;
        public Vector3 Scale = Vector3.One;

        public bool Visible = true;

        /// <summary>Child ids, in draw order.</summary>
        public readonly List<int> Children = new List<int>();

        /// <summary>World matrix for the current frame, filled in by the renderer.</summary>
        public Matrix World = Matrix.Identity;

        /// <summary>
        /// The node's own transform, without its parent's.
        ///
        /// Scale, then rotate, then translate - the order the editor's numeric
        /// fields imply and the order the original engine composed in.
        /// </summary>
        public Matrix LocalMatrix()
        {
            var scale = Matrix.CreateScale(Scale);
            var rotation = Matrix.CreateFromQuaternion(Rotation);
            var translation = Matrix.CreateTranslation(Position);

            return Matrix.Multiply(Matrix.Multiply(scale, rotation), translation);
        }

        /// <summary>Rejects a rotation that has drifted away from unit length.</summary>
        public void NormalizeRotation()
        {
            if (Rotation.LengthSquared < 1e-8f || !MathHelper.IsFinite(Rotation.LengthSquared))
            {
                Rotation = Quaternion.Identity;
                return;
            }

            Rotation.Normalize();
        }
    }

    /// <summary>A camera: an eye, a target, and a field of view.</summary>
    public sealed class Camera
    {
        public Vector3 Position = new Vector3(0f, 0f, -10f);
        public Vector3 Target = Vector3.Zero;
        public Vector3 Up = Vector3.Up;

        /// <summary>Vertical field of view in degrees. 45 is the engine's default.</summary>
        public float FieldOfView = 45f;

        /// <summary>Roll around the view axis, in degrees.</summary>
        public float Roll;

        public float NearPlane = 0.1f;
        public float FarPlane = 2000f;

        /// <summary>Builds the view matrix, including roll.</summary>
        public Matrix CreateView()
        {
            var up = Up;

            if (Math.Abs(Roll) > 0.0001f)
            {
                var forward = Target - Position;

                if (forward.LengthSquared > 1e-8f)
                {
                    forward.Normalize();
                    var rotation = Matrix.CreateFromAxisAngle(forward, MathHelper.ToRadians(Roll));
                    up = Vector3.TransformNormal(up, rotation);
                }
            }

            return Matrix.CreateLookAt(Position, Target, up);
        }

        public Matrix CreateProjection(float aspectRatio)
        {
            var fieldOfView = MathHelper.ToRadians(MathHelper.Clamp(FieldOfView, 1f, 179f));

            if (aspectRatio <= 0f || !MathHelper.IsFinite(aspectRatio)) aspectRatio = 1f;

            return Matrix.CreatePerspectiveFieldOfView(fieldOfView, aspectRatio,
                NearPlane, Math.Max(FarPlane, NearPlane + 1f));
        }

        public Vector3 Forward
        {
            get
            {
                var forward = Target - Position;

                if (forward.LengthSquared < 1e-8f) return Vector3.Forward;

                forward.Normalize();
                return forward;
            }
        }

        public Camera Clone()
        {
            return (Camera)MemberwiseClone();
        }
    }

    /// <summary>Fog settings for a scene.</summary>
    public struct FogSettings
    {
        public bool Enabled;
        public ColorRgba Colour;
        public float Start;
        public float End;

        public static FogSettings Disabled
        {
            get { return new FogSettings { Enabled = false, Colour = ColorRgba.Black }; }
        }
    }

    /// <summary>
    /// A drawable scene: a set of nodes, a camera and an environment.
    ///
    /// Nodes live in one list and reference each other by id, so the renderer can
    /// walk the tree without chasing pointers and the editor can reorder the list
    /// freely.
    /// </summary>
    public sealed class Scene
    {
        public int Id;
        public string Name = "";

        public readonly List<SceneNode> Nodes = new List<SceneNode>();
        public readonly List<int> RootNodes = new List<int>();

        public readonly List<SceneAnimation> Animations = new List<SceneAnimation>();
        public readonly List<AnimationTrack> CameraTracks = new List<AnimationTrack>();

        public Camera Camera = new Camera();
        public FogSettings Fog = FogSettings.Disabled;

        /// <summary>Ambient light colour, applied to lit materials.</summary>
        public ColorRgba Ambient = new ColorRgba(255, 255, 255, 255);

        /// <summary>Direction the key light comes from, in world space.</summary>
        public Vector3 LightDirection = new Vector3(0.4f, -0.7f, 0.6f);

        /// <summary>True when any node has a particle emitter.</summary>
        public bool HasParticles
        {
            get
            {
                for (var i = 0; i < Nodes.Count; i++)
                {
                    if (Nodes[i].Emitter != null) return true;
                }

                return false;
            }
        }

        public SceneNode Find(int id)
        {
            for (var i = 0; i < Nodes.Count; i++)
            {
                if (Nodes[i].Id == id) return Nodes[i];
            }

            return null;
        }

        public SceneAnimation FindAnimation(int id)
        {
            for (var i = 0; i < Animations.Count; i++)
            {
                if (Animations[i].Id == id) return Animations[i];
            }

            return null;
        }

        public bool HasAnimation(int id)
        {
            return FindAnimation(id) != null;
        }

        /// <summary>
        /// Rebuilds <see cref="Children"/> and <see cref="RootNodes"/> from the
        /// parent ids.
        ///
        /// Called once after loading. The parent ids are the single source of
        /// truth, so a file cannot describe a tree the renderer disagrees with.
        /// </summary>
        public void RebuildHierarchy()
        {
            RootNodes.Clear();

            for (var i = 0; i < Nodes.Count; i++)
            {
                Nodes[i].Children.Clear();
            }

            for (var i = 0; i < Nodes.Count; i++)
            {
                var node = Nodes[i];

                if (node.ParentId < 0)
                {
                    RootNodes.Add(node.Id);
                    continue;
                }

                var parent = Find(node.ParentId);

                if (parent == null)
                {
                    // A dangling parent is a broken file, not a fatal error: the
                    // node becomes a root so it stays visible and the editor can
                    // show it as an orphan.
                    node.ParentId = -1;
                    RootNodes.Add(node.Id);
                    continue;
                }

                parent.Children.Add(node.Id);
            }
        }

        /// <summary>Computes world matrices for the whole tree.</summary>
        public void UpdateTransforms()
        {
            for (var i = 0; i < RootNodes.Count; i++)
            {
                UpdateTransform(Find(RootNodes[i]), Matrix.Identity);
            }
        }

        private void UpdateTransform(SceneNode node, Matrix parentWorld)
        {
            if (node == null) return;

            node.World = Matrix.Multiply(node.LocalMatrix(), parentWorld);

            for (var i = 0; i < node.Children.Count; i++)
            {
                UpdateTransform(Find(node.Children[i]), node.World);
            }
        }

        /// <summary>
        /// Poses the scene into an animation at <paramref name="time"/> seconds.
        ///
        /// Tracks that a given node does not have are simply skipped, so an
        /// animation can drive only the objects it cares about.
        /// </summary>
        public void ApplyAnimation(int animationId, float time)
        {
            var animation = FindAnimation(animationId);

            if (animation == null) return;

            for (var i = 0; i < animation.Tracks.Count; i++)
            {
                var track = animation.Tracks[i];

                // Track properties are "nodeId.property", so one animation can
                // drive every object without a per-node list.
                var dot = track.Property.IndexOf('.');

                if (dot <= 0) continue;

                int nodeId;

                if (!int.TryParse(track.Property.Substring(0, dot), out nodeId)) continue;

                var node = Find(nodeId);

                if (node == null) continue;

                ApplyToNode(node, track.Property.Substring(dot + 1), track.Sample(time));
            }

            // Particle emitters follow the nodes they are attached to.
            UpdateTransforms();
        }

        private static void ApplyToNode(SceneNode node, string property, float value)
        {
            switch (property)
            {
                case "x": node.Position.X = value; break;
                case "y": node.Position.Y = value; break;
                case "z": node.Position.Z = value; break;

                case "sx": node.Scale.X = value; break;
                case "sy": node.Scale.Y = value; break;
                case "sz": node.Scale.Z = value; break;

                case "rx": node.Rotation.X = value; break;
                case "ry": node.Rotation.Y = value; break;
                case "rz": node.Rotation.Z = value; break;
                case "rw": node.Rotation.W = value; break;

                case "r": node.Material.Colour.R = ToByte(value); break;
                case "g": node.Material.Colour.G = ToByte(value); break;
                case "b": node.Material.Colour.B = ToByte(value); break;
                case "a": node.Material.Colour.A = ToByte(value); break;
            }

            node.NormalizeRotation();
        }

        private static byte ToByte(float value)
        {
            if (!MathHelper.IsFinite(value) || value <= 0f) return 0;
            if (value >= 255f) return 255;
            return (byte)value;
        }

        /// <summary>Samples the camera tracks at a time, falling back to the static camera.</summary>
        public Camera SampleCamera(float time)
        {
            var camera = Camera.Clone();

            for (var i = 0; i < CameraTracks.Count; i++)
            {
                var track = CameraTracks[i];

                if (track.Curve.IsEmpty) continue;

                var value = track.Sample(time);

                switch (track.Property)
                {
                    case "eyeX": camera.Position.X = value; break;
                    case "eyeY": camera.Position.Y = value; break;
                    case "eyeZ": camera.Position.Z = value; break;

                    case "targetX": camera.Target.X = value; break;
                    case "targetY": camera.Target.Y = value; break;
                    case "targetZ": camera.Target.Z = value; break;

                    case "fov": camera.FieldOfView = MathHelper.Clamp(value, 1f, 179f); break;
                    case "roll": camera.Roll = value; break;
                }
            }

            return camera;
        }

        /// <summary>Advances every emitter in the scene by one simulation step.</summary>
        public void UpdateParticles(int steps)
        {
            for (var i = 0; i < Nodes.Count; i++)
            {
                var emitter = Nodes[i].Emitter;

                if (emitter == null) continue;

                emitter.Update(Nodes[i].World, steps);
            }
        }

        /// <summary>Resets every emitter, for a timeline rewind.</summary>
        public void ResetParticles()
        {
            for (var i = 0; i < Nodes.Count; i++)
            {
                if (Nodes[i].Emitter != null) Nodes[i].Emitter.Reset();
            }
        }
    }
}
