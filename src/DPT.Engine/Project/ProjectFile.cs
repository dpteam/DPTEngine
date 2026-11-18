//
// DPT.Engine - the project file.
// Copyright (C) 2026 DartPower Team. GPLv2.
//

using System;
using System.Collections.Generic;
using System.Globalization;

using DPT.Engine.Mathematics;
using DPT.Engine.Scenes;
using DPT.Engine.Shared;
using DPT.Engine.Shared.Diagnostics;
using DPT.Engine.Timeline;

namespace DPT.Engine.Project
{
    /// <summary>
    /// A texture as the project stores it: raw pixels, not a file reference.
    ///
    /// Embedded rather than linked because a project is a single portable file -
    /// a demo entry is one thing to copy, and the master server can hand it out
    /// without resolving a directory tree. The cost is size; a 512x512 texture is
    /// a megabyte of base64, which is acceptable for the sizes this engine uses.
    /// </summary>
    public sealed class ProjectTexture
    {
        public int Id;
        public int Width;
        public int Height;

        /// <summary>ARGB pixels, row-major, top-left origin.</summary>
        public uint[] Pixels = new uint[0];

        public bool IsValid
        {
            get { return Width > 0 && Height > 0 && Pixels.Length >= Width * Height; }
        }
    }

    /// <summary>
    /// A whole project: resolution, textures, scenes and a timeline.
    ///
    /// This is the unit the editor opens, the runtime plays, and the master
    /// server publishes. It is deliberately flat - scenes do not reference each
    /// other, and the timeline addresses them by id - so that any part can be
    /// edited without touching the rest.
    /// </summary>
    public sealed class ProjectFile
    {
        public const int CurrentFormat = 1;

        public int Format = CurrentFormat;
        public string Name = "Untitled";
        public string Author = "";

        /// <summary>Render resolution. The runtime letterboxes to the real window.</summary>
        public int Width = 1024;

        public int Height = 768;

        /// <summary>
        /// Timeline frames per second. The original ran at 100, which is fine
        /// enough that its 10 ms tick is invisible; changing it changes the timing
        /// of every existing project, so it is part of the file.
        /// </summary>
        public int FramesPerSecond = 100;

        /// <summary>Total length in frames.</summary>
        public int Duration = 600;

        public readonly List<ProjectTexture> Textures = new List<ProjectTexture>();
        public readonly List<Scene> Scenes = new List<Scene>();
        public readonly List<Effect> Timeline = new List<Effect>();

        public ProjectTexture FindTexture(int id)
        {
            for (var i = 0; i < Textures.Count; i++)
            {
                if (Textures[i].Id == id) return Textures[i];
            }

            return null;
        }

        public Scene FindScene(int id)
        {
            for (var i = 0; i < Scenes.Count; i++)
            {
                if (Scenes[i].Id == id) return Scenes[i];
            }

            return null;
        }

        public float AspectRatio
        {
            get { return Height > 0 ? (float)Width / Height : 1f; }
        }

        public float DurationSeconds
        {
            get { return FramesPerSecond > 0 ? (float)Duration / FramesPerSecond : 0f; }
        }

        /// <summary>
        /// Validates and repairs a freshly loaded project.
        ///
        /// A project file is user input. Everything that could produce a crash
        /// later - a missing texture, a scene id that does not exist, a negative
        /// duration - is fixed here once, with a log line, instead of being
        /// defended against in every consumer.
        /// </summary>
        public void Normalize()
        {
            if (Width <= 0 || Width > 8192) Width = 1024;
            if (Height <= 0 || Height > 8192) Height = 768;
            if (FramesPerSecond <= 0 || FramesPerSecond > 1000) FramesPerSecond = 100;
            if (Duration <= 0) Duration = FramesPerSecond * 5;

            for (var i = 0; i < Textures.Count; i++)
            {
                var texture = Textures[i];

                if (!texture.IsValid)
                {
                    EngineLog.Warn("project", "Texture " + texture.Id + " has invalid pixel data and was dropped.");
                    Textures.RemoveAt(i);
                    i--;
                    continue;
                }

                // Textures are uploaded as managed Direct3D textures, which need
                // power-of-two dimensions on older hardware. Rounding up here is
                // cheaper than failing at upload time on the machine that matters.
                if (!IsPowerOfTwo(texture.Width) || !IsPowerOfTwo(texture.Height))
                {
                    EngineLog.Warn("project", "Texture " + texture.Id + " is " + texture.Width + "x" +
                                              texture.Height + "; non-power-of-two sizes may not " +
                                              "render on older hardware.");
                }
            }

            for (var i = 0; i < Scenes.Count; i++)
            {
                Scenes[i].RebuildHierarchy();
            }

            for (var i = 0; i < Timeline.Count; i++)
            {
                var effect = Timeline[i];

                if (effect.EndFrame < effect.StartFrame)
                {
                    var swap = effect.EndFrame;
                    effect.EndFrame = effect.StartFrame;
                    effect.StartFrame = swap;
                }

                if (effect.StartFrame < 0) effect.StartFrame = 0;
                if (effect.EndFrame > Duration) effect.EndFrame = Duration;
            }

            Timeline.Sort(delegate(Effect a, Effect b) { return a.StartFrame.CompareTo(b.StartFrame); });
        }

        private static bool IsPowerOfTwo(int value)
        {
            return value > 0 && (value & (value - 1)) == 0;
        }

        // ---------------------------------------------------------------- helpers

        internal static string Float(float value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        internal static float ParseFloat(JsonValue value, float fallback)
        {
            return value.AsFloat(fallback);
        }

        internal static JsonValue Vector(Vector3 value)
        {
            return JsonValue.NewArray().Add(value.X).Add(value.Y).Add(value.Z);
        }

        internal static Vector3 ParseVector(JsonValue value, Vector3 fallback)
        {
            if (value.Type != JsonValue.Kind.Array || value.Count < 3) return fallback;

            return new Vector3(value[0].AsFloat(fallback.X), value[1].AsFloat(fallback.Y),
                value[2].AsFloat(fallback.Z));
        }

        internal static JsonValue QuaternionValue(Quaternion value)
        {
            return JsonValue.NewArray().Add(value.X).Add(value.Y).Add(value.Z).Add(value.W);
        }

        internal static Quaternion ParseQuaternion(JsonValue value, Quaternion fallback)
        {
            if (value.Type != JsonValue.Kind.Array || value.Count < 4) return fallback;

            var result = new Quaternion(
                value[0].AsFloat(fallback.X), value[1].AsFloat(fallback.Y),
                value[2].AsFloat(fallback.Z), value[3].AsFloat(fallback.W));

            if (result.LengthSquared < 1e-8f) return Quaternion.Identity;

            result.Normalize();
            return result;
        }
    }
}
