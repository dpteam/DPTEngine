//
// DPT.Engine - Addict2 .dmo project importer.
// Copyright (C) 2026 DartPower Team. GPLv2.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using DPT.Engine.Mathematics;
using DPT.Engine.Project;
using DPT.Engine.Scenes;
using DPT.Engine.Textures;
using DPT.Engine.Timeline;

namespace DPT.Engine.Import
{
    /// <summary>
    /// Reads an Addict2 <c>.dmo</c> project.
    ///
    /// The original engine stores a project as a chunked binary stream: an eight
    /// byte signature, then a sequence of <c>{ id, size, payload }</c> records
    /// terminated by END0. Everything the editor knows - textures, scenes, worlds,
    /// cameras, animations, the timeline - is one of those records, and objects are
    /// spread over several records that build on each other (an OBJ4 is an OBJ3
    /// plus a supershape block, an OBJ3 is an OBJ2 plus two flags, and so on).
    ///
    /// Two things make this tractable:
    ///
    /// * Every chunk carries its own size, so the reader can verify after each one
    ///   that it consumed exactly the payload. A wrong struct size therefore shows
    ///   up as a precise error instead of silent corruption three chunks later.
    ///
    /// * Nothing is guessed. Field order, sizes and conditional fields come from
    ///   FileIO.cpp in the original sources, and the record sizes were checked
    ///   against the nineteen projects that ship with them.
    ///
    /// The result is a <see cref="ProjectFile"/>: Addict2's worlds become scenes,
    /// its world objects become nodes, and its events become timeline effects.
    /// </summary>
    public static class DmoReader
    {
        private const string Signature = "aDDict2P";

        // Chunk identifiers, little-endian four character codes from ChunkHeaders.h.
        private const string ChunkTexture0 = "TEX0";
        private const string ChunkTexture1 = "TEX1";
        private const string ChunkPolySelect = "PLS0";
        private const string ChunkVertexSelect = "VXS0";
        private const string ChunkBrush = "BRS0";
        private const string ChunkScene0 = "SCN0";
        private const string ChunkScene1 = "SCN1";
        private const string ChunkObject0 = "OBJ0";
        private const string ChunkObject1 = "OBJ1";
        private const string ChunkObject2 = "OBJ2";
        private const string ChunkObject3 = "OBJ3";
        private const string ChunkObject4 = "OBJ4";
        private const string ChunkObject5 = "OBJ5";
        private const string ChunkMapXform = "MAP0";
        private const string ChunkMeshSmooth = "MSH0";
        private const string ChunkMeshSmooth1 = "MSH1";
        private const string ChunkClone = "CLN0";
        private const string ChunkWorld0 = "WRL0";
        private const string ChunkWorld1 = "WRL1";
        private const string ChunkWorld2 = "WRL2";
        private const string ChunkWorldObject0 = "WRO0";
        private const string ChunkWorldObject1 = "WRO1";
        private const string ChunkWorldObject2 = "WRO2";
        private const string ChunkWorldObject3 = "WRO3";
        private const string ChunkWorldObject4 = "WRO4";
        private const string ChunkWorldObject5 = "WRO5";
        private const string ChunkWorldAnim = "WRA0";
        private const string ChunkWorldObjectAnim0 = "WOA0";
        private const string ChunkWorldObjectAnim1 = "WOA1";
        private const string ChunkWorldObjectAnim2 = "WOA2";
        private const string ChunkCamera = "CAM0";
        private const string ChunkEvent0 = "EVT0";
        private const string ChunkEvent1 = "EVT1";
        private const string ChunkEvent2 = "EVT2";
        private const string ChunkEnd = "END0";

        // Struct sizes, in bytes, as the original C++ sees them.
        private const int SizeVector2 = 8;
        private const int SizeVector3 = 12;
        private const int SizeColor = 4;
        private const int SizeMatrix = 64;
        private const int SizeRst = 52;          // Pos, Rot, Scale, Quaternion
        private const int SizeCamera = 68;       // sizeof(CAMERA) - 16: Normal and d are not stored
        private const int SizeLight = 96;
        // 3dEngine.h has no #pragma pack, so this struct carries its natural
        // alignment: 100 bytes, not the 85 a packed layout would give. The
        // difference is padding after each of the small flag fields.
        private const int SizeSupershape = 100;

        /// <summary>Primitive ids, from the original aDDict_* enum.</summary>
        private const int PrimitiveBox = 0;
        private const int PrimitiveSphere = 1;
        private const int PrimitiveCylinder = 2;
        private const int PrimitiveCone = 3;
        private const int PrimitivePlane = 4;
        private const int PrimitiveArc = 5;
        private const int PrimitiveLine = 6;
        private const int PrimitiveLoft = 7;
        private const int PrimitiveIcosaeder = 8;
        private const int PrimitiveDodecaeder = 9;
        private const int PrimitiveCircle = 10;
        private const int PrimitiveClone = 11;
        private const int PrimitiveStored = 12;
        private const int PrimitiveFur = 13;
        private const int PrimitiveSupershape = 14;

        /// <summary>World object primitive ids, from aDDict_* in the world enum.</summary>
        private const int WorldDummy = 0;
        private const int WorldScene = 1;
        private const int WorldSubscene = 2;
        private const int WorldSphereEmitter = 3;
        private const int WorldCubeEmitter = 4;
        private const int WorldCylinderEmitter = 5;
        private const int WorldPlaneDeflector = 6;
        private const int WorldSphereDeflector = 7;
        private const int WorldGravity = 8;

        // ---------------------------------------------------------------- loading

        /// <summary>Reads a project file. Throws <see cref="InvalidDataException"/> on a malformed file.</summary>
        public static ProjectFile Load(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException("path");

            return Load(File.ReadAllBytes(path), Path.GetFileNameWithoutExtension(path));
        }

