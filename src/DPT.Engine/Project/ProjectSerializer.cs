//
// DPT.Engine - project file reading and writing.
// Copyright (C) 2026 DartPower Team. GPLv2.
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

using DPT.Engine.Mathematics;
using DPT.Engine.Scenes;
using DPT.Engine.Shared;
using DPT.Engine.Shared.Diagnostics;
using DPT.Engine.Timeline;

namespace DPT.Engine.Project
{
    /// <summary>
    /// Reads and writes the project file.
    ///
    /// JSON rather than a packed binary format, for three reasons that matter more
    /// than size here:
    ///
    ///   * the editor is a web page, so the same document goes over the wire to
    ///     the browser and back without a translation layer;
    ///   * a project in git produces a readable diff;
    ///   * a hand-edited project still loads, and a broken one names the line.
    ///
    /// Everything that can be wrong with a file is handled by
    /// <see cref="ProjectFile.Normalize"/> after loading, so the loader itself can
    /// stay a straight translation and the repair policy lives in one place.
    /// </summary>
    public static class ProjectSerializer
    {
        // ---------------------------------------------------------------- files

        public static ProjectFile Load(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException("path");

            var text = File.ReadAllText(path, Encoding.UTF8);

            var project = LoadFromText(text);
            EngineLog.Info("project", "Loaded '" + project.Name + "' from " + path + " (" +
                                      project.Scenes.Count + " scenes, " + project.Timeline.Count +
                                      " effects).");

            return project;
        }

        public static void Save(ProjectFile project, string path)
        {
            if (project == null) throw new ArgumentNullException("project");
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException("path");

            PortablePaths.EnsureParent(path);

            var text = ToText(project);

            // Write to a temporary file and move it into place: a crash halfway
            // through a save must not leave the user with a truncated project.
            var temporary = path + ".tmp";

            File.WriteAllText(temporary, text, new UTF8Encoding(false));

            if (File.Exists(path)) File.Delete(path);
            File.Move(temporary, path);

            EngineLog.Info("project", "Saved '" + project.Name + "' to " + path + ".");
        }

        public static ProjectFile LoadFromText(string text)
        {
            if (text == null) throw new ArgumentNullException("text");

            JsonValue root;

            try
            {
                root = JsonValue.Parse(text);
            }
            catch (FormatException error)
            {
                throw new InvalidDataException("The project file is not valid JSON: " + error.Message, error);
            }

            var project = new ProjectFile();

            project.Format = root["format"].AsInt(ProjectFile.CurrentFormat);
            project.Name = root["name"].AsString("Untitled");
            project.Author = root["author"].AsString("");
            project.Width = root["width"].AsInt(1024);
            project.Height = root["height"].AsInt(768);
            project.FramesPerSecond = root["fps"].AsInt(100);
            project.Duration = root["duration"].AsInt(600);

            ReadTextures(project, root.ArrayOf("textures"));
            ReadScenes(project, root.ArrayOf("scenes"));
            ReadTimeline(project, root.ArrayOf("timeline"));

            project.Normalize();

            return project;
        }

        public static string ToText(ProjectFile project)
        {
            if (project == null) throw new ArgumentNullException("project");

            return ToJson(project).ToJson(true);
        }

        public static JsonValue ToJson(ProjectFile project)
        {
            var root = JsonValue.NewObject();

            root.Set("format", ProjectFile.CurrentFormat);
            root.Set("name", project.Name);
            root.Set("author", project.Author);
            root.Set("width", project.Width);
            root.Set("height", project.Height);
            root.Set("fps", project.FramesPerSecond);
            root.Set("duration", project.Duration);

            var textures = JsonValue.NewArray();

            for (var i = 0; i < project.Textures.Count; i++)
            {
                textures.Add(WriteTexture(project.Textures[i]));
            }

            root.Set("textures", textures);

            var scenes = JsonValue.NewArray();

            for (var i = 0; i < project.Scenes.Count; i++)
            {
                scenes.Add(WriteScene(project.Scenes[i]));
            }

            root.Set("scenes", scenes);

            var timeline = JsonValue.NewArray();

            for (var i = 0; i < project.Timeline.Count; i++)
            {
                var effect = JsonValue.NewObject();
                project.Timeline[i].Write(effect);
                timeline.Add(effect);
            }

            root.Set("timeline", timeline);

            return root;
        }

