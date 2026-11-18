//
// DPT.Engine - 32-bit colour.
// Copyright (C) 2026 DartPower Team. GPLv2.
//

using System;
using System.Globalization;

namespace DPT.Engine.Mathematics
{
    /// <summary>
    /// A colour as four bytes, stored as 0xAARRGGBB.
    ///
    /// Deliberately not float: the project format stores colours as four bytes,
    /// the shaders take them pre-multiplied into a DWORD, and a 32-bit value
    /// round-trips through a file without the drift that float normalisation
    /// introduces (0.5f * 255 = 127.5, which truncates differently depending on
    /// which direction you convert).
    ///
    /// The memory layout is A, R, G, B so that a DWORD written to a vertex or a
    /// texture is exactly D3DCOLOR, which is what every D3D9 call expects.
    /// </summary>
    public struct ColorRgba : IEquatable<ColorRgba>
    {
        public byte R;
        public byte G;
        public byte B;
        public byte A;

        public static readonly ColorRgba Transparent = new ColorRgba(0, 0, 0, 0);
        public static readonly ColorRgba Black = new ColorRgba(0, 0, 0, 255);
        public static readonly ColorRgba White = new ColorRgba(255, 255, 255, 255);
        public static readonly ColorRgba Red = new ColorRgba(255, 0, 0, 255);
        public static readonly ColorRgba Green = new ColorRgba(0, 255, 0, 255);
        public static readonly ColorRgba Blue = new ColorRgba(0, 0, 255, 255);

        public ColorRgba(byte r, byte g, byte b)
        {
            R = r;
            G = g;
            B = b;
            A = 255;
        }

        public ColorRgba(byte r, byte g, byte b, byte a)
        {
            R = r;
            G = g;
            B = b;
            A = a;
        }

        /// <summary>Reads 0xAARRGGBB, the layout the project format uses.</summary>
        public static ColorRgba FromArgb(uint value)
        {
            return new ColorRgba(
                (byte)((value >> 16) & 0xFF),
                (byte)((value >> 8) & 0xFF),
                (byte)(value & 0xFF),
                (byte)((value >> 24) & 0xFF));
        }

        /// <summary>
        /// Reads a D3DCOLOR, which is 0xAARRGGBB in memory but written as
        /// 0x00BBGGRR when a little-endian DWORD is read as a number.
        ///
        /// The engine's project files store the ARGB form, so this conversion
        /// lives here rather than being open-coded at each upload site.
        /// </summary>
        public static ColorRgba FromD3DColor(uint value)
        {
            return new ColorRgba(
                (byte)((value >> 16) & 0xFF),
                (byte)((value >> 8) & 0xFF),
                (byte)(value & 0xFF),
                (byte)((value >> 24) & 0xFF));
        }

        /// <summary>Packs to a D3DCOLOR: 0xAARRGGBB.</summary>
        public uint ToD3DColor()
        {
            return ((uint)A << 24) | ((uint)R << 16) | ((uint)G << 8) | B;
        }

        public uint ToArgb()
        {
            return ToD3DColor();
        }

        /// <summary>Packs to the 0xAABBGGRR order a BGRA texture upload wants.</summary>
        public uint ToBgraDword()
        {
            return ((uint)A << 24) | ((uint)B << 16) | ((uint)G << 8) | R;
        }

        /// <summary>Luminance in 0..1, using the ITU-R BT.601 weights.</summary>
        public float Luminance
        {
            get { return (R * 0.299f + G * 0.587f + B * 0.114f) / 255f; }
        }

        public static ColorRgba Lerp(ColorRgba from, ColorRgba to, float amount)
        {
            if (amount <= 0f) return from;
            if (amount >= 1f) return to;

            return new ColorRgba(
                (byte)(from.R + (to.R - from.R) * amount),
                (byte)(from.G + (to.G - from.G) * amount),
                (byte)(from.B + (to.B - from.B) * amount),
                (byte)(from.A + (to.A - from.A) * amount));
        }

        public static ColorRgba operator *(ColorRgba color, float scale)
        {
            return new ColorRgba(
                ClampByte(color.R * scale),
                ClampByte(color.G * scale),
                ClampByte(color.B * scale),
                ClampByte(color.A * scale));
        }

        public static ColorRgba operator *(ColorRgba a, ColorRgba b)
        {
            // Multiply is normalised: (a * b) / 255, so a white texel leaves the
            // other operand untouched.
            return new ColorRgba(
                (byte)(a.R * b.R / 255),
                (byte)(a.G * b.G / 255),
                (byte)(a.B * b.B / 255),
                (byte)(a.A * b.A / 255));
        }

        public static bool operator ==(ColorRgba a, ColorRgba b)
        {
            return a.R == b.R && a.G == b.G && a.B == b.B && a.A == b.A;
        }

        public static bool operator !=(ColorRgba a, ColorRgba b)
        {
            return !(a == b);
        }

        private static byte ClampByte(float value)
        {
            if (value <= 0f) return 0;
            if (value >= 255f) return 255;
            return (byte)value;
        }

        /// <summary>Parses "#RRGGBB" or "#AARRGGBB", as used in the editor's JSON.</summary>
        public static ColorRgba Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return White;

            var value = text.Trim();
            if (value.StartsWith("#", StringComparison.Ordinal)) value = value.Substring(1);

            uint packed;

            if (!uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out packed))
            {
                return White;
            }

            if (value.Length == 6)
            {
                return new ColorRgba(
                    (byte)((packed >> 16) & 0xFF),
                    (byte)((packed >> 8) & 0xFF),
                    (byte)(packed & 0xFF));
            }

            if (value.Length == 8) return FromArgb(packed);

            return White;
        }

        public string ToHex()
        {
            return "#" + R.ToString("X2", CultureInfo.InvariantCulture) +
                   G.ToString("X2", CultureInfo.InvariantCulture) +
                   B.ToString("X2", CultureInfo.InvariantCulture) +
                   A.ToString("X2", CultureInfo.InvariantCulture);
        }

        public override bool Equals(object obj)
        {
            return obj is ColorRgba && Equals((ColorRgba)obj);
        }

        public bool Equals(ColorRgba other)
        {
            return this == other;
        }

        public override int GetHashCode()
        {
            return (int)ToD3DColor();
        }

        public override string ToString()
        {
            return ToHex();
        }
    }
}