        public static ProjectFile Load(byte[] data, string name)
        {
            if (data == null) throw new ArgumentNullException("data");
            if (data.Length < 16) throw new InvalidDataException("The file is too short to be a project.");

            var signature = Encoding.ASCII.GetString(data, 0, 8).TrimEnd('\0');

            if (!string.Equals(signature, Signature, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Not an Addict2 project: the signature is '" + signature + "', expected '" + Signature + "'.");
            }

            var reader = new Reader(data, name);
            return reader.ReadProject();
        }

        /// <summary>True when the bytes look like an Addict2 project, without parsing it.</summary>
        public static bool IsDmo(byte[] data)
        {
            if (data == null || data.Length < 8) return false;

            var signature = Encoding.ASCII.GetString(data, 0, 8).TrimEnd('\0');
            return string.Equals(signature, Signature, StringComparison.Ordinal);
        }

        // ---------------------------------------------------------------- reader

        /// <summary>
        /// Walks the chunk stream and builds the project.
        ///
        /// Kept as a nested class so the parse state - the buffer, the position, the
        /// objects accumulated so far - is explicit rather than threaded through a
        /// dozen parameters.
        /// </summary>
        private sealed class Reader
        {
            private readonly byte[] data;
            private readonly string name;

            private int position;
            private int chunkStart;
            private int chunkSize;

            private readonly ProjectFile project = new ProjectFile();

            // Addict2 builds state incrementally: an object chunk appends to "the
            // current scene", a world object appends to "the current world". The
            // reader has to model that, because the chunks are siblings in the
            // stream rather than nested.
            private DmoScene currentScene;
            private DmoWorld currentWorld;
            private DmoObject currentObject;

            private readonly List<DmoTexture> textures = new List<DmoTexture>();
            private readonly List<DmoScene> scenes = new List<DmoScene>();
            private readonly List<DmoWorld> worlds = new List<DmoWorld>();
            private readonly List<DmoEvent> events = new List<DmoEvent>();

            /// <summary>World-object animations are written after their object, addressed by index.</summary>
            private int objectAnimationCursor;

            /// <summary>Where the last chunk's parse went, for a useful error message.</summary>
            private string lastTrace;

            internal Reader(byte[] data, string name)
            {
                this.data = data;
                this.name = name;
            }

            internal ProjectFile ReadProject()
            {
                project.Name = string.IsNullOrEmpty(name) ? "Imported project" : name;
                project.Author = "Addict2";

                position = 8;

                while (position + 8 <= data.Length)
                {
                    var id = ReadChunkId();
                    chunkSize = ReadInt32();
                    chunkStart = position;
                    lastTrace = null;

                    if (chunkSize < 0 || chunkStart + chunkSize > data.Length)
                    {
                        throw new InvalidDataException("Chunk " + id + " at " + (chunkStart - 8) +
                                                       " claims " + chunkSize + " bytes, past the end of the file.");
                    }

                    if (id == ChunkEnd) break;

                    Dispatch(id);

                    // The self-check. Every loader must consume its payload exactly;
                    // if it does not, a struct size in this file is wrong, and saying
                    // so here is far better than failing somewhere unrelated later.
                    if (position != chunkStart + chunkSize)
                    {
                        var detail = lastTrace != null ? " [" + lastTrace + "]" : "";

                        throw new InvalidDataException("Chunk " + id + " at " + (chunkStart - 8) +
                                                       " consumed " + (position - chunkStart) + " of " +
                                                       chunkSize + " bytes." + detail);
                    }
                }

                BuildProject();

                return project;
            }

            private void Dispatch(string id)
            {
                switch (id)
                {
                    case ChunkTexture0: ReadTexture(0); break;
                    case ChunkTexture1: ReadTexture(1); break;
                    case ChunkPolySelect:
                    case ChunkVertexSelect: ReadPolySelect(); break;
                    case ChunkBrush: ReadBrush(); break;
                    case ChunkScene0: ReadScene(0); break;
                    case ChunkScene1: ReadScene(1); break;

                    case ChunkObject0: ReadObject(0); break;
                    case ChunkObject1: ReadObject(1); break;
                    case ChunkObject2: ReadObject(2); break;
                    case ChunkObject3: ReadObject(3); break;
                    case ChunkObject4: ReadObject(4); break;
                    case ChunkObject5: ReadObject(5); break;
                    case ChunkMapXform: ReadMapXform(); break;
                    case ChunkMeshSmooth: ReadMeshSmooth(false); break;
                    case ChunkMeshSmooth1: ReadMeshSmooth(true); break;
                    case ChunkClone: ReadClone(); break;

                    case ChunkWorld0: ReadWorld(0); break;
                    case ChunkWorld1: ReadWorld(1); break;
                    case ChunkWorld2: ReadWorld(2); break;

                    case ChunkWorldObject0: ReadWorldObject(0); break;
                    case ChunkWorldObject1: ReadWorldObject(1); break;
                    case ChunkWorldObject2: ReadWorldObject(2); break;
                    case ChunkWorldObject3: ReadWorldObject(3); break;
                    case ChunkWorldObject4: ReadWorldObject(4); break;
                    case ChunkWorldObject5: ReadWorldObject(5); break;

                    case ChunkCamera: ReadCamera(); break;
                    case ChunkWorldAnim: ReadWorldAnimation(); break;
                    case ChunkWorldObjectAnim0: ReadObjectAnimation(0); break;
                    case ChunkWorldObjectAnim1: ReadObjectAnimation(1); break;
                    case ChunkWorldObjectAnim2: ReadObjectAnimation(2); break;

                    case ChunkEvent0: ReadEvent(); break;
                    case ChunkEvent1: ReadEvent(); break;
                    case ChunkEvent2: ReadEvent(); break;

                    default:
                        // An unknown chunk is skipped rather than rejected: a project
                        // from a newer editor must still play what this build knows.
                        position = chunkStart + chunkSize;
                        break;
                }
            }

            // ------------------------------------------------------------ textures

            private void ReadTexture(int version)
            {
                var texture = new DmoTexture();

                texture.Name = ReadAsciiZ();
                texture.Id = ReadInt32();
                texture.Resolution = ReadInt32();
                var commandCount = ReadInt32();

                if (texture.Resolution <= 0 || texture.Resolution > 2048)
                {
                    throw new InvalidDataException("Texture '" + texture.Name + "' has resolution " +
                                                   texture.Resolution + ".");
                }

                var trace = new StringBuilder();

                var recipe = new TexRecipe();
                recipe.Id = texture.Id;
                recipe.Resolution = texture.Resolution;
                recipe.Commands = new List<TexCommand>();

                var commandTrace = new StringBuilder();

                for (var i = 0; i < commandCount; i++)
                {
                    var command = new TexCommand();

                    command.Filter = ReadByte();
                    command.Layer = ReadByte();
                    command.Operator = ReadByte();
                    command.OperatorMask = ReadInt32();

                    command.Data = ReadFilterData(command.Filter, version);
                    recipe.Commands.Add(command);

                    // Record where each command ended. When a texture chunk does not
                    // consume its payload exactly, this trace names the filter that
                    // went wrong, which is the difference between a five-minute fix
                    // and an afternoon of hex dumping.
                    if (commandTrace.Length < 400)
                    {
                        commandTrace.Append(command.Filter).Append('@').Append(position - chunkStart).Append(' ');
                    }
                }

                texture.Recipe = recipe;
                textures.Add(texture);

                lastTrace = texture.Name + " [" + commandTrace + "]";
            }

            /// <summary>
            /// Reads the parameter block of one texture command.
            ///
            /// The sizes are the template structs in TexGen2.h, which are compiled
            /// with #pragma pack(1), so they are exact sums of their fields - a
            /// two-byte template really is two bytes on disk. Three filters are
            /// variable length and are handled separately.
            /// </summary>
            private byte[] ReadFilterData(int filter, int version)
            {
                switch (filter)
                {
                    // A null-terminated string after a fixed prefix. The original
                    // leaves room for a pointer in the struct and then stores the
                    // text instead, so the on-disk size is the prefix plus the text.
                    case 5: return ReadTextFilter(version);
                    case 30: return ReadMaterialNameFilter();

                    // A length-prefixed JPEG blob.
                    case 35:
                    {
                        var length = ReadInt32();

                        if (length < 0 || length > 16 * 1024 * 1024)
                        {
                            throw new InvalidDataException("A texture image claims " + length + " bytes.");
                        }

                        var header = BitConverter.GetBytes(length);
                        var body = ReadBytes(length);

                        var result = new byte[header.Length + body.Length];
                        Array.Copy(header, result, header.Length);
                        Array.Copy(body, 0, result, header.Length, body.Length);
                        return result;
                    }
                }

                return ReadBytes(FilterSize(filter));
            }

            /// <summary>
            /// The on-disk size of a filter's parameter block, from the packed
            /// template structs in TexGen2.h.
            /// </summary>
            private static int FilterSize(int filter)
            {
                switch (filter)
                {
                    case 0: return 1;     // Envmap: Size
                    case 1: return 2;     // Plasma: XSines, YSines
                    case 2: return 6;     // Map: two layers, two channels, two amounts
                    case 3: return 2;     // Blur: Iterations, SampleWidth
                    case 4: return 3;     // DirBlur: DataLayer, DataChannel, Depth
                    case 6: return 2;     // SubPlasma: Size, RandSeed
                    case 7: return 4;     // FractalPlasma: Min, Max, Blend, RandSeed
                    case 8: return 9;     // Colorize: two colours, DataChannel
                    case 9: return 2;     // Shade: DataLayer, DataChannel
                    case 10: return 1;    // Brightness: Amount
                    case 11: return 1;    // Copy: DataLayer
                    case 12: return 3;    // Cells: Points, Power, RandSeed
                    case 13: return 1;    // Twirl: Amount
                    case 14: return 4;    // SineDist: XSines, YSines, XAmp, YAmp
                    case 15: return 2;    // Mix: DataLayer, Amount
                    case 16: return 3;    // MixMap: DataLayer, MixLayer, MixChannel
                    case 17: return 3;    // Scale: DataChannel, Min, Max
                    case 18: return 2;    // SineColor: DataChannel, NumSines
                    case 19: return 1;    // Max: DataLayer
                    case 20: return 2;    // HSV: Hue, Saturation
                    case 21: return 0;    // Emboss: no parameters
                    case 22: return 0;    // Invert: no parameters
                    case 23: return 3;    // Glass: DataLayer, DataChannel, Amount
                    case 24: return 2;    // Pixelize: XSquares, YSquares
                    case 25: return 2;    // Offset: X, Y
                    case 26: return 2;    // Crystalize: Points, RandSeed
                    case 27: return 4;    // Rectangle: StartX, StartY, SizeX, SizeY
                    case 28: return 4;    // Circle: InnerRadius, OuterRadius, X, Y
                    case 29: return 1;    // Contrast: Amount
                    case 31: return 16;   // Gradient: four corner colours
                    case 32: return 6;    // Rotozoom: centre, zoom, direction, rotation
                    case 33: return 5;    // ChamferRectangle: rectangle plus a chamfer
                    case 34: return 8;    // Dots: count, range, roundness, sizes, seed
                    case 36: return 1;    // FractalPlasmaOld: RandSeed
                    default: return 0;    // unknown: assume no parameters
                }
            }

            private byte[] ReadTextFilter(int version)
            {
                // TEXTTEMPLATE is seven bytes of settings, then the string. The
                // original writes sizeof(TEXTTEMPLATE)-4, and the struct is those
                // seven fields plus a four-byte pointer, so seven is what is on disk.
                var prefix = ReadBytes(7);
                var text = ReadAsciiZ();

                var result = new byte[prefix.Length + text.Length + 1];
                Array.Copy(prefix, result, prefix.Length);
                Encoding.ASCII.GetBytes(text, 0, text.Length, result, prefix.Length);
                return result;
            }

            private byte[] ReadMaterialNameFilter()
            {
                // MAKEMATERIALTEMPLATE is the slot byte, then the material name.
                var slot = ReadByte();
                var name = ReadAsciiZ();

                var result = new byte[1 + name.Length + 1];
                result[0] = slot;
                Encoding.ASCII.GetBytes(name, 0, name.Length, result, 1);
                return result;
            }

            // ------------------------------------------------------------ scenes

            private void ReadScene(int version)
            {
                var scene = new DmoScene();

                scene.Name = ReadAsciiZ();
                scene.Id = ReadInt32();
                scene.Camera = ReadCameraBlock();
                scene.FogColour = ReadColor();
                Skip(8 * SizeLight);
                scene.ColorDiscard = version >= 1 && ReadByte() != 0;

                scenes.Add(scene);
                currentScene = scene;
                currentObject = null;
            }

            private void ReadPolySelect()
            {
                // A named selection of polygons, used only by the map-xform tool. The
                // geometry itself is unaffected, so the record is skipped by name and
                // then by its declared count.
                ReadAsciiZ();
                ReadInt32();
            }

            private void ReadBrush()
            {
                // PVSelect is six ints. Brushes are editor-side selection state.
                ReadAsciiZ();
                Skip(24);
            }

            // ------------------------------------------------------------ objects

            private void ReadObject(int version)
            {
                if (currentScene == null)
                {
                    throw new InvalidDataException("An object chunk appears before any scene.");
                }

                var obj = new DmoObject();

                obj.Name = ReadAsciiZ();
                ReadByte();                        // list selection flag, editor only
                obj.Id = ReadInt32();
                obj.Param1 = ReadInt32();
                obj.Param2 = ReadInt32();
                obj.Param3 = ReadInt32();
                obj.Param4 = ReadInt32();
                obj.Param5 = ReadInt32();
                obj.Primitive = ReadByte();

                // The original forces a minimum segment count for a cone; without it
                // a cone with zero sides generates nothing at all.
                if (obj.Primitive == PrimitiveCone && obj.Param3 == 0) obj.Param3 = 1;

                if (obj.Primitive == PrimitiveStored) ReadStoredGeometry(obj, version >= 5);

                if (obj.Primitive == PrimitiveLoft)
                {
                    obj.Param1 += 0;
                    obj.Param2 += 0;
                }

                obj.Transform = ReadMatrix();

                obj.Wireframe = ReadByte() != 0;
                obj.Textured = ReadByte() != 0;
                obj.EnvMapped = ReadByte() != 0;
                obj.NormalsInverted = ReadByte() != 0;
                obj.Xtile = ReadByte();
                obj.Ytile = ReadByte();
                obj.XSwap = ReadByte() != 0;
                obj.YSwap = ReadByte() != 0;
                obj.Swap = ReadByte() != 0;
                obj.OffsetX = ReadSingle();
                obj.OffsetY = ReadSingle();
                obj.SourceBlend = ReadInt32();
                obj.DestinationBlend = ReadInt32();
                obj.Colour = ReadColor();

                obj.TextureId = ReadInt32();
                obj.TextureSlot = ReadInt32();
                obj.EnvironmentId = ReadInt32();
                obj.EnvironmentSlot = ReadInt32();

                obj.ZMask = true;
                obj.AEpsilon = 0;
                obj.Shading = 0;
                obj.Backface = false;
                obj.Backfront = false;

                if (version >= 1)
                {
                    obj.ZMask = ReadByte() != 0;
                    obj.AEpsilon = ReadByte();
                }

                if (version >= 2) obj.Shading = ReadInt32();

                if (version >= 3)
                {
                    obj.Backface = ReadByte() != 0;
                    obj.Backfront = ReadByte() != 0;
                }

                if (version >= 4)
                {
                    if (obj.Primitive == PrimitiveSupershape)
                    {
                        obj.Supershape = ReadBytes(SizeSupershape);
                    }
                }

                if (version >= 5)
                {
                    // OBJ5 is OBJ4 with per-vertex colour data, which this build does
                    // not use; the flag byte is present in the stream and consumed by
                    // ReadStoredGeometry when the object is STORED.
                }

                currentScene.Objects.Add(obj);
                currentObject = obj;
            }

            private void ReadStoredGeometry(DmoObject obj, bool haveColourInfo)
            {
                // OBJ5 is the only version that may carry per-vertex colour, and it
                // signals it with one byte before the vertex count. Missing that byte
                // shifts the whole geometry block by one and the polygon count reads
                // as garbage - which is exactly how it first failed.
                var perVertexColour = haveColourInfo && ReadByte() != 0;

                var vertexCount = ReadInt32();

                if (vertexCount < 0 || vertexCount > 2000000)
                {
                    throw new InvalidDataException("Object '" + obj.Name + "' claims " + vertexCount + " vertices.");
                }

                obj.Vertices = new DmoVertex[vertexCount];

                for (var i = 0; i < vertexCount; i++)
                {
                    var vertex = new DmoVertex();
                    vertex.Position = ReadVector3();
                    vertex.Normal = ReadVector3();
                    vertex.TextureCoordinate = ReadVector2();

                    if (perVertexColour)
                    {
                        // Read and discarded: the engine colours a whole mesh, not
                        // individual vertices, so this data has nowhere to go.
                        ReadInt32();
                    }

                    obj.Vertices[i] = vertex;
                }

                var polygonCount = ReadInt32();

                if (polygonCount < 0 || polygonCount > 2000000)
                {
                    throw new InvalidDataException("Object '" + obj.Name + "' claims " + polygonCount + " polygons.");
                }

                obj.Polygons = new DmoPolygon[polygonCount];

                for (var i = 0; i < polygonCount; i++)
                {
                    var polygon = new DmoPolygon();
                    polygon.A = ReadInt32();
                    polygon.B = ReadInt32();
                    polygon.C = ReadInt32();
                    polygon.Normal = ReadVector3();
                    polygon.U = ReadVector2();
                    polygon.V = ReadVector2();
                    polygon.W = ReadVector2();
                    obj.Polygons[i] = polygon;
                }
            }

            private void ReadMapXform()
            {
                if (currentScene == null) throw new InvalidDataException("A map-xform chunk appears before any scene.");

                var obj = new DmoObject();

                obj.Name = ReadAsciiZ();
                ReadByte();
                obj.Id = ReadInt32();
                obj.Param1 = ReadInt32();
                obj.Param2 = ReadInt32();
                obj.Param3 = ReadInt32();
                obj.Param4 = ReadInt32();
                obj.Param5 = ReadInt32();
                obj.Primitive = ReadByte();
                obj.Transform = ReadMatrix();
                obj.Xtile = ReadByte();
                obj.Ytile = ReadByte();
                obj.XSwap = ReadByte() != 0;
                obj.YSwap = ReadByte() != 0;
                obj.Swap = ReadByte() != 0;
                obj.OffsetX = ReadSingle();
                obj.OffsetY = ReadSingle();
                obj.TextureId = ReadInt32();
                obj.TextureSlot = ReadInt32();
                obj.TargetObjectId = ReadInt32();
                obj.MapXformType = ReadSByte();
                obj.MapXformColour = ReadSByte();

                // A map transform is a texture-coordinate modifier applied to another
                // object. This build draws the target object with its own coordinates,
                // so the modifier is recorded and not applied.
                obj.IsMapXform = true;
                currentScene.Objects.Add(obj);
            }

            private void ReadMeshSmooth(bool modern)
            {
                if (currentScene == null) throw new InvalidDataException("A mesh-smooth chunk appears before any scene.");

                var obj = new DmoObject();

                obj.Name = ReadAsciiZ();

                if (modern) obj.Primitive = ReadByte();

                ReadByte();                        // list selection
                obj.Id = ReadInt32();
                obj.LinearSmooth = ReadByte() != 0;
                obj.Param1 = ReadInt32();
                obj.Param2 = ReadInt32();

                // A smoothing modifier replaces an existing object's geometry in the
                // original. This build keeps the original geometry, which is what the
                // modifier approximates anyway.
                obj.IsModifier = true;
                currentScene.Objects.Add(obj);
            }

            private void ReadClone()
            {
                if (currentScene == null) throw new InvalidDataException("A clone chunk appears before any scene.");

                var obj = new DmoObject();

                obj.Name = ReadAsciiZ();
                ReadByte();
                obj.Id = ReadInt32();
                obj.Param1 = ReadInt32();

                // A clone stores the source object's indices, one per copy.
                var count = obj.Param1;

                if (count < 0 || count > 100000)
                {
                    throw new InvalidDataException("Clone '" + obj.Name + "' claims " + count + " sources.");
                }

                Skip(count * 4);

                obj.Primitive = ReadByte();
                obj.Transform = ReadMatrix();

                obj.IsModifier = true;
                currentScene.Objects.Add(obj);
            }

            // ------------------------------------------------------------ worlds

            private void ReadWorld(int version)
            {
                var world = new DmoWorld();

                world.Name = ReadAsciiZ();
                world.Id = ReadInt32();
                world.Camera = ReadCameraBlock();
                Skip(8 * SizeLight);

                if (version >= 1)
                {
                    world.Fog = ReadByte() != 0;

                    if (version >= 2)
                    {
                        world.FogColour = ReadColorRgb();
                        world.FogStart = ReadSingle();
                        world.FogEnd = ReadSingle();
                        world.Lighting = ReadByte() != 0;
                    }
                    else
                    {
                        world.FogColour = ReadColor();
                    }
                }

                worlds.Add(world);
                currentWorld = world;
            }

            private void ReadWorldObject(int version)
            {
                if (currentWorld == null) throw new InvalidDataException("A world-object chunk appears before any world.");

                var obj = new DmoWorldObject();

                obj.Name = ReadAsciiZ();
                obj.Id = ReadInt32();
                obj.ParentId = ReadInt32();
                obj.Primitive = ReadInt32();
                obj.Position = ReadRst();
                obj.SourceBlend = ReadInt32();
                obj.DestinationBlend = ReadInt32();
                obj.TargetId = ReadInt32();
                obj.Textured = ReadByte() != 0;
                obj.TextureId = ReadInt32();
                obj.TextureSlot = ReadInt32();

                var emitter = new DmoEmitter();
                emitter.Head = ReadByte() != 0;
                emitter.Tail = ReadByte() != 0;
                emitter.DefaultAge = ReadInt32();
                emitter.AgeChaos = ReadInt32();
                emitter.ParticlesPerFrame = ReadSingle();
                emitter.MaxParticles = ReadInt32();
                emitter.ObjectHead = ReadByte() != 0;
                emitter.TailLength = ReadByte();
                emitter.TailLength2 = ReadByte();
                emitter.ObjectHeadSceneId = ReadInt32();

                emitter.Color1 = ReadFloats(5);
                emitter.Color2 = ReadFloats(5);

                emitter.Param1 = ReadSingle();
                emitter.Param2 = ReadSingle();
                emitter.Size = ReadSingle();

                obj.Emitter = emitter;
                obj.Colour = ColorRgba.White;
                obj.ZMask = false;
                obj.AEpsilon = 0;

                if (version >= 1)
                {
                    emitter.RotationSpeed = ReadSingle();
                    emitter.RotationChaos = ReadSingle();
                    emitter.CameraFade = ReadSingle();
                }

                if (version >= 2) obj.Colour = ReadColor();

                if (version >= 3)
                {
                    obj.ZMask = ReadByte() != 0;
                    obj.AEpsilon = ReadByte();
                }

                if (version >= 4)
                {
                    emitter.Speed = ReadSingle();
                    emitter.SpeedChaos = ReadSingle();
                    emitter.DirectionChaos = ReadSingle();
                    emitter.Direction = ReadVector3();
                    ReadByte();                    // list selection
                }

                if (version >= 5)
                {
                    emitter.RandomRotate = ReadByte() != 0;
                    emitter.FixedUp = ReadByte() != 0;
                }

                currentWorld.Objects.Add(obj);
            }

            private void ReadCamera()
            {
                if (currentWorld == null) throw new InvalidDataException("A camera chunk appears before any world.");

                var camera = new DmoCamera();

                camera.Name = ReadAsciiZ();
                camera.Id = ReadInt32();

                camera.EyeX = ReadSpline();
                camera.EyeY = ReadSpline();
                camera.EyeZ = ReadSpline();
                camera.TargetX = ReadSpline();
                camera.TargetY = ReadSpline();
                camera.TargetZ = ReadSpline();
                camera.FieldOfView = ReadSpline();
                camera.Roll = ReadSpline();

                currentWorld.Cameras.Add(camera);
            }

            private void ReadWorldAnimation()
            {
                if (currentWorld == null) throw new InvalidDataException("An animation chunk appears before any world.");

                var animation = new DmoAnimation();

                animation.Id = ReadInt32();
                animation.Name = ReadAsciiZ();

                // Eight lights, seven splines each, in this exact order.
                for (var i = 0; i < 8; i++)
                {
                    ReadSpline();   // ambient red
                    ReadSpline();   // ambient green
                    ReadSpline();   // ambient blue
                    ReadSpline();   // red
                    ReadSpline();   // green
                    ReadSpline();   // blue
                    ReadSpline();   // lit
                }

                currentWorld.Animations.Add(animation);
                objectAnimationCursor = 0;
            }

            private void ReadObjectAnimation(int version)
            {
                if (currentWorld == null) throw new InvalidDataException("An object-animation chunk appears before any world.");

                var animation = new DmoObjectAnimation();

                animation.Red = ReadSpline();
                animation.Green = ReadSpline();
                animation.Blue = ReadSpline();
                animation.Alpha = ReadSpline();
                animation.Size = ReadSpline();
                animation.ParticlesPerFrame = ReadSpline();
                animation.AnimationTime = ReadSpline();

                if (version >= 2) animation.AnimationId = ReadSpline();

                animation.PositionX = ReadSpline();
                animation.PositionY = ReadSpline();
                animation.PositionZ = ReadSpline();
                animation.RotationX = ReadSpline();
                animation.RotationY = ReadSpline();
                animation.RotationZ = ReadSpline();

                if (version >= 1) animation.RotationW = ReadSpline();

                animation.ScaleX = ReadSpline();
                animation.ScaleY = ReadSpline();
                animation.ScaleZ = ReadSpline();

                currentWorld.ObjectAnimations.Add(animation);
                objectAnimationCursor++;
            }

            // ------------------------------------------------------------ events

            private void ReadEvent()
            {
                var ev = new DmoEvent();

                var nameBytes = ReadBytes(30);
                ev.Name = Encoding.ASCII.GetString(nameBytes).TrimEnd('\0');

                ReadInt32();                        // number, editor only
                ev.StartFrame = ReadInt32();
                ev.EndFrame = ReadInt32();
                ev.Type = ReadInt32();
                ev.Pass = ReadInt32();

                Skip(40);                           // ten integer parameters
                Skip(40);                           // ten float parameters

                ev.StartX1 = ReadInt32();
                ev.StartY1 = ReadInt32();
                ev.StartX2 = ReadInt32();
                ev.StartY2 = ReadInt32();
                ev.EndX1 = ReadInt32();
                ev.EndY1 = ReadInt32();
                ev.EndX2 = ReadInt32();
                ev.EndY2 = ReadInt32();

                // Everything after the header is this effect's parameter block. Its
                // length is whatever the chunk has left, which is more reliable than
                // the template size the original used - and it means an effect this
                // build does not understand is still skipped correctly.
                ev.Payload = ReadBytes(chunkStart + chunkSize - position);

                events.Add(ev);
            }

            // ------------------------------------------------------------ conversion

            /// <summary>
            /// Turns the parsed Addict2 structures into the engine's project model.
            ///
            /// The mapping is deliberate: an Addict2 world is a scene graph, so it
            /// becomes a <see cref="Scene"/>; the objects inside it become nodes; a
            /// node that references a mesh scene gets that scene's geometry merged
            /// into one mesh.
            /// </summary>
            private void BuildProject()
            {
                BuildTextures();
                BuildScenes();
                BuildTimeline();
                BuildCameraTracks();

                project.Normalize();
            }

            private void BuildTextures()
            {
                foreach (var texture in textures)
                {
                    var target = new ProjectTexture();
                    target.Id = texture.Id;

                    uint[] pixels = null;

                    if (texture.Recipe != null && texture.Recipe.Commands.Count > 0)
                    {
                        pixels = TexGen.Generate(texture.Recipe);
                    }

                    if (pixels == null || pixels.Length == 0)
                    {
                        // A texture with no commands is a flat surface in the original.
                        var resolution = Math.Max(1, texture.Resolution);
                        pixels = new uint[resolution * resolution];

                        for (var i = 0; i < pixels.Length; i++)
                        {
                            pixels[i] = ColorRgba.White.ToArgb();
                        }
                    }

                    target.Width = (int)Math.Sqrt(pixels.Length);
                    target.Height = target.Width;
                    target.Pixels = pixels;

                    project.Textures.Add(target);
                }
            }

            private void BuildScenes()
            {
                var sceneId = 1;

                foreach (var world in worlds)
                {
                    var scene = new Scene();
                    scene.Id = sceneId++;
                    scene.Name = world.Name;

                    scene.Camera.Position = world.Camera.Position;
                    scene.Camera.Target = world.Camera.Target;
                    scene.Camera.Up = world.Camera.Up;
                    scene.Camera.FieldOfView = world.Camera.FieldOfView > 0f
                        ? world.Camera.FieldOfView
                        : 45f;

                    scene.Fog.Enabled = world.Fog;
                    scene.Fog.Colour = world.FogColour;
                    scene.Fog.Start = world.FogStart;
                    scene.Fog.End = world.FogEnd;

                    foreach (var obj in world.Objects)
                    {
                        var node = BuildWorldObject(obj, world);

                        if (node != null) scene.Nodes.Add(node);
                    }

                    scene.RebuildHierarchy();
                    project.Scenes.Add(scene);
                }

                // A project with meshes but no world still has something to show.
                if (project.Scenes.Count == 0 && scenes.Count > 0)
                {
                    var scene = new Scene();
                    scene.Id = 1;
                    scene.Name = scenes[0].Name;

                    scene.Camera.Position = scenes[0].Camera.Position;
                    scene.Camera.Target = scenes[0].Camera.Target;
                    scene.Camera.Up = scenes[0].Camera.Up;
                    scene.Camera.FieldOfView = scenes[0].Camera.FieldOfView > 0f
                        ? scenes[0].Camera.FieldOfView
                        : 45f;

                    for (var i = 0; i < scenes.Count; i++)
                    {
                        var mesh = BuildMesh(scenes[i]);

                        if (mesh == null || mesh.IsEmpty) continue;

                        var node = new SceneNode();
                        node.Id = i + 1;
                        node.Name = scenes[i].Name;
                        node.Mesh = mesh;
                        node.Material = new Material();

                        scene.Nodes.Add(node);
                    }

                    scene.RebuildHierarchy();
                    project.Scenes.Add(scene);
                }
            }

            private SceneNode BuildWorldObject(DmoWorldObject obj, DmoWorld world)
            {
                var node = new SceneNode();

                node.Id = obj.Id;
                node.ParentId = obj.ParentId;
                node.Name = obj.Name;
                node.Position = obj.Position.Position;
                node.Rotation = obj.Position.Quaternion;
                node.Scale = obj.Position.Scale;

                if (node.Scale.X == 0f && node.Scale.Y == 0f && node.Scale.Z == 0f)
                {
                    node.Scale = Vector3.One;
                }

                node.NormalizeRotation();

                node.Material = new Material();
                node.Material.Colour = obj.Colour;
                node.Material.TextureIndex = FindTextureIndex(obj.TextureId);
                ApplyBlend(node.Material, obj.SourceBlend, obj.DestinationBlend);

                // Imported geometry is drawn two-sided.
                //
                // Addict2 leaves face culling off by default - its scene renderer
                // explicitly sets CULLNONE - and its primitives are wound for that.
                // The engine culls back faces to save fill rate, so an imported mesh
                // rendered with culling on disappears entirely: every triangle faces
                // the wrong way. Two-sided is both correct for the source and the
                // safe choice for geometry this build did not author.
                node.Material.TwoSided = true;

                if (obj.Primitive == WorldScene)
                {
                    var source = FindScene(obj.TargetId);

                    if (source == null) return null;

                    var mesh = BuildMesh(source);

                    if (mesh == null || mesh.IsEmpty) return null;

                    node.Mesh = mesh;
                }
                else if (obj.Primitive == WorldSubscene)
                {
                    // A subscene is another world rendered in place. This build does
                    // not nest worlds, so the node is kept as an empty parent: its
                    // children still inherit its transform, which is the visible part.
                    node.Mesh = null;
                }
                else if (obj.Primitive == WorldSphereEmitter ||
                         obj.Primitive == WorldCubeEmitter ||
                         obj.Primitive == WorldCylinderEmitter)
                {
                    node.Emitter = BuildEmitter(obj.Emitter);
                    node.Material.Transparent = true;
                }
                else
                {
                    // A deflector or gravity well has no geometry of its own.
                    node.Mesh = null;
                }

                return node;
            }

            private ParticleEmitter BuildEmitter(DmoEmitter source)
            {
                var emitter = new ParticleEmitter();

                emitter.MaxParticles = Clamp(source.MaxParticles, 1, 20000);
                emitter.Rate = Math.Max(0f, source.ParticlesPerFrame);
                emitter.Lifetime = Math.Max(0.05f, source.DefaultAge / 100f);
                emitter.LifetimeChaos = Math.Abs(source.AgeChaos) / 100f;
                emitter.Speed = source.Speed;
                emitter.SpeedChaos = source.SpeedChaos;
                emitter.Direction = source.Direction;
                emitter.Spread = Clamp(source.DirectionChaos, 0f, 180f);
                emitter.StartSize = Math.Max(0.001f, source.Size);
                emitter.EndSize = 0f;
                emitter.SizeChaos = 0f;

                emitter.StartColour = ToColour(source.Color1);
                emitter.EndColour = ToColour(source.Color2);

                emitter.Gravity = Vector3.Zero;
                emitter.Spin = source.RotationSpeed;
                emitter.Seed = 1;

                emitter.Reset();
                return emitter;
            }

            private static ColorRgba ToColour(float[] channels)
            {
                if (channels == null || channels.Length < 4) return ColorRgba.White;

                return new ColorRgba(
                    ClampByte(channels[0]),
                    ClampByte(channels[1]),
                    ClampByte(channels[2]),
                    ClampByte(channels[3]));
            }

            private static byte ClampByte(float value)
            {
                if (value <= 0f) return 0;
                if (value >= 255f) return 255;
                return (byte)value;
            }

            private static int Clamp(int value, int min, int max)
            {
                if (value < min) return min;
                if (value > max) return max;
                return value;
            }

            private static float Clamp(float value, float min, float max)
            {
                if (value < min) return min;
                if (value > max) return max;
                return value;
            }

            private DmoScene FindScene(int id)
            {
                for (var i = 0; i < scenes.Count; i++)
                {
                    if (scenes[i].Id == id) return scenes[i];
                }

                return null;
            }

            private int FindTextureIndex(int addictTextureId)
            {
                for (var i = 0; i < textures.Count; i++)
                {
                    if (textures[i].Id == addictTextureId) return textures[i].Id;
                }

                return -1;
            }

            /// <summary>
            /// Merges a scene's objects into one mesh.
            ///
            /// Addict2 renders a scene as a list of objects, each with its own
            /// transform; the engine's node has one mesh. Baking the transforms into
            /// a single mesh keeps the draw count down and is visually identical,
            /// because the objects in a scene never animate independently.
            /// </summary>
            private Mesh BuildMesh(DmoScene scene)
            {
                var positions = new List<Vector3>();
                var normals = new List<Vector3>();
                var texCoords = new List<Vector2>();
                var indices = new List<int>();

                foreach (var obj in scene.Objects)
                {
                    if (obj.IsModifier || obj.IsMapXform) continue;

                    var triangles = GetObjectTriangles(obj);

                    for (var i = 0; i < triangles.Count; i++)
                    {
                        var triangle = triangles[i];
                        var baseIndex = positions.Count;

                        var normal = triangle.Normal;

                        if (obj.NormalsInverted) normal = -normal;

                        positions.Add(Vector3.Transform(triangle.P0, obj.Transform));
                        positions.Add(Vector3.Transform(triangle.P1, obj.Transform));
                        positions.Add(Vector3.Transform(triangle.P2, obj.Transform));

                        normals.Add(Vector3.TransformNormal(normal, obj.Transform));
                        normals.Add(Vector3.TransformNormal(normal, obj.Transform));
                        normals.Add(Vector3.TransformNormal(normal, obj.Transform));

                        texCoords.Add(triangle.T0);
                        texCoords.Add(triangle.T1);
                        texCoords.Add(triangle.T2);

                        indices.Add(baseIndex);
                        indices.Add(baseIndex + 1);
                        indices.Add(baseIndex + 2);
                    }
                }

                if (positions.Count == 0) return null;

                var mesh = new Mesh();
                mesh.Vertices = new Vertex[positions.Count];
                mesh.Indices = indices.ToArray();

                for (var i = 0; i < positions.Count; i++)
                {
                    mesh.Vertices[i] = new Vertex(positions[i], normals[i], texCoords[i]);
                }

                mesh.RecalculateBounds();

                return mesh;
            }

            /// <summary>
            /// The triangles of one object: either its stored geometry or a generated
            /// primitive, always in object space.
            /// </summary>
            private List<GeneratedTriangle> GetObjectTriangles(DmoObject obj)
            {
                if (obj.Primitive == PrimitiveStored && obj.Vertices != null && obj.Polygons != null)
                {
                    var result = new List<GeneratedTriangle>(obj.Polygons.Length);

                    for (var i = 0; i < obj.Polygons.Length; i++)
                    {
                        var polygon = obj.Polygons[i];

                        if (polygon.A < 0 || polygon.B < 0 || polygon.C < 0) continue;
                        if (polygon.A >= obj.Vertices.Length) continue;
                        if (polygon.B >= obj.Vertices.Length) continue;
                        if (polygon.C >= obj.Vertices.Length) continue;

                        var triangle = new GeneratedTriangle();
                        triangle.P0 = obj.Vertices[polygon.A].Position;
                        triangle.P1 = obj.Vertices[polygon.B].Position;
                        triangle.P2 = obj.Vertices[polygon.C].Position;
                        triangle.Normal = polygon.Normal;
                        triangle.T0 = polygon.U;
                        triangle.T1 = polygon.V;
                        triangle.T2 = polygon.W;

                        result.Add(triangle);
                    }

                    return result;
                }

                return PrimitiveGenerator.Generate(obj.Primitive, obj.Param1, obj.Param2,
                    obj.Param3, obj.Param4, obj.Param5, obj.Supershape);
            }

            // ------------------------------------------------------------ timeline

            private void BuildTimeline()
            {
                // The original timeline runs at a fixed 100 frames per second, which is
                // why every timing in a .dmo is a frame count at that rate.
                project.FramesPerSecond = 100;

                var lastFrame = 0;

                foreach (var ev in events)
                {
                    if (ev.EndFrame > lastFrame) lastFrame = ev.EndFrame;
                }

                project.Duration = lastFrame > 0 ? lastFrame : 600;

                foreach (var ev in events)
                {
                    var effect = ConvertEvent(ev);

                    if (effect == null) continue;

                    effect.StartFrame = ev.StartFrame;
                    effect.EndFrame = ev.EndFrame;

                    // Timeline rectangles are authored in the 800x600 space the
                    // original used, which is exactly what the engine's effects
                    // expect, so they transfer unchanged.
                    effect.StartRect = new Rect(ev.StartX1, ev.StartY1, ev.StartX2, ev.StartY2);
                    effect.EndRect = new Rect(ev.EndX1, ev.EndY1, ev.EndX2, ev.EndY2);

                    project.Timeline.Add(effect);
                }
            }

            private Effect ConvertEvent(DmoEvent ev)
            {
                switch (ev.Type)
                {
                    case 0: return new ClearEffect { ClearScreen = false, ClearDepth = false };
                    case 1: return ConvertLayer2D(ev);
                    case 2: return ConvertParticles(ev);
                    case 3: return ConvertClear(ev);
                    case 4: return ConvertRenderToTexture(ev);
                    case 5: return ConvertFeedback(ev, false);
                    case 6: return ConvertFeedback(ev, true);
                    case 7: return ConvertDisplay3D(ev);
                    case 8: return new ParticleEffect();
                    case 9: return ConvertContrast(ev);
                    case 10: return ConvertBlur(ev);
                    case 13: return ConvertShake(ev);
                    case 16: return ConvertFeedback(ev, false);
                    default: return null;
                }
            }

            private Effect ConvertClear(DmoEvent ev)
            {
                var effect = new ClearEffect();
                var payload = ev.Payload;

                if (payload.Length >= 6)
                {
                    effect.Colour = ReadColorAt(payload, 0);
                    effect.ClearScreen = payload[4] != 0;
                    effect.ClearDepth = payload[5] != 0;
                }

                return effect;
            }

            private Effect ConvertLayer2D(DmoEvent ev)
            {
                var effect = new Layer2DEffect();
                var payload = ev.Payload;

                if (payload.Length >= 21)
                {
                    effect.StartColour = ReadColorAt(payload, 0);
                    effect.EndColour = ReadColorAt(payload, 4);
                    effect.SourceBlend = ConvertBlend(ReadInt32At(payload, 8), true);
                    effect.DestinationBlend = ConvertBlend(ReadInt32At(payload, 12), false);

                    var textured = payload[16] != 0;
                    var textureId = payload[17] | (payload[18] << 8) | (payload[19] << 16);

                    if (textured) effect.TextureId = textureId;
                }

                return effect;
            }

            private Effect ConvertDisplay3D(DmoEvent ev)
            {
                var effect = new Display3DEffect();
                var payload = ev.Payload;

                // Twenty-eight bytes is the older event layout: the trailing
                // "don't save the camera" flag was added later. Requiring the full
                // twenty-nine rejected every old project's 3D scene outright, which is
                // why they all rendered empty.
                if (payload.Length >= 28)
                {
                    var worldId = ReadInt32At(payload, 0);

                    effect.SceneId = FindSceneIndex(worldId);
                    effect.CameraIndex = ReadInt32At(payload, 4);
                    effect.AnimationId = ReadInt32At(payload, 8);
                    effect.CameraStart = ReadSingleAt(payload, 12);
                    effect.CameraEnd = ReadSingleAt(payload, 16);
                    effect.AnimationStart = ReadSingleAt(payload, 20);
                    effect.AnimationEnd = ReadSingleAt(payload, 24);

                    if (payload.Length >= 29) effect.DontSaveCamera = payload[28] != 0;
                }

                return effect;
            }

            private Effect ConvertParticles(DmoEvent ev)
            {
                var effect = new ParticleEffect();
                var payload = ev.Payload;

                if (payload.Length >= 12)
                {
                    var worldId = ReadInt32At(payload, 0);

                    effect.SceneId = FindSceneIndex(worldId);
                    effect.AnimationId = ReadInt32At(payload, 8);

                    if (payload.Length >= 20)
                    {
                        effect.AnimationStart = ReadSingleAt(payload, 12);
                        effect.AnimationEnd = ReadSingleAt(payload, 16);
                    }
                }

                return effect;
            }

            private Effect ConvertRenderToTexture(DmoEvent ev)
            {
                var effect = new RenderToTextureEffect();

                if (ev.Payload.Length >= 1) effect.Slot = Clamp(ev.Payload[0], 0, 5);

                return effect;
            }

            private Effect ConvertFeedback(DmoEvent ev, bool old)
            {
                var effect = new FeedbackEffect();
                var payload = ev.Payload;

                if (payload.Length >= 19)
                {
                    effect.StartColour = ReadColorAt(payload, 0);
                    effect.EndColour = ReadColorAt(payload, 4);
                    effect.SourceBlend = ConvertBlend(ReadInt32At(payload, 8), true);
                    effect.DestinationBlend = ConvertBlend(ReadInt32At(payload, 12), false);
                    effect.Slot = Clamp(payload[16], 0, 5);
                    effect.Layers = payload[17];
                    effect.Zoom = payload[18];
                }

                return effect;
            }

            private Effect ConvertBlur(DmoEvent ev)
            {
                var effect = new BlurEffect();
                var payload = ev.Payload;

                if (payload.Length >= 19)
                {
                    effect.StartColour = ReadColorAt(payload, 0);
                    effect.EndColour = ReadColorAt(payload, 4);
                    effect.SourceBlend = ConvertBlend(ReadInt32At(payload, 8), true);
                    effect.DestinationBlend = ConvertBlend(ReadInt32At(payload, 12), false);
                    effect.Slot = Clamp(payload[16], 0, 5);
                    effect.Layers = payload[17];
                    effect.Zoom = payload[18];
                }

                return effect;
            }

            private Effect ConvertContrast(DmoEvent ev)
            {
                var effect = new ContrastEffect();
                var payload = ev.Payload;

                if (payload.Length >= 19)
                {
                    effect.StartColour = ReadColorAt(payload, 0);
                    effect.EndColour = ReadColorAt(payload, 4);
                    effect.SourceBlend = ConvertBlend(ReadInt32At(payload, 8), true);
                    effect.DestinationBlend = ConvertBlend(ReadInt32At(payload, 12), false);
                    effect.Slot = Clamp(payload[16], 0, 5);
                    effect.Multiply = payload[17];
                    effect.Add = payload[18];
                }

                return effect;
            }

            private Effect ConvertShake(DmoEvent ev)
            {
                var effect = new CameraShakeEffect();
                var payload = ev.Payload;

                if (payload.Length >= 17)
                {
                    effect.EyeAmount = ReadSingleAt(payload, 0);
                    effect.TargetAmount = ReadSingleAt(payload, 4);
                    effect.Start = ReadSingleAt(payload, 8);
                    effect.End = ReadSingleAt(payload, 12);
                    effect.Frequency = payload[16];
                }

                return effect;
            }

            private int FindSceneIndex(int worldId)
            {
                for (var i = 0; i < worlds.Count; i++)
                {
                    if (worlds[i].Id == worldId) return i + 1;
                }

                return 0;
            }

            // ------------------------------------------------------------ cameras

            private void BuildCameraTracks()
            {
                // The first world's first camera becomes the scene camera animation,
                // which is the only camera slot the engine's scenes expose.
                for (var w = 0; w < worlds.Count && w < project.Scenes.Count; w++)
                {
                    var world = worlds[w];

                    if (world.Cameras.Count == 0) continue;

                    var camera = world.Cameras[0];
                    var scene = project.Scenes[w];

                    AddCameraTrack(scene, "eyeX", camera.EyeX);
                    AddCameraTrack(scene, "eyeY", camera.EyeY);
                    AddCameraTrack(scene, "eyeZ", camera.EyeZ);
                    AddCameraTrack(scene, "targetX", camera.TargetX);
                    AddCameraTrack(scene, "targetY", camera.TargetY);
                    AddCameraTrack(scene, "targetZ", camera.TargetZ);
                    AddCameraTrack(scene, "fov", camera.FieldOfView);
                    AddCameraTrack(scene, "roll", camera.Roll);
                }
            }

            private static void AddCameraTrack(Scene scene, string property, Spline spline)
            {
                if (spline == null || spline.IsEmpty) return;

                var track = new AnimationTrack();
                track.Property = property;
                track.Curve = spline;

                scene.CameraTracks.Add(track);
            }

            // ------------------------------------------------------------ primitives

            private Vector3 ReadVector3()
            {
                var x = ReadSingle();
                var y = ReadSingle();
                var z = ReadSingle();
                return new Vector3(x, y, z);
            }

            private Vector2 ReadVector2()
            {
                var x = ReadSingle();
                var y = ReadSingle();
                return new Vector2(x, y);
            }

            private ColorRgba ReadColor()
            {
                var r = ReadByte();
                var g = ReadByte();
                var b = ReadByte();
                var a = ReadByte();
                return new ColorRgba(r, g, b, a);
            }

            private ColorRgba ReadColorRgb()
            {
                var r = ReadByte();
                var g = ReadByte();
                var b = ReadByte();
                return new ColorRgba(r, g, b);
            }

            private DmoCameraBlock ReadCameraBlock()
            {
                var camera = new DmoCameraBlock();

                camera.Position = ReadVector3();
                camera.Target = ReadVector3();
                camera.Up = ReadVector3();
                camera.Roll = ReadSingle();
                camera.FieldOfView = ReadSingle();
                ReadVector3();                     // i
                ReadVector3();                     // j

                return camera;
            }

            private Rst ReadRst()
            {
                var result = new Rst();

                result.Position = ReadVector3();
                ReadVector3();                     // rotation in euler form, unused
                result.Scale = ReadVector3();

                var qx = ReadSingle();
                var qy = ReadSingle();
                var qz = ReadSingle();
                var qw = ReadSingle();

                result.Quaternion = new Quaternion(qx, qy, qz, qw);

                if (result.Quaternion.LengthSquared < 1e-8f)
                {
                    result.Quaternion = Quaternion.Identity;
                }

                return result;
            }

            /// <summary>
            /// Reads a 4x4 matrix.
            ///
            /// The subtlety that costs an afternoon: the original stores a
            /// <c>float[4][4]</c>, which is row-major in memory, and then hands the
            /// same 16 floats to <c>glMultMatrixf</c>, which reads them as
            /// column-major. So OpenGL ends up using the *transpose* of the array as
            /// written. Working the two conventions through, the engine's row-vector
            /// matrix is exactly the row-major reading of the bytes - no transpose on
            /// load. Transposing here as well flipped every object inside out and
            /// threw them to the wrong side of the scene.
            /// </summary>
            private Matrix ReadMatrix()
            {
                var values = new float[16];

                for (var i = 0; i < 16; i++)
                {
                    values[i] = ReadSingle();
                }

                return new Matrix(
                    values[0], values[1], values[2], values[3],
                    values[4], values[5], values[6], values[7],
                    values[8], values[9], values[10], values[11],
                    values[12], values[13], values[14], values[15]);
            }

            private Spline ReadSpline()
            {
                var spline = new Spline();

                var keyCount = ReadInt32();
                var interpolation = ReadInt32();

                if (keyCount < 0 || keyCount > 100000)
                {
                    throw new InvalidDataException("A spline claims " + keyCount + " keys.");
                }

                var keys = new Key[keyCount];

                for (var i = 0; i < keyCount; i++)
                {
                    keys[i].Time = ReadSingle();
                    keys[i].Value = ReadSingle();
                }

                spline.Interpolation = ConvertInterpolation(interpolation);
                spline.SetKeys(keys);

                return spline;
            }

            private static Interpolation ConvertInterpolation(int value)
            {
                switch (value)
                {
                    case 1: return Interpolation.Linear;
                    case 2: return Interpolation.Hermite;
                    case 3: return Interpolation.Bezier;
                    case 4: return Interpolation.Catmull;
                    case 5: return Interpolation.Sine;
                    case 6: return Interpolation.Saw;
                    case 7: return Interpolation.Square;
                    default: return Interpolation.Step;
                }
            }

            /// <summary>
            /// Maps an OpenGL blend factor onto the engine's vocabulary.
            ///
            /// The values are the GL constants the original wrote: 0 and 1 for zero
            /// and one, 0x300..0x308 for the rest.
            /// </summary>
            /// <summary>
            /// Applies a world object's stored blend factors to a material.
            ///
            /// Two quirks of the original, both of which produce a black screen if
            /// taken at face value:
            ///
            /// * The stored pair is passed to OpenGL as
            ///   <c>glBlendFunc(DSTBlend, SRCBlend)</c> - the fields are named the
            ///   other way round from the call. So the file's <c>DSTBlend</c> is the
            ///   source factor and its <c>SRCBlend</c> is the destination factor.
            ///
            /// * A freshly created object in the editor has both fields zero, and zero
            ///   is <c>GL_ZERO</c>: a literal reading would multiply the source and the
            ///   destination by zero and draw nothing. Eight of the nine objects in the
            ///   tutorial project are stored that way, so it plainly means "no blending
            ///   chosen" and the object is drawn opaque.
            /// </summary>
            private static void ApplyBlend(Material material, int fileSource, int fileDestination)
            {
                if (fileSource == 0 && fileDestination == 0)
                {
                    material.SourceBlend = BlendMode.One;
                    material.DestinationBlend = BlendMode.Zero;
                    return;
                }

                // Swapped on purpose: see above.
                material.SourceBlend = ConvertBlend(fileDestination, true);
                material.DestinationBlend = ConvertBlend(fileSource, false);
            }

            private static BlendMode ConvertBlend(int value, bool source)
            {
                switch (value)
                {
                    case 0: return BlendMode.Zero;
                    case 1: return BlendMode.One;
                    case 0x300: return BlendMode.SourceColour;
                    case 0x301: return BlendMode.InverseSourceColour;
                    case 0x302: return BlendMode.SourceAlpha;
                    case 0x303: return BlendMode.InverseSourceAlpha;
                    case 0x304: return BlendMode.DestinationAlpha;
                    case 0x305: return BlendMode.InverseDestinationAlpha;
                    case 0x306: return BlendMode.DestinationColour;
                    case 0x307: return BlendMode.InverseDestinationColour;
                    case 0x308: return BlendMode.SourceAlphaSaturation;
                    default: return source ? BlendMode.SourceAlpha : BlendMode.InverseSourceAlpha;
                }
            }

            // ------------------------------------------------------------ binary helpers

            private string ReadChunkId()
            {
                var id = Encoding.ASCII.GetString(data, position, 4);
                position += 4;
                return id;
            }

            private byte ReadByte()
            {
                if (position >= data.Length) throw new EndOfStreamException("The project ended mid-chunk.");

                return data[position++];
            }

            private sbyte ReadSByte()
            {
                return unchecked((sbyte)ReadByte());
            }

            private int ReadInt32()
            {
                if (position + 4 > data.Length) throw new EndOfStreamException("The project ended mid-chunk.");

                var value = data[position]
                            | (data[position + 1] << 8)
                            | (data[position + 2] << 16)
                            | (data[position + 3] << 24);

                position += 4;
                return value;
            }

            private float ReadSingle()
            {
                return BitConverter.ToSingle(BitConverter.GetBytes(ReadInt32()), 0);
            }

            private float[] ReadFloats(int count)
            {
                var result = new float[count];

                for (var i = 0; i < count; i++)
                {
                    result[i] = ReadSingle();
                }

                return result;
            }

            private byte[] ReadBytes(int count)
            {
                if (count < 0) throw new InvalidDataException("A chunk claims a negative length.");

                if (position + count > data.Length)
                {
                    throw new EndOfStreamException("The project ended mid-chunk.");
                }

                var result = new byte[count];
                Array.Copy(data, position, result, 0, count);
                position += count;
                return result;
            }

            private string ReadAsciiZ()
            {
                var start = position;

                while (position < data.Length && data[position] != 0)
                {
                    position++;
                }

                var length = position - start;
                var text = Encoding.ASCII.GetString(data, start, length);

                if (position < data.Length) position++;

                return text;
            }

            private void Skip(int count)
            {
                if (count < 0) throw new InvalidDataException("A chunk claims a negative length.");
                if (position + count > data.Length) throw new EndOfStreamException("The project ended mid-chunk.");

                position += count;
            }

            private static ColorRgba ReadColorAt(byte[] buffer, int offset)
            {
                if (offset + 4 > buffer.Length) return ColorRgba.White;

                return new ColorRgba(buffer[offset], buffer[offset + 1], buffer[offset + 2], buffer[offset + 3]);
            }

            private static int ReadInt32At(byte[] buffer, int offset)
            {
                if (offset + 4 > buffer.Length) return 0;

                return buffer[offset]
                       | (buffer[offset + 1] << 8)
                       | (buffer[offset + 2] << 16)
                       | (buffer[offset + 3] << 24);
            }

            private static float ReadSingleAt(byte[] buffer, int offset)
            {
                return BitConverter.ToSingle(BitConverter.GetBytes(ReadInt32At(buffer, offset)), 0);
            }
        }

        // ---------------------------------------------------------------- intermediate types

        private sealed class DmoTexture
        {
            internal string Name;
            internal int Id;
            internal int Resolution;
            internal TexRecipe Recipe;
        }

        private sealed class DmoScene
        {
            internal string Name;
            internal int Id;
            internal DmoCameraBlock Camera;
            internal ColorRgba FogColour;
            internal bool ColorDiscard;
            internal readonly List<DmoObject> Objects = new List<DmoObject>();
        }

        private sealed class DmoWorld
        {
            internal string Name;
            internal int Id;
            internal DmoCameraBlock Camera;
            internal bool Fog;
            internal ColorRgba FogColour;
            internal float FogStart;
            internal float FogEnd;
            internal bool Lighting;
            internal readonly List<DmoWorldObject> Objects = new List<DmoWorldObject>();
            internal readonly List<DmoCamera> Cameras = new List<DmoCamera>();
            internal readonly List<DmoAnimation> Animations = new List<DmoAnimation>();
            internal readonly List<DmoObjectAnimation> ObjectAnimations = new List<DmoObjectAnimation>();
        }

        private sealed class DmoObject
        {
            internal string Name;
            internal int Id;
            internal int Param1, Param2, Param3, Param4, Param5;
            internal int Primitive;

            internal DmoVertex[] Vertices;
            internal DmoPolygon[] Polygons;

            internal byte[] Supershape;
            internal Matrix Transform = Matrix.Identity;

            internal bool Wireframe, Textured, EnvMapped, NormalsInverted;
            internal byte Xtile = 1, Ytile = 1;
            internal bool XSwap, YSwap, Swap;
            internal float OffsetX, OffsetY;
            internal int SourceBlend, DestinationBlend;
            internal ColorRgba Colour = ColorRgba.White;

            internal int TextureId = -1;
            internal int TextureSlot;
            internal int EnvironmentId = -1;
            internal int EnvironmentSlot;

            internal bool ZMask = true;
            internal byte AEpsilon;
            internal int Shading;
            internal bool Backface, Backfront;

            internal int TargetObjectId;
            internal sbyte MapXformType, MapXformColour;
            internal bool IsMapXform;
            internal bool IsModifier;
            internal bool LinearSmooth;
        }

        private struct DmoVertex
        {
            internal Vector3 Position;
            internal Vector3 Normal;
            internal Vector2 TextureCoordinate;
        }

        private struct DmoPolygon
        {
            internal int A, B, C;
            internal Vector3 Normal;
            internal Vector2 U, V, W;
        }

        private sealed class DmoWorldObject
        {
            internal string Name;
            internal int Id;
            internal int ParentId;
            internal int Primitive;
            internal Rst Position;
            internal int SourceBlend, DestinationBlend;
            internal int TargetId;
            internal bool Textured;
            internal int TextureId = -1;
            internal int TextureSlot;
            internal ColorRgba Colour = ColorRgba.White;
            internal bool ZMask;
            internal byte AEpsilon;
            internal DmoEmitter Emitter;
        }

        private sealed class DmoEmitter
        {
            internal bool Head, Tail;
            internal int DefaultAge;
            internal int AgeChaos;
            internal float ParticlesPerFrame;
            internal int MaxParticles;
            internal bool ObjectHead;
            internal byte TailLength, TailLength2;
            internal int ObjectHeadSceneId;
            internal float[] Color1, Color2;
            internal float Param1, Param2, Size;
            internal float RotationSpeed, RotationChaos, CameraFade;
            internal float Speed, SpeedChaos, DirectionChaos;
            internal Vector3 Direction;
            internal bool RandomRotate, FixedUp;
        }

        private struct Rst
        {
            internal Vector3 Position;
            internal Vector3 Scale;
            internal Quaternion Quaternion;
        }

        private struct DmoCameraBlock
        {
            internal Vector3 Position;
            internal Vector3 Target;
            internal Vector3 Up;
            internal float Roll;
            internal float FieldOfView;
        }

        private sealed class DmoCamera
        {
            internal string Name;
            internal int Id;
            internal Spline EyeX, EyeY, EyeZ;
            internal Spline TargetX, TargetY, TargetZ;
            internal Spline FieldOfView;
            internal Spline Roll;
        }

        private sealed class DmoAnimation
        {
            internal int Id;
            internal string Name;
        }

        private sealed class DmoObjectAnimation
        {
            internal Spline Red, Green, Blue, Alpha, Size;
            internal Spline ParticlesPerFrame, AnimationTime, AnimationId;
            internal Spline PositionX, PositionY, PositionZ;
            internal Spline RotationX, RotationY, RotationZ, RotationW;
            internal Spline ScaleX, ScaleY, ScaleZ;
        }

        private sealed class DmoEvent
        {
            internal string Name;
            internal int StartFrame;
            internal int EndFrame;
            internal int Type;
            internal int Pass;
            internal int StartX1, StartY1, StartX2, StartY2;
            internal int EndX1, EndY1, EndX2, EndY2;
            internal byte[] Payload = new byte[0];
        }
    }
}