        // ---------------------------------------------------------------- textures

        private static JsonValue WriteTexture(ProjectTexture texture)
        {
            var json = JsonValue.NewObject();

            json.Set("id", texture.Id);
            json.Set("width", texture.Width);
            json.Set("height", texture.Height);

            // Pixels as base64 of the raw ARGB words. A PNG would be smaller, but
            // it would also mean shipping a deflate implementation and a second
            // decode path that the editor's browser already does not need.
            var bytes = new byte[texture.Pixels.Length * 4];
            Buffer.BlockCopy(texture.Pixels, 0, bytes, 0, bytes.Length);

            json.Set("pixels", Convert.ToBase64String(bytes));

            return json;
        }

        private static void ReadTextures(ProjectFile project, JsonValue array)
        {
            foreach (var json in array.Items)
            {
                var texture = new ProjectTexture();

                texture.Id = json["id"].AsInt(0);
                texture.Width = json["width"].AsInt(0);
                texture.Height = json["height"].AsInt(0);

                var encoded = json["pixels"].AsString("");

                if (encoded.Length == 0)
                {
                    EngineLog.Warn("project", "Texture " + texture.Id + " has no pixel data.");
                    continue;
                }

                try
                {
                    var bytes = Convert.FromBase64String(encoded);
                    var count = bytes.Length / 4;

                    texture.Pixels = new uint[count];
                    Buffer.BlockCopy(bytes, 0, texture.Pixels, 0, count * 4);
                }
                catch (FormatException error)
                {
                    EngineLog.Warn("project", "Texture " + texture.Id + " has unreadable pixel data: " +
                                              error.Message);
                    continue;
                }

                project.Textures.Add(texture);
            }
        }

        // ---------------------------------------------------------------- scenes

        private static JsonValue WriteScene(Scene scene)
        {
            var json = JsonValue.NewObject();

            json.Set("id", scene.Id);
            json.Set("name", scene.Name);

            var camera = JsonValue.NewObject();
            camera.Set("position", ProjectFile.Vector(scene.Camera.Position));
            camera.Set("target", ProjectFile.Vector(scene.Camera.Target));
            camera.Set("up", ProjectFile.Vector(scene.Camera.Up));
            camera.Set("fov", scene.Camera.FieldOfView);
            camera.Set("roll", scene.Camera.Roll);
            camera.Set("near", scene.Camera.NearPlane);
            camera.Set("far", scene.Camera.FarPlane);
            json.Set("camera", camera);

            var fog = JsonValue.NewObject();
            fog.Set("enabled", scene.Fog.Enabled);
            fog.Set("colour", scene.Fog.Colour.ToHex());
            fog.Set("start", scene.Fog.Start);
            fog.Set("end", scene.Fog.End);
            json.Set("fog", fog);

            json.Set("ambient", scene.Ambient.ToHex());
            json.Set("lightDirection", ProjectFile.Vector(scene.LightDirection));

            var nodes = JsonValue.NewArray();

            for (var i = 0; i < scene.Nodes.Count; i++)
            {
                nodes.Add(WriteNode(scene.Nodes[i]));
            }

            json.Set("nodes", nodes);

            var animations = JsonValue.NewArray();

            for (var i = 0; i < scene.Animations.Count; i++)
            {
                var animation = scene.Animations[i];
                var entry = JsonValue.NewObject();

                entry.Set("id", animation.Id);
                entry.Set("name", animation.Name);
                entry.Set("tracks", WriteTracks(animation.Tracks));

                animations.Add(entry);
            }

            json.Set("animations", animations);

            var cameraTracks = WriteTracks(scene.CameraTracks);
            json.Set("cameraTracks", cameraTracks);

            return json;
        }

