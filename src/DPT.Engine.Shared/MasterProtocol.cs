//
// DPT.Engine - master directory protocol v1.
// Copyright (C) 2026 DartPower Team. GPLv2.
//

using System;
using System.Text;

namespace DPT.Engine.Shared
{
    /// <summary>
    /// The wire format shared by the master server, the relay, the runtime, the
    /// launcher and the editor host.
    ///
    /// One bounded request/response per TCP connection: a 16-byte header, then a
    /// fixed-size body. Fixed sizes are deliberate - a directory entry that
    /// cannot be mis-sized cannot be used to walk off the end of a buffer, and
    /// the master can reject a malformed request before allocating anything.
    ///
    /// The same directory serves two kinds of listing:
    ///   * live sessions - someone is hosting a game/demo right now, and the
    ///     relay can be asked for a route when the host has no public port;
    ///   * published projects - the "platform" listing: a game or demo stored on
    ///     a host, downloadable by id, with a URL the client fetches over HTTP.
    /// </summary>
    public static class MasterProtocol
    {
        public const int Version = 1;

        /// <summary>
        /// DPT.Engine sessions are their own protocol id: a DPT.DOOM 2D host and
        /// a DPT.Engine host must never end up in each other's directory entry.
        /// </summary>
        public const int GameProtocol = 8;

        public const int HeaderSize = 16;
        public const int InfoSize = 120;
        public const int AuthSize = 48;
        public const int ProjectRowSize = 448;

        public const int MaxServers = 128;
        public const int MaxServersPerIp = 4;
        public const int MaxProjects = 256;
        public const int MaxManifestSize = 64 * 1024;

        public const int MaxBodySize = AuthSize + ProjectRowSize + MaxManifestSize;

        /// <summary>Default port of the directory. The relay listens on the next one.</summary>
        public const int DefaultPort = 25150;

        public const int DefaultRelayPort = 25151;

        public const ushort OpList = 1;
        public const ushort OpRegister = 2;
        public const ushort OpHeartbeat = 3;
        public const ushort OpRemove = 4;
        public const ushort OpRelayHost = 5;
        public const ushort OpRelayJoin = 6;
        public const ushort OpManifest = 7;
        public const ushort OpProjectPublish = 8;
        public const ushort OpProjectList = 9;
        public const ushort OpProjectFetch = 10;
        public const ushort OpProjectRemove = 11;
        public const ushort OpProjectTouch = 12;

        public const ushort StatusOk = 0;
        public const ushort StatusBadRequest = 1;
        public const ushort StatusUnauthorized = 2;
        public const ushort StatusConflict = 3;
        public const ushort StatusCapacity = 4;
        public const ushort StatusRedirect = 5;
        public const ushort StatusNotFound = 6;

        public const uint RelayAvailable = 1;

        public static readonly byte[] Magic = { (byte)'D', (byte)'P', (byte)'T', (byte)'M' };

        public static int Get16(byte[] buffer, int offset)
        {
            return buffer[offset] | (buffer[offset + 1] << 8);
        }

        public static uint Get32(byte[] buffer, int offset)
        {
            return (uint)(buffer[offset] |
                          (buffer[offset + 1] << 8) |
                          (buffer[offset + 2] << 16) |
                          (buffer[offset + 3] << 24));
        }

        public static void Put16(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
        }

        public static void Put32(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }

        public static byte[] Header(ushort operation, int bodyLength, ushort status)
        {
            var header = new byte[HeaderSize];
            Array.Copy(Magic, header, 4);
            Put16(header, 4, Version);
            Put16(header, 6, operation);
            Put32(header, 8, (uint)bodyLength);
            Put16(header, 12, status);
            Put16(header, 14, 0);
            return header;
        }

