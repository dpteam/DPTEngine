//
// DPT.Engine - text rendering for the overlay.
// Copyright (C) 2026 DartPower Team. GPLv2.
//

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

using DPT.Engine.Graphics;
using DPT.Engine.Mathematics;
using DPT.Engine.Scenes;

namespace DPT.Engine.Rendering
{
    /// <summary>
    /// Draws short lines of text over the frame.
    ///
    /// Text is rasterised with GDI+ into a bitmap and uploaded as a texture,
    /// rather than shipping a bitmap font. The reasons are practical: a demo
    /// engine needs an FPS counter, a subsystem list and a log tail, all of which
    /// change every frame and none of which justify a font atlas; and GDI+ gives
    /// real antialiasing for free on every Windows version this engine supports.
    ///
    /// The cost is a texture upload per changed string. That is why results are
    /// cached by content: an FPS counter that updates ten times a second uploads
    /// ten small textures a second, not sixty.
    /// </summary>
    internal sealed class TextRenderer : IDisposable
    {
        private const int CacheLimit = 48;

        private readonly D3D9Device device;
        private readonly QuadRenderer quads;

        private readonly Dictionary<string, CacheEntry> cache =
            new Dictionary<string, CacheEntry>(StringComparer.Ordinal);

        private readonly List<string> cacheOrder = new List<string>();

        private readonly Font font;
        private readonly StringFormat format;

        private bool disposed;

        internal TextRenderer(D3D9Device device, QuadRenderer quads)
        {
            if (device == null) throw new ArgumentNullException("device");
            if (quads == null) throw new ArgumentNullException("quads");

            this.device = device;
            this.quads = quads;

            // A fixed-pitch face keeps a changing number from shifting the layout
            // sideways every time a digit changes width.
            font = new Font("Consolas", 13f, FontStyle.Regular, GraphicsUnit.Pixel);

            if (!font.Name.Equals("Consolas", StringComparison.OrdinalIgnoreCase))
            {
                // Consolas ships with Vista and later; XP falls back to Courier New,
                // which is close enough in metrics.
                font.Dispose();
                font = new Font("Courier New", 13f, FontStyle.Regular, GraphicsUnit.Pixel);
            }

            format = (StringFormat)StringFormat.GenericTypographic.Clone();
            format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
        }

        /// <summary>
        /// Draws a line of text at a screen position.
        ///
        /// <paramref name="shadow"/> draws a one-pixel dark copy behind the text,
        /// which is what makes it readable over a bright frame without a
        /// background box.
        /// </summary>
        internal void Draw(string text, int x, int y, ColorRgba colour, bool shadow)
        {
            if (disposed || string.IsNullOrEmpty(text)) return;

            if (!quads.IsReady) quads.Initialize();

            if (shadow)
            {
                DrawTexture(text, x + 1, y + 1, new ColorRgba(0, 0, 0, 200));
            }

            DrawTexture(text, x, y, colour);
        }

        private void DrawTexture(string text, int x, int y, ColorRgba colour)
        {
            var entry = Get(text);

            if (entry.Texture == null) return;

            quads.Draw(entry.Texture, x, y, entry.Width, entry.Height,
                colour, BlendMode.SourceAlpha, BlendMode.InverseSourceAlpha);
        }

        /// <summary>Width in pixels the string will occupy, for right-aligning.</summary>
        internal int Measure(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            return Get(text).Width;
        }

        private CacheEntry Get(string text)
        {
            CacheEntry entry;

            if (cache.TryGetValue(text, out entry))
            {
                entry.LastUsed = Environment.TickCount;
                return entry;
            }

            entry = Render(text);

            if (cache.Count >= CacheLimit) Evict();

            cache[text] = entry;
            cacheOrder.Add(text);

            return entry;
        }