        private static void ReadScenes(ProjectFile project, JsonValue array)
        {
            foreach (var json in array.Items)
            {
                var scene = new Scene();

                scene.Id = json["id"].AsInt(0);
                scene.Name = json["name"].AsString("scene");

                var camera = json["camera"];
                scene.Camera.Position = ProjectFile.ParseVector(camera["position"], new Vector3(0f, 0f, -10f));
                scene.Camera.Target = ProjectFile.ParseVector(camera["target"], Vector3.Zero);
                scene.Camera.Up = ProjectFile.ParseVector(camera["up"], Vector3.Up);
                scene.Camera.FieldOfView = camera["fov"].AsFloat(45f);
                scene.Camera.Roll = camera["roll"].AsFloat(0f);
                scene.Camera.NearPlane = camera["near"].AsFloat(0.1f);
                scene.Camera.FarPlane = camera["far"].AsFloat(2000f);

                var fog = json["fog"];
                scene.Fog.Enabled = fog["enabled"].AsBool(false);
                scene.Fog.Colour = ColorRgba.Parse(fog["colour"].AsString("#000000"));
                scene.Fog.Start = fog["start"].AsFloat(0f);
                scene.Fog.End = fog["end"].AsFloat(100f);

                scene.Ambient = ColorRgba.Parse(json["ambient"].AsString("#FFFFFFFF"));
                scene.LightDirection = ProjectFile.ParseVector(json["lightDirection"],
                    new Vector3(0.4f, -0.7f, 0.6f));

                foreach (var nodeJson in json.ArrayOf("nodes").Items)
                {
                    scene.Nodes.Add(ReadNode(nodeJson));
                }

                foreach (var animationJson in json.ArrayOf("animations").Items)
                {
                    var animation = new SceneAnimation();
                    animation.Id = animationJson["id"].AsInt(0);
                    animation.Name = animationJson["name"].AsString("");

                    ReadTracks(animationJson.ArrayOf("tracks"), animation.Tracks);
                    scene.Animations.Add(animation);
                }

                ReadTracks(json.ArrayOf("cameraTracks"), scene.CameraTracks);

                scene.RebuildHierarchy();
                project.Scenes.Add(scene);
            }
        }

        private static JsonValue WriteNode(SceneNode node)
        {
            var json = JsonValue.NewObject();

            json.Set("id", node.Id);
            json.Set("parent", node.ParentId);
            json.Set("name", node.Name);
            json.Set("visible", node.Visible);

            json.Set("position", ProjectFile.Vector(node.Position));
            json.Set("rotation", ProjectFile.QuaternionValue(node.Rotation));
            json.Set("scale", ProjectFile.Vector(node.Scale));

            if (node.Mesh != null) json.Set("mesh", WriteMesh(node.Mesh));
            if (node.Material != null) json.Set("material", WriteMaterial(node.Material));
            if (node.Emitter != null) json.Set("emitter", WriteEmitter(node.Emitter));

            return json;
        }

        private static SceneNode ReadNode(JsonValue json)
        {
            var node = new SceneNode();

            node.Id = json["id"].AsInt(0);
            node.ParentId = json["parent"].AsInt(-1);
            node.Name = json["name"].AsString("");
            node.Visible = json["visible"].AsBool(true);

            node.Position = ProjectFile.ParseVector(json["position"], Vector3.Zero);
            node.Rotation = ProjectFile.ParseQuaternion(json["rotation"], Quaternion.Identity);
            node.Scale = ProjectFile.ParseVector(json["scale"], Vector3.One);

            var mesh = json["mesh"];

            if (mesh.Type == JsonValue.Kind.Object) node.Mesh = ReadMesh(mesh);

            node.Material = ReadMaterial(json["material"]);

            var emitter = json["emitter"];

            if (emitter.Type == JsonValue.Kind.Object) node.Emitter = ReadEmitter(emitter);

            return node;
        }