        /// <summary>
        /// Returns the expected body length for a request, or -1 when the header
        /// is malformed.
        /// </summary>
        public static int RequestLength(byte[] header)
        {
            if (header == null || header.Length < HeaderSize) return -1;

            if (header[0] != Magic[0] || header[1] != Magic[1] ||
                header[2] != Magic[2] || header[3] != Magic[3])
            {
                return -1;
            }

            if (Get16(header, 4) != Version || Get16(header, 12) != 0 || Get16(header, 14) != 0)
            {
                return -1;
            }

            var operation = Get16(header, 6);
            var length = (int)Get32(header, 8);

            switch (operation)
            {
                case OpList:
                case OpProjectList:
                    return length == 0 ? 0 : -1;

                case OpRegister:
                    return length == InfoSize ? InfoSize : -1;

                case OpHeartbeat:
                case OpRemove:
                case OpRelayHost:
                case OpProjectRemove:
                    return length == AuthSize ? AuthSize : -1;

                case OpRelayJoin:
                case OpProjectFetch:
                case OpProjectTouch:
                    return length == 16 ? 16 : -1;

                case OpManifest:
                    // Publish: auth followed by the manifest. Fetch: a 16-byte id.
                    if (length == 16) return 16;
                    if (length >= AuthSize && length <= AuthSize + MaxManifestSize) return length;
                    return -1;

                case OpProjectPublish:
                    return length == AuthSize + ProjectRowSize ? length : -1;
            }

            return -1;
        }

        /// <summary>Human-readable reason for a rejected header, or null when it is valid.</summary>
        public static string HeaderError(byte[] header)
        {
            if (header == null || header.Length < HeaderSize) return "short-header";

            if (header[0] != Magic[0] || header[1] != Magic[1] ||
                header[2] != Magic[2] || header[3] != Magic[3])
            {
                return "magic-not-DPTM";
            }

            if (Get16(header, 4) != Version)
            {
                return "directory-version-mismatch";
            }

            if (Get16(header, 12) != 0 || Get16(header, 14) != 0)
            {
                return "nonzero-request-status";
            }

            var operation = Get16(header, 6);
            if (operation < OpList || operation > OpProjectTouch)
            {
                return "unknown-operation";
            }

            if (RequestLength(header) < 0)
            {
                return "invalid-body-length";
            }

            return null;
        }

        /// <summary>
        /// Identifiers travel as 16 raw bytes on the wire and as 32 hex characters
        /// whenever a person has to retype one. Both conversions live here so the
        /// master, the launcher and the editor cannot disagree about the spelling.
        /// </summary>
        public static string IdToHex(byte[] id)
        {
            const string digits = "0123456789abcdef";

            var text = new char[id.Length * 2];

            for (var i = 0; i < id.Length; i++)
            {
                text[i * 2] = digits[id[i] >> 4];
                text[i * 2 + 1] = digits[id[i] & 0x0F];
            }

            return new string(text);
        }

        /// <summary>Returns null when the text is not exactly 32 hex characters.</summary>
        public static byte[] IdFromHex(string text)
        {
            if (text == null || text.Length != 32) return null;

            var id = new byte[16];

            for (var i = 0; i < 16; i++)
            {
                var high = HexDigit(text[i * 2]);
                var low = HexDigit(text[i * 2 + 1]);

                if (high < 0 || low < 0) return null;

                id[i] = (byte)((high << 4) | low);
            }

            return id;
        }

        private static int HexDigit(char ch)
        {
            if (ch >= '0' && ch <= '9') return ch - '0';
            if (ch >= 'a' && ch <= 'f') return ch - 'a' + 10;
            if (ch >= 'A' && ch <= 'F') return ch - 'A' + 10;
            return -1;
        }

        public static bool IsPrintableAscii(string text)
        {
            foreach (var ch in text)
            {
                if (ch < 0x20 || ch > 0x7E) return false;
            }

            return true;
        }

        public static string ReadAscii(byte[] buffer, int offset, int length)
        {
            var end = offset;
            while (end < offset + length && buffer[end] != 0)
            {
                end++;
            }

            return Encoding.ASCII.GetString(buffer, offset, end - offset);
        }

        public static void WriteAscii(byte[] buffer, int offset, int length, string text)
        {
            Array.Clear(buffer, offset, length);

            if (string.IsNullOrEmpty(text)) return;

            var count = Math.Min(text.Length, length - 1);
            for (var i = 0; i < count; i++)
            {
                var ch = text[i];
                buffer[offset + i] = ch < 0x20 || ch > 0x7E ? (byte)'?' : (byte)ch;
            }
        }

        /// <summary>Random 16-byte identifier, from the platform CSPRNG.</summary>
        public static byte[] NewId()
        {
            var id = new byte[16];
            var random = System.Security.Cryptography.RandomNumberGenerator.Create();
            try
            {
                random.GetBytes(id);
            }
            finally
            {
                // RandomNumberGenerator has no IDisposable on net40.
            }

            return id;
        }
    }
}
