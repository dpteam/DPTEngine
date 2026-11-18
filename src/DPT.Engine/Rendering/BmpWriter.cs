//
// DPT.Engine - BMP writer for screenshots.
// Copyright (C) 2026 DartPower Team. GPLv2.
//

using System;
using System.IO;

namespace DPT.Engine.Rendering
{
    /// <summary>
    /// Writes a 24-bit BMP.
    ///
    /// BMP rather than PNG because it needs no compression library and no
    /// dependency, and a screenshot is a diagnostic artefact rather than something
    /// that gets published. The engine's documentation images are converted to PNG
    /// by the build script.
    /// </summary>
    internal static class BmpWriter
    {
        private const int FileHeaderSize = 14;
        private const int InfoHeaderSize = 40;

        /// <summary>
        /// Writes a BMP from BGRA pixels, as Direct3D hands them back.
        ///
        /// Rows are flipped: Direct3D's origin is top-left, a BMP's is bottom-left.
        /// Getting this wrong produces an upside-down screenshot, which is the kind
        /// of bug that survives for years because nobody looks at the file.
        /// </summary>
        internal static void Write(string path, byte[] bgra, int width, int height)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException("path");
            if (bgra == null) throw new ArgumentNullException("bgra");
            if (width <= 0) throw new ArgumentOutOfRangeException("width");
            if (height <= 0) throw new ArgumentOutOfRangeException("height");

            var rowBytes = width * 3;
            var padding = (4 - rowBytes % 4) % 4;
            var stride = rowBytes + padding;

            var pixelBytes = stride * height;
            var fileSize = FileHeaderSize + InfoHeaderSize + pixelBytes;

            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                using (var writer = new BinaryWriter(stream))
                {
                    // BITMAPFILEHEADER
                    writer.Write((byte)'B');
                    writer.Write((byte)'M');
                    writer.Write(fileSize);
                    writer.Write(0);
                    writer.Write(FileHeaderSize + InfoHeaderSize);

                    // BITMAPINFOHEADER
                    writer.Write(InfoHeaderSize);
                    writer.Write(width);
                    writer.Write(height);
                    writer.Write((short)1);   // planes
                    writer.Write((short)24);  // bits per pixel
                    writer.Write(0);          // BI_RGB
                    writer.Write(pixelBytes);
                    writer.Write(2835);       // 72 dpi
                    writer.Write(2835);
                    writer.Write(0);
                    writer.Write(0);

                    var row = new byte[stride];

                    for (var y = height - 1; y >= 0; y--)
                    {
                        var source = y * width * 4;

                        for (var x = 0; x < width; x++)
                        {
                            row[x * 3 + 0] = bgra[source + x * 4 + 0]; // B
                            row[x * 3 + 1] = bgra[source + x * 4 + 1]; // G
                            row[x * 3 + 2] = bgra[source + x * 4 + 2]; // R
                        }

                        for (var p = rowBytes; p < stride; p++)
                        {
                            row[p] = 0;
                        }

                        writer.Write(row);
                    }
                }
            }
        }
    }
}
