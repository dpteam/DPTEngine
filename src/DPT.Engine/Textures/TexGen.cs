//
// DPT.Engine - Addict2 procedural texture generator (TexGen) port.
// Copyright (C) 2026 DartPower Team. GPLv2.
//
// An Addict2 .dmo project does not store texture pixels: it stores a *recipe*,
// a list of filters with their parameter blocks. This file is the executor for
// that recipe. Ported from refs/Some2/aDDict2/TexGen2.cpp; every filter method
// names the C++ function it reproduces.
//
// ---------------------------------------------------------------------------
// Pixel representation
// ---------------------------------------------------------------------------
// The C++ uses a packed RGBA struct whose bytes in memory are r,g,b,a, so a
// little-endian dword reads as 0xAABBGGRR. The engine's pixel type is
// 0xAARRGGBB, so the layer buffers here are ARGB and every access to a C++
// `.c[i]` goes through GetChannel/SetChannel, which map C++ channel 0..3
// (r,g,b,a) to the matching ARGB byte.
//
// Code that only moves whole dwords (Copy, Invert, Max, the blend operators)
// is independent of the byte order and is ported literally.
//
// ---------------------------------------------------------------------------
// Layer model
// ---------------------------------------------------------------------------
// Eight RGBA layers, all initialised to opaque black (a=255, rgb=0), exactly
// as TEXTURE::TEXTURE does. A command copies its target layer into a working
// buffer, the filter rewrites that buffer, and then the command's operator
// blends the buffer back into the layer. Filters that need a second layer (Map,
// Shade, Mix, ...) read the *committed* layers, never the working buffer -
// which is why the working copy exists at all.
//
// ---------------------------------------------------------------------------
// Deliberate deviations from the C++ (all of them are places where the original
// read uninitialised memory or divided by zero, so it had no defined output):
//   * DirBlur with Depth == 0 leaves the layer untouched instead of copying an
//     uninitialised buffer over it.
//   * Crystalize with Points == 0 leaves the layer untouched (the C++ indexed an
//     uninitialised `dots` array).
//   * Pixelize with a zero block count is skipped (the C++ divided by zero).
//   * Crystalize's source index is wrapped instead of read out of bounds.
//   * The sub-plasma octave buffers are cleared first so that a malformed
//     octave count cannot sample leftover heap.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DPT.Engine.Textures
{
    // The engine has its own DPT.Engine.Graphics namespace, which wins over a
    // file-scope alias because it is a member of an enclosing namespace. Declaring
    // the aliases inside this namespace body makes them take precedence, so the
    // Text and Jpeg filters can name the System.Drawing types directly.
    using Bitmap = System.Drawing.Bitmap;
    using BitmapData = System.Drawing.Imaging.BitmapData;
    using Brush = System.Drawing.Brush;
    using Color = System.Drawing.Color;
    using Font = System.Drawing.Font;
    using FontFamily = System.Drawing.FontFamily;
    using FontStyle = System.Drawing.FontStyle;
    using Graphics = System.Drawing.Graphics;
    using GraphicsUnit = System.Drawing.GraphicsUnit;
    using Image = System.Drawing.Image;
    using ImageLockMode = System.Drawing.Imaging.ImageLockMode;
    using InterpolationMode = System.Drawing.Drawing2D.InterpolationMode;
    using PixelFormat = System.Drawing.Imaging.PixelFormat;
    using PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode;
    using Rectangle = System.Drawing.Rectangle;
    using SolidBrush = System.Drawing.SolidBrush;
    using StringFormat = System.Drawing.StringFormat;
    using TextRenderingHint = System.Drawing.Text.TextRenderingHint;

    /// <summary>
    /// One command in a texture recipe, as stored in the .dmo file.
    /// </summary>
    public sealed class TexCommand
    {
        /// <summary>One of the TEXGEN_* constants in <see cref="TexGen"/>.</summary>
        public int Filter;

        /// <summary>Layer the command reads and writes, 0..7.</summary>
        public int Layer;

        /// <summary>Blend operator, 0..3 (see <see cref="TexGen"/>).</summary>
        public int Operator;

        /// <summary>
        /// Which colour channels the operator applies to, 0..15: bit 0 = red,
        /// bit 1 = green, bit 2 = blue, bit 3 = alpha.
        /// </summary>
        public int OperatorMask;

        /// <summary>The raw filter parameter block, already read from the file.</summary>
        public byte[] Data;
    }

    /// <summary>
    /// The recipe for one texture.
    /// </summary>
    public sealed class TexRecipe
    {
        public int Id;

        /// <summary>Texture size, always a power of two.</summary>
        public int Resolution;

        public List<TexCommand> Commands;

        public TexRecipe()
        {
            Commands = new List<TexCommand>();
        }
    }

    /// <summary>
    /// Executes Addict2 texture recipes.
    /// </summary>
    public static class TexGen
    {
        // ------------------------------------------------------------------
        // Filter ids, straight from TexGen2.h.
        // ------------------------------------------------------------------

        public const int TEXGEN_Envmap = 0;
        public const int TEXGEN_Plasma = 1;
        public const int TEXGEN_Map = 2;
        public const int TEXGEN_Blur = 3;
        public const int TEXGEN_DirBlur = 4;
        public const int TEXGEN_Text = 5;
        public const int TEXGEN_SubPlasma = 6;
        public const int TEXGEN_FractalPlasma = 7;
        public const int TEXGEN_Colorize = 8;
        public const int TEXGEN_Shade = 9;
        public const int TEXGEN_Brightness = 10;
        public const int TEXGEN_Copy = 11;
        public const int TEXGEN_Cells = 12;
        public const int TEXGEN_Twirl = 13;
        public const int TEXGEN_SineDist = 14;
        public const int TEXGEN_Mix = 15;
        public const int TEXGEN_MixMap = 16;
        public const int TEXGEN_Scale = 17;
        public const int TEXGEN_SineColor = 18;
        public const int TEXGEN_Max = 19;
        public const int TEXGEN_HSV = 20;
        public const int TEXGEN_Emboss = 21;
        public const int TEXGEN_Invert = 22;
        public const int TEXGEN_Glass = 23;
        public const int TEXGEN_Pixelize = 24;
        public const int TEXGEN_Offset = 25;
        public const int TEXGEN_Crystalize = 26;
        public const int TEXGEN_Rectangle = 27;
        public const int TEXGEN_Circle = 28;
        public const int TEXGEN_Contrast = 29;
        public const int TEXGEN_MakeMaterial = 30;
        public const int TEXGEN_Gradient = 31;
        public const int TEXGEN_Rotozoom = 32;
        public const int TEXGEN_ChamferRectangle = 33;
        public const int TEXGEN_Dots = 34;
        public const int TEXGEN_Jpeg = 35;
        public const int TEXGEN_FractalPlasmaOld = 36;

        // Blend operators (opproc[] in TexGen2.cpp).
        private const int OperatorReplace = 0; // "X"
        private const int OperatorAdd = 1;     // "+"
        private const int OperatorSub = 2;     // "-"
        private const int OperatorMul = 3;     // "*"

        private const int LayerCount = 8;

        /// <summary>
        /// The value every layer starts at: TEXTURE::TEXTURE memsets to 0 and then
        /// sets a=255 on every pixel, i.e. opaque black.
        /// </summary>
        private const uint InitialPixel = 0xFF000000u;

        // Fonts available to the Text filter (TexGen2.cpp: fonts[]).
        private static readonly string[] TextFonts =
        {
            "Arial", "Verdana", "Courier New", "Webdings", "Lucida Console",
            "Tahoma", "Impact", "Wingdings", "Flakey", "Microsoft Sans Serif",
            "Trebuchet MS", "Symbol"
        };

        /// <summary>
        /// Executes a recipe and returns ARGB pixels (0xAARRGGBB), row-major,
        /// top-left origin, exactly Resolution*Resolution entries.
        ///
        /// Never returns null. A command whose parameter block is too short is
        /// skipped, an unknown filter is skipped, and a filter that throws is
        /// skipped; the layer keeps whatever it had, so a broken recipe still
        /// renders as a (possibly flat) texture rather than taking the engine down.
        ///
        /// Which layer comes back: the texture the original produced was whatever
        /// layer the recipe's TEXGEN_MakeMaterial command last copied into a
        /// material slot, so the last such snapshot is returned. A recipe without
        /// a MakeMaterial command returns layer 0, which is the layer the editor
        /// works on by default.
        /// </summary>
        public static uint[] Generate(TexRecipe recipe)
        {
            if (recipe == null) return new uint[0];

            int res = ClampResolution(recipe.Resolution);
            int count = res * res;

            Ctx ctx = new Ctx(res, count);

            if (recipe.Commands != null)
            {
                for (int i = 0; i < recipe.Commands.Count; i++)
                {
                    TexCommand cmd = recipe.Commands[i];
                    if (cmd == null) continue;

                    int layerIndex = LayerIndex(cmd.Layer);
                    uint[] layer = ctx.Layers[layerIndex];

                    // The filter works on a copy, exactly like PerformCommand():
                    // memcpy(Buffer, Layers[cmd.Layer].Data, ...) then the filter.
                    Array.Copy(layer, ctx.Work, count);

                    try
                    {
                        ApplyFilter(ctx, cmd, ctx.Work);
                    }
                    catch (Exception)
                    {
                        // A 2004 recipe can contain anything. A half-written work
                        // buffer must not be blended back, so drop the command.
                        continue;
                    }

                    if (cmd.Filter == TEXGEN_MakeMaterial)
                    {
                        // MakeMaterial is not a pixel filter. The original copies
                        // the current layer into a material slot and does not run
                        // the operator at all (Parser.cpp, ImportTextures), so the
                        // recipe's result is the layer as it stands right here.
                        Snapshot(ctx, layer);
                        continue;
                    }

                    Compose(layer, ctx.Work, cmd.Operator, cmd.OperatorMask, count);
                }
            }

            uint[] result = ctx.HasResult ? ctx.Result : ctx.Layers[0];
            return result;
        }

        /// <summary>
        /// The resolution a recipe should be baked at. Returns a power of two.
        ///
        /// Rounded up: a caller asking for 100 gets 128, so asking for more detail
        /// never silently produces less. 4096 is the format's own maximum (the
        /// .dmo stores 32 &lt;&lt; size with size in a byte, and the tool stops at
        /// 32 &lt;&lt; 7), and 1 is the smallest power of two that still makes sense.
        /// </summary>
        public static int ClampResolution(int requested)
        {
            if (requested <= 1) return 1;
            if (requested >= 4096) return 4096;

            int result = 1;
            while (result < requested) result <<= 1;
            return result;
        }

        // ==================================================================
        // Command plumbing
        // ==================================================================

        /// <summary>
        /// Per-texture scratch state, allocated once per Generate() call so that no
        /// filter allocates per pixel.
        /// </summary>
        private sealed class Ctx
        {
            public readonly int Res;
            public readonly int Count;
            public readonly uint[][] Layers;
            public readonly uint[] Work;
            public readonly uint[] Scratch;
            public readonly byte[] Bytes0;
            public readonly byte[] Bytes1;
            public readonly int[] Ints;
            public readonly uint[] Lut;
            public readonly int[] Dots;
            public uint[] Result;
            public bool HasResult;

            public Ctx(int res, int count)
            {
                Res = res;
                Count = count;

                Layers = new uint[LayerCount][];
                for (int i = 0; i < LayerCount; i++)
                {
                    uint[] layer = new uint[count];
                    for (int p = 0; p < count; p++) layer[p] = InitialPixel;
                    Layers[i] = layer;
                }

                Work = new uint[count];
                Scratch = new uint[count];
                Bytes0 = new byte[count];
                Bytes1 = new byte[count];
                Ints = new int[count];
                Lut = new uint[256];
                // Cells keeps up to 255 points, Crystalize up to 510.
                Dots = new int[512 * 2];
            }
        }

        private static void Snapshot(Ctx ctx, uint[] layer)
        {
            if (ctx.Result == null) ctx.Result = new uint[ctx.Count];
            Array.Copy(layer, ctx.Result, ctx.Count);
            ctx.HasResult = true;
        }

        /// <summary>Clamps a layer index read from a file into 0..7.</summary>
        private static int LayerIndex(int value)
        {
            if (value < 0) return 0;
            if (value >= LayerCount) return LayerCount - 1;
            return value;
        }

        /// <summary>True when the parameter block is at least <paramref name="bytes"/> long.</summary>
        private static bool HasData(byte[] data, int bytes)
        {
            return data != null && data.Length >= bytes;
        }

        private static void ApplyFilter(Ctx ctx, TexCommand cmd, uint[] layer)
        {
            byte[] d = cmd.Data;
            int res = ctx.Res;

            switch (cmd.Filter)
            {
                case TEXGEN_Envmap: Envmap(layer, res, d); break;
                case TEXGEN_Plasma: Plasma(layer, res, d); break;
                case TEXGEN_Map: Map(ctx, layer, d); break;
                case TEXGEN_Blur: Blur(ctx, layer, d); break;
                case TEXGEN_DirBlur: DirBlur(ctx, layer, d); break;
                case TEXGEN_Text: Text(layer, res, d); break;
                case TEXGEN_SubPlasma: SubPlasma(ctx, layer, d); break;
                case TEXGEN_FractalPlasma: FractalPlasma(ctx, layer, d); break;
                case TEXGEN_Colorize: Colorize(ctx, layer, d); break;
                case TEXGEN_Shade: Shade(ctx, layer, d); break;
                case TEXGEN_Brightness: Brightness(ctx, layer, d); break;
                case TEXGEN_Copy: Copy(ctx, layer, d); break;
                case TEXGEN_Cells: Cells(ctx, layer, d); break;
                case TEXGEN_Twirl: Twirl(ctx, layer, d); break;
                case TEXGEN_SineDist: SineDist(ctx, layer, d); break;
                case TEXGEN_Mix: Mix(ctx, layer, d); break;
                case TEXGEN_MixMap: MixMap(ctx, layer, d); break;
                case TEXGEN_Scale: Scale(layer, d); break;
                case TEXGEN_SineColor: SineColor(layer, d); break;
                case TEXGEN_Max: Max(ctx, layer, d); break;
                case TEXGEN_HSV: Hsv(layer, d); break;
                case TEXGEN_Emboss: Emboss(ctx, layer); break;
                case TEXGEN_Invert: Invert(layer); break;
                case TEXGEN_Glass: Glass(ctx, layer, d); break;
                case TEXGEN_Pixelize: Pixelize(layer, res, d); break;
                case TEXGEN_Offset: Offset(ctx, layer, d); break;
                case TEXGEN_Crystalize: Crystalize(ctx, layer, d); break;
                case TEXGEN_Rectangle: Rectangle(layer, res, d); break;
                case TEXGEN_Circle: Circle(ctx, layer, res, d); break;
                case TEXGEN_Contrast: Contrast(layer, d); break;
                case TEXGEN_MakeMaterial: break; // handled by Generate()
                case TEXGEN_Gradient: Gradient(layer, res, d); break;
                case TEXGEN_Rotozoom: Rotozoom(ctx, layer, d); break;
                case TEXGEN_ChamferRectangle: ChamferRectangle(ctx, layer, res, d); break;
                case TEXGEN_Dots: Dots(ctx, layer, res, d); break;
                case TEXGEN_Jpeg: Jpeg(layer, res, d); break;
                case TEXGEN_FractalPlasmaOld: FractalPlasmaOld(ctx, layer, d); break;
                default: break; // unknown filter: skip the command
            }
        }

        // ==================================================================
        // Channel helpers
        // ==================================================================

        /// <summary>
        /// Reads C++ channel 0..3 (r,g,b,a) out of an 0xAARRGGBB pixel.
        /// Out-of-range channels are wrapped rather than trusted: the file can say 9.
        /// </summary>
        private static int GetChannel(uint px, int channel)
        {
            switch (channel & 3)
            {
                case 0: return (int)((px >> 16) & 0xFF); // r
                case 1: return (int)((px >> 8) & 0xFF);  // g
                case 2: return (int)(px & 0xFF);         // b
                default: return (int)(px >> 24);         // a
            }
        }

        /// <summary>Writes C++ channel 0..3 (r,g,b,a); the value is truncated to a byte.</summary>
        private static uint SetChannel(uint px, int channel, int value)
        {
            uint v = (uint)(value & 0xFF);
            switch (channel & 3)
            {
                case 0: return (px & 0xFF00FFFFu) | (v << 16);
                case 1: return (px & 0xFFFF00FFu) | (v << 8);
                case 2: return (px & 0xFFFFFF00u) | v;
                default: return (px & 0x00FFFFFFu) | (v << 24);
            }
        }

        private static int ClampByte(int value)
        {
            if (value < 0) return 0;
            if (value > 255) return 255;
            return value;
        }

        /// <summary>
        /// SCALEX/SCALEY: a 0..255 parameter mapped onto the texture size. The C++
        /// uses ((x)*XRes)&gt;&gt;8 on an int, so the result can be negative for a
        /// negative input; that is intentional and relied on by Circle and
        /// ChamferRectangle, which offset by q-128.
        /// </summary>
        private static int Scaled(int value, int res)
        {
            return (value * res) >> 8;
        }

        /// <summary>WRAPX/WRAPY: wraps a (possibly negative) coordinate into the texture.</summary>
        private static int Wrap(int value, int res)
        {
            return value & (res - 1);
        }

        /// <summary>
        /// Reproduces `(int)f` as MSVC emits it for a value that may be out of range.
        /// C# leaves the out-of-range case unspecified, so it is pinned here: the
        /// only thing that matters for these filters is the low byte.
        /// </summary>
        private static int FloatToInt(float value)
        {
            if (float.IsNaN(value)) return 0;
            if (value >= 2147483000f) return int.MaxValue;
            if (value <= -2147483000f) return int.MinValue;
            return (int)value;
        }

        /// <summary>
        /// Reproduces `(unsigned int)(f * 256)` from the bilinear branches: a
        /// negative float becomes the two's complement of the truncated int, which
        /// is what MSVC's float-to-unsigned conversion produces and what makes
        /// Bilinear() wrap the way the original does.
        /// </summary>
        private static uint ToFixed8(float value)
        {
            return unchecked((uint)FloatToInt(value));
        }

        /// <summary>
        /// TexGen2.cpp: interpolate_colors. The MMX version has two quirks that are
        /// reproduced on purpose:
        ///   * pmullw keeps only the low 16 bits, so a large positive delta times a
        ///     large alpha wraps negative. black->white at alpha 255 therefore lands
        ///     on 254, not 255.
        ///   * the add after psrlw is paddb, i.e. it wraps at 256 instead of
        ///     saturating, which compensates for the wrap above when the delta is
        ///     negative (white->black at alpha 255 lands on 0).
        /// Byte order inside the dword is irrelevant here: the operation is per byte.
        /// </summary>
        private static uint InterpolateColors(uint from, uint to, int alpha)
        {
            int a = alpha & 0xFF;
            uint result = 0;

            for (int shift = 0; shift < 32; shift += 8)
            {
                int c1 = (int)((from >> shift) & 0xFF);
                int c2 = (int)((to >> shift) & 0xFF);

                short delta = unchecked((short)((c2 - c1) * a)); // pmullw
                int scaled = (int)((ushort)delta) >> 8;          // psrlw (logical)
                int value = (c1 + scaled) & 0xFF;                // paddb (wrapping)

                result |= (uint)value << shift;
            }

            return result;
        }

        /// <summary>
        /// TexGen2.cpp: TEXTURE::Bilinear. u and v are 24.8 fixed point. Note that
        /// WRAPX is applied to the *shifted* unsigned value, so a negative input
        /// coordinate wraps in the unsigned domain; ToFixed8 keeps that exact.
        /// </summary>
        private static uint Bilinear(uint[] layer, uint u, uint v, int res)
        {
            int ux = (int)(u >> 8);
            int vy = (int)(v >> 8);

            uint c00 = layer[Wrap(ux, res) + Wrap(vy, res) * res];
            uint c01 = layer[Wrap(ux, res) + Wrap(vy + 1, res) * res];
            uint c10 = layer[Wrap(ux + 1, res) + Wrap(vy, res) * res];
            uint c11 = layer[Wrap(ux + 1, res) + Wrap(vy + 1, res) * res];

            int fu = (int)(u & 0xFF);
            int fv = (int)(v & 0xFF);

            uint top = InterpolateColors(c00, c01, fv);
            uint bottom = InterpolateColors(c10, c11, fv);
            return InterpolateColors(top, bottom, fu);
        }

        // ==================================================================
        // PRNG
        // ==================================================================

        /// <summary>
        /// The MSVC CRT rand(): state = state * 214013 + 2531011, result is
        /// (state >> 16) &amp; 0x7FFF. System.Random would give a different (and
        /// version-dependent) sequence, which would make a demo look different on
        /// every machine - the whole point of porting this generator is that it
        /// does not.
        /// </summary>
        private sealed class MsRand
        {
            private uint _state;

            public MsRand(int seed)
            {
                _state = unchecked((uint)seed);
            }

            public int Next()
            {
                _state = unchecked(_state * 214013u + 2531011u);
                return (int)((_state >> 16) & 0x7FFF);
            }
        }

        private const int RandMax = 0x7FFF;

        // ==================================================================
        // 0 - Envmap
        // ==================================================================

        /// <summary>TexGen2.cpp: TEXTURE::Envmap. Radial white blob, black outside.</summary>
        private static void Envmap(uint[] layer, int res, byte[] d)
        {
            if (!HasData(d, 1)) return;

            float s = 255f / d[0]; // Size 0 gives +inf; the clamp below turns that into 0
            int half = res / 2;

            for (int y = 0; y < res; y++)
            {
                for (int x = 0; x < res; x++)
                {
                    float dx = x - half;
                    float dy = y - half;
                    float distance = (float)Math.Sqrt((double)(dx * dx + dy * dy));

                    // min(255, max(0, 255 - 255*s*dist/(res/2)))
                    float v = 255f - 255f * s * distance / half;
                    if (v < 0f) v = 0f;
                    if (v > 255f) v = 255f;

                    uint grey = (uint)(byte)v;
                    layer[y * res + x] = grey * 0x01010101u;
                }
            }
        }

        // ==================================================================
        // 1 - Plasma
        // ==================================================================

        /// <summary>TexGen2.cpp: TEXTURE::Plasma. Two sine waves, one per axis.</summary>
        private static void Plasma(uint[] layer, int res, byte[] d)
        {
            if (!HasData(d, 2)) return;

            int xsines = d[0];
            int ysines = d[1];

            for (int y = 0; y < res; y++)
            {
                float sy = (float)Math.Sin((double)(y / (float)res * ysines * 2.0f * 3.1415f));

                for (int x = 0; x < res; x++)
                {
                    float sx = (float)Math.Sin((double)(x / (float)res * xsines * 2.0f * 3.1415f));
                    byte v = (byte)(int)(127.0f + 63.5f * sx + 63.5f * sy);
                    layer[y * res + x] = v * 0x01010101u;
                }
            }
        }

        // ==================================================================
        // 2 - Map
        // ==================================================================

        /// <summary>
        /// TexGen2.cpp: TEXTURE::Map. Displaces the layer by the contents of two
        /// other layers.
        ///
        /// Judgement call: the C++ build defines __TEXGEN_BILINEAR__, so the
        /// bilinear branch is the one that actually shipped (and the non-bilinear
        /// branch of Rotozoom does not even compile, which is how you can tell it
        /// was never built). The bilinear branch is ported here.
        ///
        /// Judgement call: the aDDict2 loader (FileIO.cpp) overwrites
        /// YDataLayer/YDataChannel with the X values after reading the block, so
        /// the shipped tool only ever displaced both axes by the same layer and
        /// channel. This method honours the stored Y fields, as TexGen2.cpp does;
        /// a loader that wants the original's post-load behaviour has to apply the
        /// same fix-up before building the TexCommand.
        /// </summary>
        private static void Map(Ctx ctx, uint[] layer, byte[] d)
        {
            if (!HasData(d, 6)) return;

            int xLayer = LayerIndex(d[0]);
            int xChannel = d[1];
            int xAmount = d[2];
            int yLayer = LayerIndex(d[3]);
            int yChannel = d[4];
            int yAmount = d[5];

            uint[] xData = ctx.Layers[xLayer];
            uint[] yData = ctx.Layers[yLayer];
            uint[] buffer = ctx.Scratch;
            int res = ctx.Res;

            for (int y = 0; y < res; y++)
            {
                for (int x = 0; x < res; x++)
                {
                    int ofs = x + y * res;
                    int sx = GetChannel(xData[ofs], xChannel);
                    int sy = GetChannel(yData[ofs], yChannel);

                    uint ox = (uint)x + (uint)FloatToInt((float)((sx * xAmount * res) / 65536.0));
                    uint oy = (uint)y + (uint)FloatToInt((float)((sy * yAmount * res) / 65536.0));

                    buffer[ofs] = Bilinear(layer, ox * 256u, oy * 256u, res);
                }
            }

            Array.Copy(buffer, layer, ctx.Count);
        }

        // ==================================================================
        // 3 - Blur
        // ==================================================================

        /// <summary>
        /// TexGen2.cpp: TEXTURE::Blur. Four-neighbour average at SampleWidth
        /// distance, repeated Iterations times.
        /// </summary>
        private static void Blur(Ctx ctx, uint[] layer, byte[] d)
        {
            if (!HasData(d, 2)) return;

            int iterations = d[0];
            int s = Scaled(d[1], ctx.Res);
            int res = ctx.Res;
            uint[] buffer = ctx.Scratch;

            for (int z = 0; z < iterations; z++)
            {
                for (int j = 0; j < res; j++)
                {
                    for (int i = 0; i < res; i++)
                    {
                        uint left = layer[Wrap(i - s, res) + Wrap(j, res) * res];
                        uint right = layer[Wrap(i + s, res) + Wrap(j, res) * res];
                        uint up = layer[Wrap(i, res) + Wrap(j - s, res) * res];
                        uint down = layer[Wrap(i, res) + Wrap(j + s, res) * res];

                        buffer[i + j * res] = Average4(left, right, up, down);
                    }
                }

                Array.Copy(buffer, layer, ctx.Count);
            }
        }

        /// <summary>
        /// paddw / psrlw 2 / packuswb on four pixels: per channel (a+b+c+d)>>2.
        /// The 16-bit sums cannot overflow, so no wrap handling is needed.
        /// </summary>
        private static uint Average4(uint a, uint b, uint c, uint d)
        {
            uint result = 0;

            for (int shift = 0; shift < 32; shift += 8)
            {
                int sum = (int)((a >> shift) & 0xFF)
                        + (int)((b >> shift) & 0xFF)
                        + (int)((c >> shift) & 0xFF)
                        + (int)((d >> shift) & 0xFF);

                result |= (uint)(sum >> 2) << shift;
            }

            return result;
        }

        // ==================================================================
        // 4 - DirBlur
        // ==================================================================

        /// <summary>
        /// TexGen2.cpp: TEXTURE::DirBlur. Smears along the direction stored in
        /// another layer's channel.
        /// </summary>
        private static void DirBlur(Ctx ctx, uint[] layer, byte[] d)
        {
            if (!HasData(d, 3)) return;

            int dataLayer = LayerIndex(d[0]);
            int dataChannel = d[1];
            int samples = d[2];

            // The C++ guards the pixel loop with `if (numsamples)` but then copies
            // the (uninitialised) buffer over the layer regardless. Leaving the
            // layer alone is the only sane reading of that.
            if (samples == 0) return;

            uint[] dirLayer = ctx.Layers[dataLayer];
            uint[] buffer = ctx.Scratch;
            int res = ctx.Res;

            for (int x = 0; x < res; x++)
            {
                for (int y = 0; y < res; y++)
                {
                    float dir = GetChannel(dirLayer[x + res * y], dataChannel) / 256f * 2f * 3.14f;

                    int b1 = (int)(Math.Cos(dir) * res);
                    int b2 = (int)(Math.Sin(dir) * res);

                    int countR = 0, countG = 0, countB = 0, countA = 0;

                    for (int zc = 0; zc < samples; zc++)
                    {
                        int p1 = Wrap(x + ((b1 * zc) >> 8), res);
                        int p2 = Wrap(y + ((b2 * zc) >> 8), res);
                        uint px = layer[p1 + res * p2];

                        countR += (int)((px >> 16) & 0xFF);
                        countG += (int)((px >> 8) & 0xFF);
                        countB += (int)(px & 0xFF);
                        countA += (int)(px >> 24);
                    }

                    buffer[x + res * y] = Pack(countR / samples, countG / samples,
                                               countB / samples, countA / samples);
                }
            }

            Array.Copy(buffer, layer, ctx.Count);
        }

        private static uint Pack(int r, int g, int b, int a)
        {
            return ((uint)(a & 0xFF) << 24) | ((uint)(r & 0xFF) << 16)
                 | ((uint)(g & 0xFF) << 8) | (uint)(b & 0xFF);
        }

        // ==================================================================
        // 5 - Text
        // ==================================================================

        /// <summary>
        /// TexGen2.cpp: TEXTURE::Text. The original rasterises with GDI
        /// (CreateFont + TextOut) and reads the result back with GetDIBits; this
        /// port does the same job with System.Drawing, which is the closest
        /// equivalent available without dragging GDI interop into a filter.
        ///
        /// Approximation: GDI+ antialiasing and metrics are not identical to GDI's
        /// ANTIALIASED_QUALITY, and SetTextCharacterExtra (the Spacing field) has
        /// no GDI+ equivalent, so the letter spacing is ignored.
        ///
        /// The parameter block is the 7-byte TEXTTEMPLATE minus its pointer; the
        /// text itself follows it in the file, so it is read from Data[7..] up to
        /// the first NUL when the loader passes it through. Without the string the
        /// command is skipped.
        /// </summary>
        private static void Text(uint[] layer, int res, byte[] d)
        {
            if (!HasData(d, 7)) return;

            int size = d[0];
            int offsetX = d[1];
            int offsetY = d[2];
            bool bold = d[3] != 0;
            bool italic = d[4] != 0;
            int fontIndex = d[5];

            if (fontIndex < 0 || fontIndex >= TextFonts.Length) fontIndex = 0;

            int end = 7;
            while (end < d.Length && d[end] != 0) end++;
            if (end <= 7) return;

            // The file is ANSI, the same bytes TextOut() interpreted.
            string text = Encoding.Default.GetString(d, 7, end - 7);
            if (text.Length == 0) return;

            int height = (size * res) >> 8;
            if (height <= 0) height = 1; // CreateFont(0) picks a default; 1 is close enough

            try
            {
                using (Bitmap bitmap = new Bitmap(res, res, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(bitmap))
                    {
                        g.Clear(Color.Black);
                        g.PageUnit = GraphicsUnit.Pixel;
                        g.TextRenderingHint = TextRenderingHint.AntiAlias;

                        FontFamily family;
                        try
                        {
                            family = new FontFamily(TextFonts[fontIndex]);
                        }
                        catch (ArgumentException)
                        {
                            family = FontFamily.GenericSansSerif;
                        }

                        FontStyle style = FontStyle.Regular;
                        if (bold) style |= FontStyle.Bold;
                        if (italic) style |= FontStyle.Italic;

                        using (Font font = new Font(family, height, style, GraphicsUnit.Pixel))
                        using (Brush brush = new SolidBrush(Color.White))
                        {
                            g.DrawString(text, font, brush, 0f, 0f, StringFormat.GenericTypographic);
                        }
                    }

                    BitmapData bits = bitmap.LockBits(new Rectangle(0, 0, res, res),
                        ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                    try
                    {
                        int stride = bits.Stride;
                        byte[] pixels = new byte[stride * res];
                        Marshal.Copy(bits.Scan0, pixels, 0, pixels.Length);

                        int shiftX = Scaled(offsetX, res);
                        int shiftY = Scaled(offsetY, res);

                        // The C++ DIB is bottom-up and is flipped while being copied,
                        // so the result is a plain top-down blit at the given offset.
                        for (int y = 0; y < res; y++)
                        {
                            int row = y * stride;
                            for (int x = 0; x < res; x++)
                            {
                                // 32bppArgb is B,G,R,A in memory; the text is white,
                                // so any of the three is the same value.
                                byte v = pixels[row + x * 4 + 2];
                                int dst = Wrap(x + shiftX, res) + Wrap(y + shiftY, res) * res;
                                layer[dst] = v * 0x01010101u;
                            }
                        }
                    }
                    finally
                    {
                        bitmap.UnlockBits(bits);
                    }
                }
            }
            catch (Exception)
            {
                // No GDI+, no font, no display: a text command is decoration, not
                // a reason to fail the whole texture.
            }
        }

        // ==================================================================
        // 6 - SubPlasma
        // ==================================================================

        /// <summary>
        /// TexGen2.cpp: TEXTURE::SubPlasma. Note that the shipped code calls
        /// generate_subplasmaold (the blockier variant), not generate_subplasma;
        /// the newer call is commented out in the source.
        /// </summary>
        private static void SubPlasma(Ctx ctx, uint[] layer, byte[] d)
        {
            if (!HasData(d, 2)) return;

            int np = OctaveSize(d[0]);
            int seed = d[1];

            GenerateSubPlasmaOld(ctx.Bytes0, np, seed, ctx.Res, ctx.Res, 255);

            for (int i = 0; i < ctx.Count; i++)
            {
                layer[i] = ctx.Bytes0[i] * 0x01010101u;
            }
        }

        /// <summary>
        /// `1 &lt;&lt; shift`, but a byte from a 2004 file can be 200. Anything that
        /// large is bigger than the texture, which the generator treats as "clear
        /// the buffer and give up", so int.MaxValue is a safe stand-in for it.
        /// </summary>
        private static int OctaveSize(int shift)
        {
            if (shift <= 0) return 1;
            if (shift >= 31) return int.MaxValue;
            return 1 << shift;
        }

        /// <summary>
        /// TexGen2.cpp: generate_subplasma. Random control points plus a
        /// Catmull-Rom pass in each direction. Only used by FractalPlasma.
        /// </summary>
        private static void GenerateSubPlasma(byte[] buffer, int np, int seed, int rx, int ry)
        {
            if (np > rx) np = rx;
            if (np <= 0) return;

            int size = rx / np;
            if (size <= 0) return;

            // The C++ leaves the parts of the buffer it does not write
            // uninitialised. Clearing makes a malformed octave count deterministic.
            Array.Clear(buffer, 0, rx * ry);

            MsRand rnd = new MsRand(seed);

            for (int y = 0; y < np; y++)
            {
                for (int x = 0; x < np; x++)
                {
                    buffer[x * size + y * size * rx] = (byte)rnd.Next();
                }
            }

            if (size <= 1) return;

            for (int y = 0; y < np; y++)
            {
                int zy = y * size * rx;

                for (int x = 0; x < rx; x++)
                {
                    int p = x & ~(size - 1);
                    buffer[x + zy] = CatmullRom(
                        buffer[((p - size) & (rx - 1)) + zy],
                        buffer[(p & (rx - 1)) + zy],
                        buffer[((p + size) & (rx - 1)) + zy],
                        buffer[((p + size * 2) & (rx - 1)) + zy],
                        x % size, size);
                }
            }

            for (int y = 0; y < ry; y++)
            {
                int p = y & ~(size - 1);

                for (int x = 0; x < rx; x++)
                {
                    buffer[x + y * rx] = CatmullRom(
                        buffer[x + ((p - size) & (ry - 1)) * rx],
                        buffer[x + (p & (ry - 1)) * rx],
                        buffer[x + ((p + size) & (ry - 1)) * rx],
                        buffer[x + ((p + size * 2) & (ry - 1)) * rx],
                        y % size, size);
                }
            }
        }

        /// <summary>
        /// TexGen2.cpp: generate_subplasmaold. Same shape, but the control points
        /// are masked with `max` so the octaves can be added together.
        /// </summary>
        private static void GenerateSubPlasmaOld(byte[] buffer, int np, int seed, int rx, int ry, int max)
        {
            if (np > rx)
            {
                Array.Clear(buffer, 0, rx * ry);
                return;
            }

            if (np <= 0) return;

            int size = rx / np;
            if (size <= 0) return;

            Array.Clear(buffer, 0, rx * ry);

            MsRand rnd = new MsRand(seed);

            for (int y = 0; y < np; y++)
            {
                for (int x = 0; x < np; x++)
                {
                    buffer[x * size + y * size * rx] = (byte)(rnd.Next() & max);
                }
            }

            if (size == 1) return;

            for (int y = 0; y < np; y++)
            {
                int zy = y * size * rx;

                for (int x = 0; x < rx; x++)
                {
                    int p = x & ~(size - 1);
                    buffer[x + zy] = CatmullRom(
                        buffer[((p - size) & (rx - 1)) + zy],
                        buffer[(p & (rx - 1)) + zy],
                        buffer[((p + size) & (rx - 1)) + zy],
                        buffer[((p + size * 2) & (rx - 1)) + zy],
                        x % size, size);
                }
            }

            for (int y = 0; y < ry; y++)
            {
                int p = y & ~(size - 1);

                for (int x = 0; x < rx; x++)
                {
                    buffer[x + y * rx] = CatmullRom(
                        buffer[x + ((p - size) & (ry - 1)) * rx],
                        buffer[x + (p & (ry - 1)) * rx],
                        buffer[x + ((p + size) & (ry - 1)) * rx],
                        buffer[x + ((p + size * 2) & (ry - 1)) * rx],
                        y % size, size);
                }
            }
        }

        /// <summary>
        /// TexGen2.cpp: catmullrom_interpolate. The MMX tail is
        /// `movd mm0,t / packuswb mm0,mm0 / movd t,mm0`, i.e. PACKUSWB saturates
        /// the low word of t into an unsigned byte, so the function returns
        /// clamp(t, 0, 255). The explicit clamp below it in the C++ is commented
        /// out precisely because the asm already does it - so this must clamp,
        /// not truncate: truncating produces wrap-around speckles in the plasma.
        /// </summary>
        private static byte CatmullRom(byte v0, byte v1, byte v2, byte v3, int x, int distance)
        {
            float xx = (float)x / (float)distance;
            float p = (v3 - v2) - (v0 - v1);
            float q = (v0 - v1) - p;
            float r = v2 - v0;

            int t = (int)(p * xx * xx * xx + q * xx * xx + r * xx + v1);

            if (t < 0) return 0;
            if (t > 255) return 255;
            return (byte)t;
        }

        // ==================================================================
        // 7 - FractalPlasma
        // ==================================================================

        /// <summary>
        /// TexGen2.cpp: TEXTURE::FractalPlasma.
        ///
        /// Judgement call: the aDDict2 loader rewrites filter 7 to
        /// TEXGEN_FractalPlasmaOld (36) when it reads a .dmo (FileIO.cpp,
        /// `cmd->Filter=TEXGEN_FractalPlasmaOld`), so old files never ran this
        /// code path. This executor keeps the two filters distinct, as the source
        /// and the enum do; a loader that wants byte-identical rendering of a
        /// 2004 file should apply the same rewrite.
        /// </summary>
        private static void FractalPlasma(Ctx ctx, uint[] layer, byte[] d)
        {
            if (!HasData(d, 4)) return;

            int min = d[0];
            int max = d[1];
            int blend = d[2];
            int seed = d[3];

            byte[] octave = ctx.Bytes0;
            byte[] accum = ctx.Bytes1;
            Array.Clear(accum, 0, ctx.Count);

            for (int x = min; x <= max; x++)
            {
                GenerateSubPlasma(octave, OctaveSize(x), seed + x, ctx.Res, ctx.Res);

                int z = max - min + 1;
                float bf = blend - 0x80;
                float tf = (x - min) / (float)(z - 1) - 0.5f;

                // (255/z) is integer division in the C++ and stays one here.
                int masik = FloatToInt((255 / z) * ((bf * tf) / 127.0f + 0.5f) * 2);

                // The asm tail uses `mul cl`, i.e. only the low byte of masik.
                int factor = masik & 0xFF;

                for (int i = 0; i < ctx.Count; i++)
                {
                    int contribution = (octave[i] * factor) >> 8;
                    accum[i] = unchecked((byte)(accum[i] + contribution));
                }
            }

            for (int i = 0; i < ctx.Count; i++)
            {
                layer[i] = accum[i] * 0x01010101u;
            }
        }

        // ==================================================================
        // 8 - Colorize
        // ==================================================================

        /// <summary>
        /// TexGen2.cpp: TEXTURE::Colorize. Maps one channel through a two-colour LUT.
        /// </summary>
        private static void Colorize(Ctx ctx, uint[] layer, byte[] d)
        {
            if (!HasData(d, 9)) return;

            uint start = ReadColor(d, 0);
            uint end = ReadColor(d, 4);
            int channel = d[8];

            uint[] lut = ctx.Lut;
            for (int z = 0; z < 256; z++) lut[z] = InterpolateColors(start, end, z);

            for (int i = 0; i < ctx.Count; i++)
            {
                layer[i] = lut[GetChannel(layer[i], channel)];
            }
        }

        /// <summary>
        /// Reads a colour the way the C++ reads it: the file holds the RGBA struct
        /// byte for byte, so byte 0 is red and byte 3 is alpha. Packed into ARGB.
        /// </summary>
        private static uint ReadColor(byte[] d, int offset)
        {
            return ((uint)d[offset + 3] << 24)
                 | ((uint)d[offset] << 16)
                 | ((uint)d[offset + 1] << 8)
                 | d[offset + 2];
        }

        // ==================================================================
        // 9 - Shade
        // ==================================================================

        /// <summary>TexGen2.cpp: TEXTURE::Shade + apply_brightness.</summary>
        private static void Shade(Ctx ctx, uint[] layer, byte[] d)
        {
            if (!HasData(d, 2)) return;

            uint[] dataLayer = ctx.Layers[LayerIndex(d[0])];
            int channel = d[1];

            for (int i = 0; i < ctx.Count; i++)
            {
                ctx.Bytes0[i] = (byte)GetChannel(dataLayer[i], channel);
            }

            ApplyBrightness(layer, ctx.Bytes0, ctx.Count);
        }

        // ==================================================================
        // 10 - Brightness
        // ==================================================================

        /// <summary>TexGen2.cpp: TEXTURE::Brightness.</summary>
        private static void Brightness(Ctx ctx, uint[] layer, byte[] d)
        {
            if (!HasData(d, 1)) return;

            byte amount = d[0];
            for (int i = 0; i < ctx.Count; i++) ctx.Bytes0[i] = amount;

            ApplyBrightness(layer, ctx.Bytes0, ctx.Count);
        }

        /// <summary>
        /// TexGen2.cpp: apply_brightness. Below 128 the map multiplies, above 128
        /// it adds the headroom that is left, which is why 128 is a no-op.
        /// </summary>
        private static void ApplyBrightness(uint[] layer, byte[] map, int count)
        {
            for (int i = 0; i < count; i++)
            {
                int m = map[i];
                uint px = layer[i];
                uint result = 0;

                for (int shift = 0; shift < 32; shift += 8)
                {
                    int old = (int)((px >> shift) & 0xFF);
                    int value;

                    if (m > 128)
                    {
                        // (255-old)*(m-128)>>7, saturating add (paddusb)
                        value = old + (((255 - old) * (m - 128)) >> 7);
                    }
                    else
                    {
                        // old*m>>7, saturated by packuswb
                        value = (old * m) >> 7;
                    }

                    if (value > 255) value = 255;
                    result |= (uint)value << shift;
                }

                layer[i] = result;
            }
        }

        // ==================================================================
        // 11 - Copy
        // ==================================================================

        /// <summary>TexGen2.cpp: TEXTURE::Copy. Whole-dword copy of another layer.</summary>
        private static void Copy(Ctx ctx, uint[] layer, byte[] d)
        {
            if (!HasData(d, 1)) return;
            Array.Copy(ctx.Layers[LayerIndex(d[0])], layer, ctx.Count);
        }

        // ==================================================================
        // 12 - Cells
        // ==================================================================

        /// <summary>
        /// TexGen2.cpp: TEXTURE::Cells. Distance-to-nearest-point map, looked up
        /// through a precomputed table so the inner loop stays integer.
        /// </summary>
        private static void Cells(Ctx ctx, uint[] layer, byte[] d)
        {
            if (!HasData(d, 3)) return;

            int points = d[0];
            int power = d[1];
            int seed = d[2];
            int res = ctx.Res;

            float s = power * 5.0f * 2.0f / (float)res;
            int[] distTable = ctx.Ints;

            for (int y = 0; y < res; y++)
            {
                for (int x = 0; x < res; x++)
                {
                    float distance = (float)Math.Sqrt((double)(x * x + y * y));
                    distTable[x + y * res] = FloatToInt(distance * s);
                }
            }

            // SCALEX((unsigned char)rand()): the low byte of the result is used.
            int[] dots = ctx.Dots;
            MsRand rnd = new MsRand(seed);

            for (int p = 0; p < points; p++)
            {
                dots[p * 2] = Scaled((byte)rnd.Next(), res);
                dots[p * 2 + 1] = Scaled((byte)rnd.Next(), res);
            }

            int half = res / 2;

            for (int y = 0; y < res; y++)
            {
                for (int x = 0; x < res; x++)
                {
                    int r = 255;

                    for (int p = 0; p < points; p++)
                    {
                        int sx = half - Wrap(x + dots[p * 2], res);
                        int sy = half - Wrap(y + dots[p * 2 + 1], res);
                        int candidate = distTable[Math.Abs(sx) + Math.Abs(sy) * res];
                        if (candidate < r) r = candidate;
                    }

                    layer[x + y * res] = (uint)r * 0x01010101u;
                }
            }
        }

        // ==================================================================
        // 13 - Twirl
        // ==================================================================

        /// <summary>TexGen2.cpp: TEXTURE::Twirl (bilinear branch).</summary>
        private static void Twirl(Ctx ctx, uint[] layer, byte[] d)
        {
            if (!HasData(d, 1)) return;

            int res = ctx.Res;
            uint[] buffer = ctx.Scratch;
            float degrees = ((d[0] - 127) * 16.0f) * (3.14f / 180.0f);

            for (int y = 0; y < res; y++)
            {
                for (int x = 0; x < res; x++)
                {
                    int dx = x - (res / 2);
                    int dy = y - (res / 2);

                    float r = (float)Math.Sqrt((double)((float)dx * dx + dy * dy)) / (float)(res >> 1);
                    if (r > 1f) r = 1f;

                    float phi = (float)Math.Cos(r * 3.1415f / 2.0f);
                    phi *= phi;

                    float angle = degrees * phi;
                    float cosA = (float)Math.Cos(angle);
                    float sinA = (float)Math.Sin(angle);

                    float xn = dx * cosA + dy * sinA + (res >> 1);
                    float yn = -dx * sinA + dy * cosA + (res >> 1);

                    buffer[x + y * res] = Bilinear(layer, ToFixed8(xn * 256f), ToFixed8(yn * 256f), res);
                }
            }

            Array.Copy(buffer, layer, ctx.Count);
        }

        // ==================================================================
        // 14 - SineDist
        // ==================================================================

        /// <summary>TexGen2.cpp: TEXTURE::SineDist (bilinear branch).</summary>
        private static void SineDist(Ctx ctx, uint[] layer, byte[] d)
        {
            if (!HasData(d, 4)) return;

            int res = ctx.Res;
            uint[] buffer = ctx.Scratch;

            int xSines = d[0];
            int ySines = d[1];
            int xAmp = Scaled(d[2], res);
            int yAmp = Scaled(d[3], res);

            for (int y = 0; y < res; y++)
            {
                float xOffset = (float)(Math.Cos(y / (float)res * xSines * 2.0f * 3.14f) * xAmp);

                for (int x = 0; x < res; x++)
                {
                    float yOffset = (float)(Math.Cos(x / (float)res * ySines * 2.0f * 3.14f) * yAmp);

                    float xp = x + xOffset;
                    float yp = y + yOffset;

                    buffer[x + y * res] = Bilinear(layer, ToFixed8(xp * 256f), ToFixed8(yp * 256f), res);
                }
            }

            Array.Copy(buffer, layer, ctx.Count);
        }

        // ==================================================================
        // 15 - Mix
        // ==================================================================

        /// <summary>TexGen2.cpp: TEXTURE::Mix.</summary>
        private static void Mix(Ctx ctx, uint[] layer, byte[] d)
        {
            if (!HasData(d, 2)) return;

            uint[] other = ctx.Layers[LayerIndex(d[0])];
            int amount = d[1];

            for (int i = 0; i < ctx.Count; i++)
            {
                layer[i] = InterpolateColors(layer[i], other[i], amount);
            }
        }

        // ==================================================================
        // 16 - MixMap
        // ==================================================================

        /// <summary>TexGen2.cpp: TEXTURE::MixMap. The mix amount comes from a channel.</summary>
        private static void MixMap(Ctx ctx, uint[] layer, byte[] d)
        {
            if (!HasData(d, 3)) return;

            uint[] dataLayer = ctx.Layers[LayerIndex(d[0])];
            uint[] mixLayer = ctx.Layers[LayerIndex(d[1])];
            int mixChannel = d[2];

            for (int i = 0; i < ctx.Count; i++)
            {
                int amount = GetChannel(mixLayer[i], mixChannel);
                layer[i] = InterpolateColors(layer[i], dataLayer[i], amount);
            }
        }

        // ==================================================================
        // 17 - Scale
        // ==================================================================

        /// <summary>TexGen2.cpp: TEXTURE::Scale. Clamps one channel into a range.</summary>
        private static void Scale(uint[] layer, byte[] d)
        {
            if (!HasData(d, 3)) return;

            int channel = d[0];
            int min = d[1];
            int max = d[2];

            if (max <= min) return;

            for (int i = 0; i < layer.Length; i++)
            {
                int v = GetChannel(layer[i], channel);
                if (v > max) v = max;
                else if (v < min) v = min;
                layer[i] = SetChannel(layer[i], channel, v);
            }
        }

        // ==================================================================
        // 18 - SineColor
        // ==================================================================

        /// <summary>TexGen2.cpp: TEXTURE::SineColor. Sine of a channel, taken absolute.</summary>
        private static void SineColor(uint[] layer, byte[] d)
        {
            if (!HasData(d, 2)) return;

            int channel = d[0];
            int numSines = d[1];

            for (int i = 0; i < layer.Length; i++)
            {
                int c = GetChannel(layer[i], channel);
                float angle = c * (3.14f / 180.0f) * (255.0f / 360.0f) * numSines;
                int n = (int)(Math.Sin(angle) * 255.0f);
                layer[i] = SetChannel(layer[i], channel, Math.Abs(n));
            }
        }

        // ==================================================================
        // 19 - Max
        // ==================================================================

        /// <summary>TexGen2.cpp: TEXTURE::Max. Per-channel maximum of two layers.</summary>
        private static void Max(Ctx ctx, uint[] layer, byte[] d)
        {
            if (!HasData(d, 1)) return;

            uint[] other = ctx.Layers[LayerIndex(d[0])];

            for (int i = 0; i < ctx.Count; i++)
            {
                uint a = layer[i];
                uint b = other[i];
                uint result = 0;

                for (int shift = 0; shift < 32; shift += 8)
                {
                    uint ca = (a >> shift) & 0xFF;
                    uint cb = (b >> shift) & 0xFF;
                    result |= (ca > cb ? ca : cb) << shift;
                }

                layer[i] = result;
            }
        }

        // ==================================================================
        // 20 - HSV
        // ==================================================================

        /// <summary>
        /// TexGen2.cpp: TEXTURE::HSV. Note that the original writes the result
        /// through RGBA out={r,g,b,0}, so alpha is cleared - that is not a bug
        /// introduced here.
        /// </summary>
        private static void Hsv(uint[] layer, byte[] d)
        {
            if (!HasData(d, 2)) return;

            float hueShift = d[0];
            int saturationScale = d[1] ^ 0xFF;

            for (int i = 0; i < layer.Length; i++)
            {
                uint px = layer[i];
                float r = GetChannel(px, 0);
                float g = GetChannel(px, 1);
                float b = GetChannel(px, 2);

                float h, s, v;
                RgbToHsv(r, g, b, out h, out s, out v);

                h = (float)(h + hueShift * 360.0 / 255.0);
                s = (float)(s * saturationScale / 255.0);

                layer[i] = HsvToRgb(h, s, v);
            }
        }

        /// <summary>TexGen2.cpp: rgb_to_hsv. Inputs and outputs are 0..255.</summary>
        private static void RgbToHsv(float r, float g, float b, out float h, out float s, out float v)
        {
            float max;
            float min;

            if (r > g) max = r; else max = g;
            if (b > max) max = b;
            if (r < g) min = r; else min = g;
            if (b < min) min = b;

            v = max;
            s = 0f;
            if (max != 0f) s = (max - min) / max;

            if (s == 0f)
            {
                h = -1f;
                return;
            }

            float delta = max - min;
            float maxr = max - r;
            float maxg = max - g;
            float maxb = max - b;

            if (r == max) h = (maxb - maxg) / delta;
            else if (g == max) h = 2.0f + ((maxr - maxb) / delta);
            else if (b == max) h = 4.0f + ((maxg - maxr) / delta);
            else h = 0f;

            h *= 60.0f;
            while (h < 0.0f) h += 360.0f;
            while (h >= 360.0f) h -= 360.0f;
        }

        /// <summary>TexGen2.cpp: hsv_to_rgb. h in degrees, s and v in 0..255.</summary>
        private static uint HsvToRgb(float h, float s, float v)
        {
            float r = 0f, g = 0f, b = 0f;

            while (h < 0.0f) h += 360.0f;
            while (h >= 360.0f) h -= 360.0f;

            if (s == 0f)
            {
                r = v;
                g = v;
                b = v;
            }
            else
            {
                h /= 60.0f;
                float i = (float)(int)h;
                float f = h - i;

                float p = v * (1.0f - s);
                float q = v * (1.0f - (s * f));
                float t = v * (1.0f - (s * (1.0f - f)));

                switch ((int)i)
                {
                    case 0: r = v; g = t; b = p; break;
                    case 1: r = q; g = v; b = p; break;
                    case 2: r = p; g = v; b = t; break;
                    case 3: r = p; g = q; b = v; break;
                    case 4: r = t; g = p; b = v; break;
                    case 5: r = v; g = p; b = q; break;
                    default: r = v; g = v; b = v; break; // unreachable after normalising
                }
            }

            int ir = ClampByte((int)r);
            int ig = ClampByte((int)g);
            int ib = ClampByte((int)b);

            // Alpha is zero, as in the C++ `RGBA out={ir,ig,ib,0}`.
            return ((uint)ir << 16) | ((uint)ig << 8) | (uint)ib;
        }

        // ==================================================================
        // 21 - Emboss
        // ==================================================================

        /// <summary>
        /// TexGen2.cpp: TEXTURE::Emboss. The active code is the plain C loop, not
        /// the commented-out MMX block above it (which adds md1 twice); the loop
        /// adds each of the three right-hand pixels once.
        /// </summary>
        private static void Emboss(Ctx ctx, uint[] layer)
        {
            int res = ctx.Res;
            uint[] buffer = ctx.Scratch;

            for (int x = 0; x < res; x++)
            {
                for (int y = 0; y < res; y++)
                {
                    uint mu0 = layer[Wrap(x - 1, res) + Wrap(y - 1, res) * res];
                    uint mu1 = layer[Wrap(x - 1, res) + Wrap(y, res) * res];
                    uint mu2 = layer[Wrap(x - 1, res) + Wrap(y + 1, res) * res];
                    uint md0 = layer[Wrap(x + 1, res) + Wrap(y - 1, res) * res];
                    uint md1 = layer[Wrap(x + 1, res) + Wrap(y, res) * res];
                    uint md2 = layer[Wrap(x + 1, res) + Wrap(y + 1, res) * res];

                    uint px = 0;

                    for (int c = 0; c < 4; c++)
                    {
                        int q = 0;
                        q -= Scaled(GetChannel(mu0, c), res);
                        q -= Scaled(GetChannel(mu1, c), res);
                        q -= Scaled(GetChannel(mu2, c), res);
                        q += Scaled(GetChannel(md0, c), res);
                        q += Scaled(GetChannel(md1, c), res);
                        q += Scaled(GetChannel(md2, c), res);

                        if (q < -127) q = -127;
                        if (q > 127) q = 127;

                        px = SetChannel(px, c, q + 0x80);
                    }

                    buffer[x + y * res] = px;
                }
            }

            Array.Copy(buffer, layer, ctx.Count);
        }

        // ==================================================================
        // 22 - Invert
        // ==================================================================

        /// <summary>TexGen2.cpp: TEXTURE::Invert. XOR with 0xFFFFFFFF, all channels.</summary>
        private static void Invert(uint[] layer)
        {
            for (int i = 0; i < layer.Length; i++) layer[i] = ~layer[i];
        }

        // ==================================================================
        // 23 - Glass
        // ==================================================================

        /// <summary>TexGen2.cpp: TEXTURE::Glass (non-bilinear branch).</summary>
        private static void Glass(Ctx ctx, uint[] layer, byte[] d)
        {
            if (!HasData(d, 3)) return;

            int dataLayer = LayerIndex(d[0]);
            int dataChannel = d[1];
            int amount = d[2];

            // Integer arithmetic here is deliberate: the non-bilinear branch is
            // plain int maths in the C++ (and unlike Rotozoom's, it compiles).
            int a = ((amount - 127) * ctx.Res / 256) * ctx.Res / 256;

            uint[] data = ctx.Layers[dataLayer];
            uint[] buffer = ctx.Scratch;
            int res = ctx.Res;

            for (int x = 0; x < res; x++)
            {
                for (int y = 0; y < res; y++)
                {
                    int ofs = x + y * res;
                    int p = GetChannel(data[ofs], dataChannel);
                    int dx = p - GetChannel(data[Wrap(x + 1, res) + y * res], dataChannel);
                    int dy = p - GetChannel(data[x + Wrap(y + 1, res) * res], dataChannel);

                    int ox = (x + (dx * a) / 32) & (res - 1);
                    int oy = (y + (dy * a) / 32) & (res - 1);

                    buffer[ofs] = layer[ox + oy * res];
                }
            }

            Array.Copy(buffer, layer, ctx.Count);
        }

        // ==================================================================
        // 24 - Pixelize
        // ==================================================================

        /// <summary>
        /// TexGen2.cpp: TEXTURE::Pixelize. Each channel is averaged over a block and
        /// written back; a block clipped by the edge still divides by the full block
        /// size, which is what the C++ does.
        /// </summary>
        private static void Pixelize(uint[] layer, int res, byte[] d)
        {
            if (!HasData(d, 2)) return;

            int xSquares = d[0];
            int ySquares = d[1];

            // The C++ divides by these, so zero is a crash, not a texture.
            if (xSquares == 0 || ySquares == 0) return;

            int sxSize = Math.Max(1, res / xSquares);
            int sySize = Math.Max(1, res / ySquares);

            for (int x = 0; x < xSquares + 1; x++)
            {
                for (int y = 0; y < ySquares + 1; y++)
                {
                    for (int c = 0; c < 4; c++)
                    {
                        uint t = 0;
                        int zx;

                        for (zx = 0; zx < sxSize && (x * sxSize + zx) < res; zx++)
                        {
                            for (int zy = 0; zy < sySize && (y * sySize + zy) < res; zy++)
                            {
                                t += (uint)GetChannel(layer[(x * sxSize + zx) + (y * sySize + zy) * res], c);
                            }
                        }

                        t /= (uint)(sxSize * sySize);

                        for (zx = 0; zx < sxSize && (x * sxSize + zx) < res; zx++)
                        {
                            for (int zy = 0; zy < sySize && (y * sySize + zy) < res; zy++)
                            {
                                int ofs = (x * sxSize + zx) + (y * sySize + zy) * res;
                                layer[ofs] = SetChannel(layer[ofs], c, (int)t);
                            }
                        }
                    }
                }
            }
        }

        // ==================================================================
        // 25 - Offset
        // ==================================================================

        /// <summary>TexGen2.cpp: TEXTURE::Offset. Wrapping translate.</summary>
        private static void Offset(Ctx ctx, uint[] layer, byte[] d)
        {
            if (!HasData(d, 2)) return;

            int res = ctx.Res;
            uint[] buffer = ctx.Scratch;
            int shiftX = Scaled(d[0], res);
            int shiftY = Scaled(d[1], res);

            for (int x = 0; x < res; x++)
            {
                for (int y = 0; y < res; y++)
                {
                    buffer[x + y * res] = layer[Wrap(x + shiftX, res) + Wrap(y + shiftY, res) * res];
                }
            }

            Array.Copy(buffer, layer, ctx.Count);
        }

        // ==================================================================
        // 26 - Crystalize
        // ==================================================================

        /// <summary>
        /// TexGen2.cpp: TEXTURE::Crystalize. Every pixel takes the colour of the
        /// point sample nearest to it.
        /// </summary>
        private static void Crystalize(Ctx ctx, uint[] layer, byte[] d)
        {
            if (!HasData(d, 2)) return;

            int points = d[0];
            int seed = d[1];
            int z = points * 2;

            // With no points the C++ reads an uninitialised dots[] entry; there is
            // no meaningful result to reproduce, so the layer is left alone.
            if (z <= 0) return;

            int res = ctx.Res;
            int[] dots = ctx.Dots;
            MsRand rnd = new MsRand(seed);

            for (int p = 0; p < z; p++)
            {
                dots[p * 2] = Scaled((byte)rnd.Next(), res);
                dots[p * 2 + 1] = Scaled((byte)rnd.Next(), res);
            }

            uint[] buffer = ctx.Scratch;
            int half = res / 2;

            for (int y = 0; y < res; y++)
            {
                for (int x = 0; x < res; x++)
                {
                    int best = int.MaxValue;
                    int bestPoint = 0;

                    for (int p = 0; p < z; p++)
                    {
                        int sx = half - Wrap(x + dots[p * 2], res);
                        int sy = half - Wrap(y + dots[p * 2 + 1], res);

                        // The C++ uses fsqrt + fistp, and FISTP's default rounding
                        // mode is round-half-to-even, not truncation. The value is
                        // only compared, but a truncating port would flip which
                        // point wins on near-ties along the cell boundaries.
                        int r2 = (int)Math.Round(Math.Sqrt((double)(sx * sx + sy * sy)),
                                                 MidpointRounding.ToEven);

                        if (r2 < best)
                        {
                            best = r2;
                            bestPoint = p;
                        }
                    }

                    // The C++ computes `XRes - WRAPX(...)` unguarded, which can be
                    // XRes (one row/column past the end) when the wrap is 0. Wrapping
                    // it keeps the read inside the buffer.
                    int srcX = (res - Wrap(dots[bestPoint * 2] + half, res)) & (res - 1);
                    int srcY = (res - Wrap(dots[bestPoint * 2 + 1] + half, res)) & (res - 1);

                    buffer[x + y * res] = layer[srcX + srcY * res];
                }
            }

            Array.Copy(buffer, layer, ctx.Count);
        }

        // ==================================================================
        // 27 - Rectangle
        // ==================================================================

        /// <summary>TexGen2.cpp: TEXTURE::Rectangle. Fills a wrapping white rectangle.</summary>
        private static void Rectangle(uint[] layer, int res, byte[] d)
        {
            if (!HasData(d, 4)) return;

            int startX = Scaled(d[0], res);
            int startY = Scaled(d[1], res);
            int sizeX = Scaled(d[2], res);
            int sizeY = Scaled(d[3], res);

            for (int x = 0; x < sizeX; x++)
            {
                for (int y = 0; y < sizeY; y++)
                {
                    layer[Wrap(startX + x, res) + Wrap(startY + y, res) * res] = 0xFFFFFFFFu;
                }
            }
        }

        // ==================================================================
        // 28 - Circle
        // ==================================================================

        /// <summary>TexGen2.cpp: TEXTURE::Circle.</summary>
        private static void Circle(Ctx ctx, uint[] layer, int res, byte[] d)
        {
            if (!HasData(d, 4)) return;

            byte[] buffer = ctx.Bytes0;
            Array.Clear(buffer, 0, ctx.Count);

            Circ(buffer, d[2], d[3], res, res, d[0], d[1], 0xFF);

            for (int i = 0; i < ctx.Count; i++)
            {
                layer[i] = buffer[i] * 0x01010101u;
            }
        }

        /// <summary>
        /// TexGen2.cpp: circ. Antialiased ring between innerr and innerr+outerr,
        /// with a soft band one pixel wide on each edge.
        /// </summary>
        private static void Circ(byte[] buffer, int qx, int qy, int xRes, int yRes, int innerr, int outerr, int color)
        {
            int ir = Scaled(innerr, xRes);
            int or = Scaled(innerr + outerr, xRes);
            float aa = xRes / 256.0f;

            for (int x = 0; x < xRes; x++)
            {
                for (int y = 0; y < yRes; y++)
                {
                    int cx = Wrap(x + Scaled(qx - 128, xRes), xRes);
                    int cy = Wrap(y + Scaled(qy - 128, yRes), yRes);

                    float dx = x - (xRes / 2.0f);
                    float dy = y - (xRes / 2.0f); // the C++ uses XRes for both axes
                    float distance = (float)Math.Sqrt((double)(dx * dx + dy * dy));

                    int c;
                    if (distance > ir && distance < or) c = 0xFF;
                    else if (Math.Abs(distance - ir) < aa) c = (int)(255 - Math.Abs(distance - ir) * 255 / aa);
                    else if (Math.Abs(distance - or) < aa) c = (int)(255 - Math.Abs(distance - or) * 255 / aa);
                    else c = 0;

                    buffer[cx + cy * yRes] = (byte)((c * color) >> 8);
                }
            }
        }

        // ==================================================================
        // 29 - Contrast
        // ==================================================================

        /// <summary>
        /// TexGen2.cpp: TEXTURE::Contrast. 128 is a no-op, 0 flattens to grey and
        /// 255 doubles the distance from mid grey.
        /// </summary>
        private static void Contrast(uint[] layer, byte[] d)
        {
            if (!HasData(d, 1)) return;

            int amount = d[0];

            for (int i = 0; i < layer.Length; i++)
            {
                uint px = layer[i];

                for (int c = 0; c < 4; c++)
                {
                    int v = GetChannel(px, c);
                    int z = ((v - 0x80) * amount) / 128 + 0x80;
                    px = SetChannel(px, c, ClampByte(z));
                }

                layer[i] = px;
            }
        }

        // ==================================================================
        // 30 - MakeMaterial
        // ==================================================================

        // Not a pixel filter; Generate() snapshots the layer when it sees one.

        // ==================================================================
        // 31 - Gradient
        // ==================================================================

        /// <summary>TexGen2.cpp: TEXTURE::Gradient. Bilinear blend of four corner colours.</summary>
        private static void Gradient(uint[] layer, int res, byte[] d)
        {
            if (!HasData(d, 16)) return;

            uint c00 = ReadColor(d, 0);
            uint c10 = ReadColor(d, 4);
            uint c01 = ReadColor(d, 8);
            uint c11 = ReadColor(d, 12);

            for (int x = 0; x < res; x++)
            {
                int u = (x << 8) / res;
                uint top = InterpolateColors(c00, c10, u);
                uint bottom = InterpolateColors(c01, c11, u);

                for (int y = 0; y < res; y++)
                {
                    int v = (y << 8) / res;
                    layer[x + y * res] = InterpolateColors(top, bottom, v);
                }
            }
        }

        // ==================================================================
        // 32 - Rotozoom
        // ==================================================================

        /// <summary>
        /// TexGen2.cpp: TEXTURE::Rotozoom (bilinear branch). ZoomIn divides the zoom
        /// factors by 256, so with ZoomIn clear the zoom factors are used as-is and
        /// the image is magnified by up to 255x, which is what the original did.
        /// </summary>
        private static void Rotozoom(Ctx ctx, uint[] layer, byte[] d)
        {
            if (!HasData(d, 6)) return;

            int res = ctx.Res;
            uint[] buffer = ctx.Scratch;

            float zoomX = d[2];
            float zoomY = d[3];
            if (d[4] != 0)
            {
                zoomX = d[2] / 256.0f;
                zoomY = d[3] / 256.0f;
            }

            float angle = d[5] * (2 * 3.1415f) / 256.0f;
            float cosA = (float)Math.Cos(angle);
            float sinA = (float)Math.Sin(angle);

            int centerX = Scaled(d[0], res);
            int centerY = Scaled(d[1], res);

            for (int y = 0; y < res; y++)
            {
                for (int x = 0; x < res; x++)
                {
                    int dx = x - centerX;
                    int dy = y - centerY;

                    float xn = (dx * cosA + dy * sinA + (res >> 1)) * zoomX;
                    float yn = (-dx * sinA + dy * cosA + (res >> 1)) * zoomY;

                    buffer[x + y * res] = Bilinear(layer, ToFixed8(xn * 256f), ToFixed8(yn * 256f), res);
                }
            }

            Array.Copy(buffer, layer, ctx.Count);
        }

        // ==================================================================
        // 33 - ChamferRectangle
        // ==================================================================

        /// <summary>TexGen2.cpp: TEXTURE::ChamferRectangle.</summary>
        private static void ChamferRectangle(Ctx ctx, uint[] layer, int res, byte[] d)
        {
            if (!HasData(d, 5)) return;

            byte[] buffer = ctx.Bytes0;
            Array.Clear(buffer, 0, ctx.Count);

            ChamfRect(buffer, d[0], d[1], d[2], d[3], res, res, d[4], 0xFF);

            for (int i = 0; i < ctx.Count; i++)
            {
                layer[i] = buffer[i] * 0x01010101u;
            }
        }

        /// <summary>TexGen2.cpp: chamfrect. Filled rectangle with optional corner cut-outs.</summary>
        private static void ChamfRect(byte[] buffer, int startX, int startY, int sizeX, int sizeY,
                                      int xRes, int yRes, int chamfer, int color)
        {
            int sx = Scaled(sizeX + 1, xRes);
            int sy = Scaled(sizeY + 1, yRes);

            for (int x = 0; x < sx; x++)
            {
                for (int y = 0; y < sy; y++)
                {
                    buffer[Wrap(Scaled(startX, xRes) + x, xRes) + Wrap(Scaled(startY, yRes) + y, yRes) * xRes] = (byte)color;
                }
            }

            if (chamfer == 0) return;

            // The C++ declares these as `unsigned char MaxChamfer` / `Chamfer`, so
            // a half-width of 256 or more truncates to 0 and silently disables the
            // chamfer. Reproduced because a 512x512 texture with a full-size
            // rectangle really did render unchamfered in the original.
            int maxChamfer = ((sx > sy ? sy / 2 : sx / 2) & 0xFF);
            int cham = ((maxChamfer * chamfer) / 255) & 0xFF;

            float aa = xRes / 256.0f;

            for (int x = 0; x < cham; x++)
            {
                for (int y = 0; y < cham; y++)
                {
                    float dx = x;
                    float dy = y;
                    float distance = (float)Math.Sqrt((double)(dx * dx + dy * dy));

                    int c;
                    if (distance < cham) c = 0xFF;
                    else if (Math.Abs(distance - cham) < aa) c = (int)(255 - Math.Abs(distance - cham) * 255 / aa);
                    else c = 0;

                    c = (c * color) >> 8;

                    int left = Wrap(Scaled(startX, xRes) + (cham - x - 1), xRes);
                    int top = Wrap(Scaled(startY, yRes) + (cham - y - 1), yRes);
                    int right = Wrap(Scaled(startX, xRes) + x + sx - cham, xRes);
                    int bottom = Wrap(Scaled(startY, yRes) + y + sy - cham, yRes);

                    buffer[right + bottom * xRes] = (byte)c;
                    buffer[left + top * xRes] = (byte)c;
                    buffer[right + top * xRes] = (byte)c;
                    buffer[left + bottom * xRes] = (byte)c;
                }
            }
        }

        // ==================================================================
        // 34 - Dots
        // ==================================================================

        /// <summary>
        /// TexGen2.cpp: TEXTURE::Dots. Stamps Count circles or squares of random
        /// size and colour, keeping the brightest value per pixel.
        /// </summary>
        private static void Dots(Ctx ctx, uint[] layer, int res, byte[] d)
        {
            // DOTSTEMPLATE is 8 packed bytes: count(2), min, max, rounded, sizeMin,
            // sizeMax, seed.
            if (!HasData(d, 8)) return;

            int count = d[0] | (d[1] << 8);
            int min = d[2];
            int max = d[3];
            bool rounded = d[4] != 0;
            int sizeMin = d[5];
            int sizeMax = d[6];
            int seed = d[7];

            Array.Clear(layer, 0, ctx.Count); // the C++ memsets the working buffer

            MsRand rnd = new MsRand(seed);

            byte[] stamp = ctx.Bytes0;
            byte[] result = ctx.Bytes1;
            Array.Clear(result, 0, ctx.Count);

            for (int x = 0; x < count; x++)
            {
                Array.Clear(stamp, 0, ctx.Count);

                int radius = sizeMin + (rnd.Next() * (sizeMax - sizeMin) / RandMax);
                int color = min + (rnd.Next() * (max - min) / RandMax);
                int cx = rnd.Next();
                int cy = rnd.Next();

                if (rounded)
                {
                    Circ(stamp, Scaled(cx, res), Scaled(cy, res), res, res, 0, radius, color);
                }
                else
                {
                    ChamfRect(stamp, Scaled(cx, res), Scaled(cy, res), Scaled(radius, res), Scaled(radius, res),
                              res, res, 0, color);
                }

                for (int i = 0; i < ctx.Count; i++)
                {
                    if (result[i] < stamp[i]) result[i] = stamp[i];
                }
            }

            for (int i = 0; i < ctx.Count; i++)
            {
                layer[i] = result[i] * 0x01010101u;
            }
        }

        // ==================================================================
        // 35 - Jpeg
        // ==================================================================

        /// <summary>
        /// TexGen2.cpp: TEXTURE::Jpeg. The C++ renders the picture twice with
        /// OleLoadPicture, once on black (which gives the colour) and once on white
        /// (whose difference from the first pass gives the alpha), then averages a
        /// 3x3 supersampled buffer down.
        ///
        /// Approximation: System.Drawing decodes and scales instead of GDI's
        /// StretchBlt, and the supersampling pass is dropped - the resampling
        /// filter differs anyway, so a 3x supersample would not bring the two
        /// closer. A JPEG is opaque, so the alpha pass yields 255, which matches.
        ///
        /// The parameter block is the 4-byte size followed by the encoded image
        /// (the C++ reads the size from the stream and points into it). Without
        /// the bytes the command is skipped.
        /// </summary>
        private static void Jpeg(uint[] layer, int res, byte[] d)
        {
            if (!HasData(d, 4)) return;

            int size = BitConverter.ToInt32(d, 0);
            if (size <= 0 || 4 + size > d.Length) return;

            try
            {
                using (MemoryStream stream = new MemoryStream(d, 4, size))
                using (Image image = Image.FromStream(stream))
                {
                    uint[] onBlack = RenderImage(image, res, Color.Black);
                    uint[] onWhite = RenderImage(image, res, Color.White);
                    int count = res * res;

                    for (int i = 0; i < count; i++)
                    {
                        uint pb = onBlack[i];
                        uint pw = onWhite[i];

                        int dr = GetChannel(pw, 0) - GetChannel(pb, 0);
                        int dg = GetChannel(pw, 1) - GetChannel(pb, 1);
                        int db = GetChannel(pw, 2) - GetChannel(pb, 2);

                        int alpha = 255 - (dr + dg + db) / 3;

                        layer[i] = Pack(GetChannel(pb, 0), GetChannel(pb, 1), GetChannel(pb, 2), alpha);
                    }
                }
            }
            catch (Exception)
            {
                // Not a picture, or no GDI+: skip the command rather than fail.
            }
        }

        /// <summary>Draws an image stretched over a solid background and returns ARGB pixels.</summary>
        private static uint[] RenderImage(Image image, int res, Color background)
        {
            uint[] pixels = new uint[res * res];

            using (Bitmap bitmap = new Bitmap(res, res, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    g.Clear(background);
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(image, new Rectangle(0, 0, res, res));
                }

                BitmapData bits = bitmap.LockBits(new Rectangle(0, 0, res, res),
                    ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    int stride = bits.Stride;
                    byte[] raw = new byte[stride * res];
                    Marshal.Copy(bits.Scan0, raw, 0, raw.Length);

                    for (int y = 0; y < res; y++)
                    {
                        int row = y * stride;
                        for (int x = 0; x < res; x++)
                        {
                            int i = row + x * 4;
                            pixels[x + y * res] = Pack(raw[i + 2], raw[i + 1], raw[i], 255);
                        }
                    }
                }
                finally
                {
                    bitmap.UnlockBits(bits);
                }
            }

            return pixels;
        }

        // ==================================================================
        // 36 - FractalPlasma (old)
        // ==================================================================

        /// <summary>
        /// TexGen2.cpp: TEXTURE::FractalPlasmaOld. Six fixed octaves added
        /// together, which is what the aDDict2 loader runs for a filter id of 7.
        /// </summary>
        private static void FractalPlasmaOld(Ctx ctx, uint[] layer, byte[] d)
        {
            if (!HasData(d, 1)) return;

            int seed = d[0];
            byte[] octave = ctx.Bytes0;
            byte[] accum = ctx.Bytes1;
            Array.Clear(accum, 0, ctx.Count);

            for (int x = 1; x <= 6; x++)
            {
                GenerateSubPlasmaOld(octave, OctaveSize(x), seed, ctx.Res, ctx.Res, (1 << (8 - x)) - 1);

                for (int i = 0; i < ctx.Count; i++)
                {
                    accum[i] = unchecked((byte)(accum[i] + octave[i])); // add al,[edi] wraps
                }
            }

            for (int i = 0; i < ctx.Count; i++)
            {
                layer[i] = accum[i] * 0x01010101u;
            }
        }

        // ==================================================================
        // Operator composition
        // ==================================================================

        /// <summary>
        /// TexGen2.cpp: TEXTURE::PerformFilter. The command's OperatorMask is the
        /// low nibble of the stored operator byte: bit i means "apply the operator
        /// to channel i", where channel 0..3 is r,g,b,a. The C++ walks the four
        /// channels by offsetting the pixel pointer by the mask byte times the
        /// channel index, which for a 0/1 mask is just the channel index.
        /// </summary>
        private static void Compose(uint[] destination, uint[] source, int op, int mask, int count)
        {
            // On disk a single byte holds the operator in its high nibble and the
            // channel mask in its low nibble (see the .dmo reader's
            // `Operator >>= 4`). TexCommand splits them already, but a caller that
            // hands over the raw byte is handled here rather than silently
            // blending with the wrong operator.
            if (op < 0 || op > 3)
            {
                mask |= op & 0x0F;
                op = (op >> 4) & 3;
            }

            mask &= 0x0F;

            if (mask == 0) return;

            for (int i = 0; i < count; i++)
            {
                uint d = destination[i];
                uint s = source[i];

                if ((mask & 1) != 0) d = BlendChannel(d, s, op, 16); // r
                if ((mask & 2) != 0) d = BlendChannel(d, s, op, 8);  // g
                if ((mask & 4) != 0) d = BlendChannel(d, s, op, 0);  // b
                if ((mask & 8) != 0) d = BlendChannel(d, s, op, 24); // a

                destination[i] = d;
            }
        }

        /// <summary>
        /// One channel of one pixel through opproc[]. Add and subtract saturate
        /// (paddusb/psubusb), multiply shifts down by eight, and "X" replaces.
        /// </summary>
        private static uint BlendChannel(uint destination, uint source, int op, int shift)
        {
            int a = (int)((destination >> shift) & 0xFF);
            int b = (int)((source >> shift) & 0xFF);
            int value;

            switch (op)
            {
                case OperatorAdd:
                    value = a + b;
                    if (value > 255) value = 255;
                    break;

                case OperatorSub:
                    value = a - b;
                    if (value < 0) value = 0;
                    break;

                case OperatorMul:
                    value = (a * b) >> 8;
                    break;

                case OperatorReplace:
                default:
                    value = b;
                    break;
            }

            uint keep = ~(0xFFu << shift);
            return (destination & keep) | ((uint)value << shift);
        }
    }
}