        private void Evict()
        {
            // Oldest first. The overlay has a handful of stable strings plus one
            // changing counter, so this almost always drops the stale counter.
            var oldestIndex = -1;
            var oldestTick = int.MaxValue;

            for (var i = 0; i < cacheOrder.Count; i++)
            {
                CacheEntry entry;

                if (!cache.TryGetValue(cacheOrder[i], out entry)) continue;

                if (entry.LastUsed < oldestTick)
                {
                    oldestTick = entry.LastUsed;
                    oldestIndex = i;
                }
            }

            if (oldestIndex < 0)
            {
                cache.Clear();
                cacheOrder.Clear();
                return;
            }

            var key = cacheOrder[oldestIndex];
            CacheEntry stale;

            if (cache.TryGetValue(key, out stale) && stale.Texture != null)
            {
                stale.Texture.Dispose();
            }

            cache.Remove(key);
            cacheOrder.RemoveAt(oldestIndex);
        }

        private CacheEntry Render(string text)
        {
            var result = new CacheEntry();

            // Measure first, then draw into an exactly sized bitmap: a fixed-size
            // buffer would clip a long line and leave the rest of the overlay
            // misaligned.
            SizeF measured;

            using (var probe = new Bitmap(1, 1))
            {
                using (var graphics = System.Drawing.Graphics.FromImage(probe))
                {
                    graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    measured = graphics.MeasureString(text, font, int.MaxValue, format);
                }
            }

            var width = (int)Math.Ceiling(measured.Width) + 2;
            var height = (int)Math.Ceiling(font.GetHeight()) + 2;

            if (width < 1) width = 1;
            if (height < 1) height = 1;

            result.Width = width;
            result.Height = height;

            // Round up to a power of two: older hardware refuses anything else as
            // a managed texture, and this engine has to run there.
            var textureWidth = NextPowerOfTwo(width);
            var textureHeight = NextPowerOfTwo(height);

            using (var bitmap = new Bitmap(textureWidth, textureHeight,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
                {
                    graphics.Clear(Color.Transparent);
                    graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    graphics.DrawString(text, font, Brushes.White, 0f, 0f, format);
                }

                result.Texture = Upload(bitmap);
            }

            return result;
        }

        private D3D9Texture Upload(Bitmap bitmap)
        {
            var width = bitmap.Width;
            var height = bitmap.Height;
            var pixels = new uint[width * height];

            var data = bitmap.LockBits(new Rectangle(0, 0, width, height),
                System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            try
            {
                unsafe
                {
                    var source = (byte*)data.Scan0;

                    for (var y = 0; y < height; y++)
                    {
                        var row = source + y * data.Stride;

                        for (var x = 0; x < width; x++)
                        {
                            // GDI+ gives BGRA; the engine's texture uploader wants
                            // the same, but the project texture format is ARGB, so
                            // convert once here.
                            var b = row[x * 4 + 0];
                            var g = row[x * 4 + 1];
                            var r = row[x * 4 + 2];
                            var a = row[x * 4 + 3];

                            pixels[y * width + x] = ((uint)a << 24) | ((uint)r << 16) |
                                                    ((uint)g << 8) | b;
                        }
                    }
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }

            return CreateTexture(width, height, pixels);
        }

        private D3D9Texture CreateTexture(int width, int height, uint[] argb)
        {
            var texture = device.CreateTexture(width, height, 1, false);

            var bgra = new byte[width * height * 4];

            for (var i = 0; i < width * height; i++)
            {
                var colour = ColorRgba.FromArgb(argb[i]);
                bgra[i * 4 + 0] = colour.B;
                bgra[i * 4 + 1] = colour.G;
                bgra[i * 4 + 2] = colour.R;
                bgra[i * 4 + 3] = colour.A;
            }

            texture.UploadBgra(0, bgra);
            return texture;
        }

        private static int NextPowerOfTwo(int value)
        {
            var result = 1;

            while (result < value) result <<= 1;

            return result < 1 ? 1 : result;
        }

        /// <summary>Drops every cached texture, before a device reset.</summary>
        internal void ReleaseDeviceResources()
        {
            foreach (var entry in cache.Values)
            {
                if (entry.Texture != null) entry.Texture.Dispose();
            }

            cache.Clear();
            cacheOrder.Clear();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            ReleaseDeviceResources();

            format.Dispose();
            font.Dispose();
        }

        private struct CacheEntry
        {
            internal D3D9Texture Texture;
            internal int Width;
            internal int Height;
            internal int LastUsed;
        }
    }
}
