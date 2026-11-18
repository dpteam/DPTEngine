//
// DPT.Engine - published project record.
// Copyright (C) 2026 DartPower Team. GPLv2.
//

using System;

namespace DPT.Engine.Shared
{
    /// <summary>
    /// One row of the platform directory: a game or demo published by a host.
    ///
    /// The directory deliberately stores metadata and a URL, not content. The
    /// content stays on the publisher's editor host (which already speaks HTTP
    /// for the editor), so the master never becomes a file server and a project
    /// can be hundreds of megabytes without touching the directory's memory.
    /// </summary>
    public sealed class MasterProjectInfo
    {
        public const int KindGame = 0;
        public const int KindDemo = 1;
        public const int KindTool = 2;
        public const int KindTemplate = 3;

        public const uint FlagPublished = 1;
        public const uint FlagOnline = 2;
        public const uint FlagFeatured = 4;
        public const uint FlagUnlisted = 8;

        public byte[] Id = new byte[16];
        public byte[] Owner = new byte[16];

        /// <summary>Unix seconds when the project was first published.</summary>
        public uint Created;

        /// <summary>Unix seconds of the last publish.</summary>
        public uint Updated;

        public uint Bytes;
        public uint Downloads;
        public uint Flags;
        public ushort Version;
        public ushort Kind;
        public ushort Players;
        public ushort Reserved;

        public string Name = "";
        public string Author = "";

        /// <summary>Absolute http(s) URL the client downloads the project archive from.</summary>
        public string Url = "";

        public string Description = "";

        public string IdHex
        {
            get { return MasterProtocol.IdToHex(Id); }
        }

        public void Encode(byte[] buffer, int offset)
        {
            if (buffer == null) throw new ArgumentNullException("buffer");
            if (buffer.Length - offset < MasterProtocol.ProjectRowSize) throw new ArgumentException("Buffer too small.", "buffer");

            Array.Clear(buffer, offset, MasterProtocol.ProjectRowSize);
            Array.Copy(Id, 0, buffer, offset, 16);
            Array.Copy(Owner, 0, buffer, offset + 16, 16);

            MasterProtocol.Put32(buffer, offset + 32, Created);
            MasterProtocol.Put32(buffer, offset + 36, Updated);
            MasterProtocol.Put32(buffer, offset + 40, Bytes);
            MasterProtocol.Put32(buffer, offset + 44, Downloads);
            MasterProtocol.Put32(buffer, offset + 48, Flags);

            MasterProtocol.Put16(buffer, offset + 52, Version);
            MasterProtocol.Put16(buffer, offset + 54, Kind);
            MasterProtocol.Put16(buffer, offset + 56, Players);
            MasterProtocol.Put16(buffer, offset + 58, Reserved);

            MasterProtocol.WriteAscii(buffer, offset + 60, 64, Name);
            MasterProtocol.WriteAscii(buffer, offset + 124, 32, Author);
            MasterProtocol.WriteAscii(buffer, offset + 156, 192, Url);
            MasterProtocol.WriteAscii(buffer, offset + 348, 96, Description);
        }

        public static MasterProjectInfo Decode(byte[] buffer, int offset)
        {
            if (buffer == null) throw new ArgumentNullException("buffer");
            if (buffer.Length - offset < MasterProtocol.ProjectRowSize) throw new ArgumentException("Buffer too small.", "buffer");

            var info = new MasterProjectInfo();
            Array.Copy(buffer, offset, info.Id, 0, 16);
            Array.Copy(buffer, offset + 16, info.Owner, 0, 16);

            info.Created = MasterProtocol.Get32(buffer, offset + 32);
            info.Updated = MasterProtocol.Get32(buffer, offset + 36);
            info.Bytes = MasterProtocol.Get32(buffer, offset + 40);
            info.Downloads = MasterProtocol.Get32(buffer, offset + 44);
            info.Flags = MasterProtocol.Get32(buffer, offset + 48);

            info.Version = (ushort)MasterProtocol.Get16(buffer, offset + 52);
            info.Kind = (ushort)MasterProtocol.Get16(buffer, offset + 54);
            info.Players = (ushort)MasterProtocol.Get16(buffer, offset + 56);
            info.Reserved = (ushort)MasterProtocol.Get16(buffer, offset + 58);

            info.Name = MasterProtocol.ReadAscii(buffer, offset + 60, 64);
            info.Author = MasterProtocol.ReadAscii(buffer, offset + 124, 32);
            info.Url = MasterProtocol.ReadAscii(buffer, offset + 156, 192);
            info.Description = MasterProtocol.ReadAscii(buffer, offset + 348, 96);

            return info;
        }

        public string KindText
        {
            get
            {
                switch (Kind)
                {
                    case KindGame: return "game";
                    case KindDemo: return "demo";
                    case KindTool: return "tool";
                    default: return "template";
                }
            }
        }
    }
}