        // ---------------------------------------------------------------- meshes

        private static JsonValue WriteMesh(Mesh mesh)
        {
            var json = JsonValue.NewObject();

            // Procedural meshes are stored as their recipe, not as baked geometry:
            // a sphere is 200 bytes as parameters and 200 kilobytes as vertices,
            // and the editor can still change its segment count.
            json.Set("primitive", mesh.Primitive.ToString().ToLowerInvariant());
            json.Set("size", mesh.PrimitiveSize);
            json.Set("segments", mesh.PrimitiveSegments);
            json.Set("rings", mesh.PrimitiveRings);
            json.Set("uvScale", mesh.TextureScale);

            if (mesh.Primitive == MeshPrimitive.Custom)
            {
                var vertices = JsonValue.NewArray();

                for (var i = 0; i < mesh.Vertices.Length; i++)
                {
                    var v = mesh.Vertices[i];

                    vertices.Add(JsonValue.NewArray()
                        .Add(v.Position.X).Add(v.Position.Y).Add(v.Position.Z)
                        .Add(v.Normal.X).Add(v.Normal.Y).Add(v.Normal.Z)
                        .Add(v.TextureCoordinate.X).Add(v.TextureCoordinate.Y));
                }

                json.Set("vertices", vertices);
                json.Set("indices", JsonValue.NewArray());

                var indices = json["indices"];

                for (var i = 0; i < mesh.Indices.Length; i++)
                {
                    indices.Add(mesh.Indices[i]);
                }
            }

            return json;
        }

        private static Mesh ReadMesh(JsonValue json)
        {
            var primitive = json["primitive"].AsString("box");
            var size = json["size"].AsFloat(2f);
            var segments = json["segments"].AsInt(16);
            var rings = json["rings"].AsInt(8);
            var uvScale = json["uvScale"].AsFloat(1f);

            Mesh mesh;

            switch (primitive.ToLowerInvariant())
            {
                case "sphere":
                    mesh = Mesh.CreateSphere(size * 0.5f, segments, rings);
                    break;

                case "plane":
                    mesh = Mesh.CreatePlane(size, size, Math.Max(1, segments / 4));
                    break;

                case "custom":
                    mesh = ReadCustomMesh(json);
                    break;

                default:
                    mesh = Mesh.CreateBox(size);
                    break;
            }

            // Re-apply the tiling the primitive was authored with. A primitive is
            // saved as its recipe, so anything that is not a constructor parameter
            // has to be restored explicitly.
            if (uvScale > 0f && Math.Abs(uvScale - 1f) > 1e-6f)
            {
                mesh.ScaleTextureCoordinates(uvScale);
            }

            return mesh;
        }

        private static Mesh ReadCustomMesh(JsonValue json)
        {
            var mesh = new Mesh();
            mesh.Primitive = MeshPrimitive.Custom;

            var vertices = json.ArrayOf("vertices");
            var indices = json.ArrayOf("indices");

            var vertexArray = new Vertex[vertices.Count];

            for (var i = 0; i < vertices.Count; i++)
            {
                var v = vertices[i];

                if (v.Count < 8) continue;

                vertexArray[i] = new Vertex(
                    new Vector3(v[0].AsFloat(0f), v[1].AsFloat(0f), v[2].AsFloat(0f)),
                    new Vector3(v[3].AsFloat(0f), v[4].AsFloat(0f), v[5].AsFloat(0f)),
                    new Vector2(v[6].AsFloat(0f), v[7].AsFloat(0f)));
            }

            var indexArray = new int[indices.Count];

            for (var i = 0; i < indices.Count; i++)
            {
                indexArray[i] = indices[i].AsInt(0);
            }

            mesh.Vertices = vertexArray;
            mesh.Indices = indexArray;

            if (vertexArray.Length > 0 && indexArray.Length >= 3) mesh.RecalculateBounds();

            return mesh;
        }

        // ---------------------------------------------------------------- materials

