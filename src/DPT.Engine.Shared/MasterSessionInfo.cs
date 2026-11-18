//
// DPT.Engine - live session record.
// Copyright (C) 2026 DartPower Team. GPLv2.
//

using System;

namespace DPT.Engine.Shared
{
    /// <summary>
    /// One row of the session directory: who is hosting, where, how full the game
    /// is. Fixed layout, little-endian, 120 bytes - see MasterProtocol.InfoSize.
    /// </summary>
    public sealed class MasterSessionInfo
    {
        public byte[] Id = new byte[16];
        public byte[] Address = new byte[4];
        public ushort Port;
        public ushort Protocol;
        public ushort Players;
        public ushort MaxPlayers;
        public uint Flags;
        public ushort Version;
        public string Name = "";
        public string Map = "";

        public const uint FlagPassword = 1;
        public const uint FlagRelay = 2;
        public const uint FlagDedicated = 4;

        public string IdHex
        {
            get { return MasterProtocol.IdToHex(Id); }
        }

        public string AddressText
        {
            get { return Address[0] + "." + Address[1] + "." + Address[2] + "." + Address[3]; }
        }

        public void Encode(byte[] buffer, int offset)
        {
            if (buffer == null) throw new ArgumentNullException("buffer");
            if (buffer.Length - offset < MasterProtocol.InfoSize) throw new ArgumentException("Buffer too small.", "buffer");

            Array.Clear(buffer, offset, MasterProtocol.InfoSize);
            Array.Copy(Id, 0, buffer, offset, 16);
            Array.Copy(Address, 0, buffer, offset + 16, 4);

            MasterProtocol.Put16(buffer, offset + 20, Port);
            MasterProtocol.Put16(buffer, offset + 22, Protocol);
            MasterProtocol.Put16(buffer, offset + 24, Players);
            MasterProtocol.Put16(buffer, offset + 26, MaxPlayers);
            MasterProtocol.Put32(buffer, offset + 28, Flags);
            MasterProtocol.Put16(buffer, offset + 32, Version);

            MasterProtocol.WriteAscii(buffer, offset + 34, 64, Name);
            MasterProtocol.WriteAscii(buffer, offset + 98, 16, Map);
        }

        public static MasterSessionInfo Decode(byte[] buffer, int offset)
        {
            if (buffer == null) throw new ArgumentNullException("buffer");
            if (buffer.Length - offset < MasterProtocol.InfoSize) throw new ArgumentException("Buffer too small.", "buffer");

            var info = new MasterSessionInfo();
            Array.Copy(buffer, offset, info.Id, 0, 16);
            Array.Copy(buffer, offset + 16, info.Address, 0, 4);

            info.Port = (ushort)MasterProtocol.Get16(buffer, offset + 20);
            info.Protocol = (ushort)MasterProtocol.Get16(buffer, offset + 22);
            info.Players = (ushort)MasterProtocol.Get16(buffer, offset + 24);
            info.MaxPlayers = (ushort)MasterProtocol.Get16(buffer, offset + 26);
            info.Flags = MasterProtocol.Get32(buffer, offset + 28);
            info.Version = (ushort)MasterProtocol.Get16(buffer, offset + 32);
            info.Name = MasterProtocol.ReadAscii(buffer, offset + 34, 64);
            info.Map = MasterProtocol.ReadAscii(buffer, offset + 98, 16);

            return info;
        }
    }
}