        private static JsonValue WriteMaterial(Material material)
        {
            var json = JsonValue.NewObject();

            json.Set("texture", material.TextureIndex);
            json.Set("environment", material.EnvironmentTextureIndex);
            json.Set("colour", material.Colour.ToHex());
            json.Set("sourceBlend", Effect.BlendName(material.SourceBlend));
            json.Set("destinationBlend", Effect.BlendName(material.DestinationBlend));
            json.Set("shading", material.Shading.ToString().ToLowerInvariant());
            json.Set("wireframe", material.Wireframe);
            json.Set("twoSided", material.TwoSided);
            json.Set("depthWrite", material.DepthWrite);
            json.Set("depthTest", material.DepthTest);
            json.Set("transparent", material.Transparent);

            return json;
        }

        private static Material ReadMaterial(JsonValue json)
        {
            var material = new Material();

            if (json.Type != JsonValue.Kind.Object) return material;

            material.TextureIndex = json["texture"].AsInt(-1);
            material.EnvironmentTextureIndex = json["environment"].AsInt(-1);
            material.Colour = ColorRgba.Parse(json["colour"].AsString("#FFFFFFFF"));
            material.SourceBlend = Effect.ParseBlend(json["sourceBlend"], BlendMode.SourceAlpha);
            material.DestinationBlend = Effect.ParseBlend(json["destinationBlend"], BlendMode.InverseSourceAlpha);

            switch (json["shading"].AsString("gouraud").ToLowerInvariant())
            {
                case "unlit": material.Shading = ShadingMode.Unlit; break;
                case "flat": material.Shading = ShadingMode.Flat; break;
                default: material.Shading = ShadingMode.Gouraud; break;
            }

            material.Wireframe = json["wireframe"].AsBool(false);
            material.TwoSided = json["twoSided"].AsBool(false);
            material.DepthWrite = json["depthWrite"].AsBool(true);
            material.DepthTest = json["depthTest"].AsBool(true);
            material.Transparent = json["transparent"].AsBool(false);

            return material;
        }

        // ---------------------------------------------------------------- emitters

        private static JsonValue WriteEmitter(ParticleEmitter emitter)
        {
            var json = JsonValue.NewObject();

            json.Set("shape", emitter.Shape.ToString().ToLowerInvariant());
            json.Set("maxParticles", emitter.MaxParticles);
            json.Set("rate", emitter.Rate);
            json.Set("lifetime", emitter.Lifetime);
            json.Set("lifetimeChaos", emitter.LifetimeChaos);
            json.Set("speed", emitter.Speed);
            json.Set("speedChaos", emitter.SpeedChaos);
            json.Set("direction", ProjectFile.Vector(emitter.Direction));
            json.Set("spread", emitter.Spread);
            json.Set("startSize", emitter.StartSize);
            json.Set("endSize", emitter.EndSize);
            json.Set("sizeChaos", emitter.SizeChaos);
            json.Set("startColour", emitter.StartColour.ToHex());
            json.Set("endColour", emitter.EndColour.ToHex());
            json.Set("gravity", ProjectFile.Vector(emitter.Gravity));
            json.Set("radius", emitter.Radius);
            json.Set("boxSize", ProjectFile.Vector(emitter.BoxSize));
            json.Set("spin", emitter.Spin);
            json.Set("seed", emitter.Seed);

            return json;
        }

        private static ParticleEmitter ReadEmitter(JsonValue json)
        {
            var emitter = new ParticleEmitter();

            switch (json["shape"].AsString("point").ToLowerInvariant())
            {
                case "sphere": emitter.Shape = EmitterShape.Sphere; break;
                case "box": emitter.Shape = EmitterShape.Box; break;
                case "disc": emitter.Shape = EmitterShape.Disc; break;
                default: emitter.Shape = EmitterShape.Point; break;
            }

            emitter.MaxParticles = json["maxParticles"].AsInt(512);
            emitter.Rate = json["rate"].AsFloat(8f);
            emitter.Lifetime = json["lifetime"].AsFloat(1.5f);
            emitter.LifetimeChaos = json["lifetimeChaos"].AsFloat(0.5f);
            emitter.Speed = json["speed"].AsFloat(3f);
            emitter.SpeedChaos = json["speedChaos"].AsFloat(1f);
            emitter.Direction = ProjectFile.ParseVector(json["direction"], new Vector3(0f, 1f, 0f));
            emitter.Spread = json["spread"].AsFloat(25f);
            emitter.StartSize = json["startSize"].AsFloat(0.5f);
            emitter.EndSize = json["endSize"].AsFloat(0.05f);
            emitter.SizeChaos = json["sizeChaos"].AsFloat(0.3f);
            emitter.StartColour = ColorRgba.Parse(json["startColour"].AsString("#FFC850FF"));
            emitter.EndColour = ColorRgba.Parse(json["endColour"].AsString("#FF280000"));
            emitter.Gravity = ProjectFile.ParseVector(json["gravity"], new Vector3(0f, -4f, 0f));
            emitter.Radius = json["radius"].AsFloat(0.5f);
            emitter.BoxSize = ProjectFile.ParseVector(json["boxSize"], new Vector3(0.5f, 0.5f, 0.5f));
            emitter.Spin = json["spin"].AsFloat(45f);
            emitter.Seed = json["seed"].AsInt(12345);

            emitter.Reset();
            return emitter;
        }

        // ---------------------------------------------------------------- tracks

        private static JsonValue WriteTracks(List<AnimationTrack> tracks)
        {
            var array = JsonValue.NewArray();

            for (var i = 0; i < tracks.Count; i++)
            {
                var track = tracks[i];
                var json = JsonValue.NewObject();

                json.Set("property", track.Property);
                json.Set("interpolation", track.Curve.Interpolation.ToString().ToLowerInvariant());
                json.Set("loop", track.Curve.Loop);

                var keys = JsonValue.NewArray();

                for (var k = 0; k < track.Curve.KeyCount; k++)
                {
                    var key = track.Curve[k];
                    keys.Add(JsonValue.NewArray().Add(key.Time).Add(key.Value));
                }

                json.Set("keys", keys);
                array.Add(json);
            }

            return array;
        }

        private static void ReadTracks(JsonValue array, List<AnimationTrack> target)
        {
            foreach (var json in array.Items)
            {
                var track = new AnimationTrack();

                track.Property = json["property"].AsString("");

                if (track.Property.Length == 0) continue;

                switch (json["interpolation"].AsString("linear").ToLowerInvariant())
                {
                    case "step": track.Curve.Interpolation = Interpolation.Step; break;
                    case "hermite": track.Curve.Interpolation = Interpolation.Hermite; break;
                    case "catmull": track.Curve.Interpolation = Interpolation.Catmull; break;
                    case "bezier": track.Curve.Interpolation = Interpolation.Bezier; break;
                    case "sine": track.Curve.Interpolation = Interpolation.Sine; break;
                    case "saw": track.Curve.Interpolation = Interpolation.Saw; break;
                    case "square": track.Curve.Interpolation = Interpolation.Square; break;
                    default: track.Curve.Interpolation = Interpolation.Linear; break;
                }

                track.Curve.Loop = json["loop"].AsBool(false);

                var keys = json.ArrayOf("keys");
                var parsed = new Key[keys.Count];

                for (var k = 0; k < keys.Count; k++)
                {
                    parsed[k] = new Key
                    {
                        Time = keys[k][0].AsFloat(0f),
                        Value = keys[k][1].AsFloat(0f)
                    };
                }

                track.Curve.SetKeys(parsed);
                target.Add(track);
            }
        }

        // ---------------------------------------------------------------- timeline

        private static void ReadTimeline(ProjectFile project, JsonValue array)
        {
            foreach (var json in array.Items)
            {
                var effect = Effect.Load(json);

                if (effect == null) continue;

                project.Timeline.Add(effect);
            }
        }
    }
}
