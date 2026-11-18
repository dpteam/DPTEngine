//
// DPT.Engine - the project catalogue.
// Copyright (C) 2026 DartPower Team. GPLv2.
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

using DPT.Engine.Shared;

namespace DPT.Engine.Master
{
    /// <summary>
    /// Published projects: the "platform" half of the directory.
    ///
    /// A project entry is metadata plus a URL, not content. The archive stays on
    /// the publisher's own host - which already serves HTTP for the editor - so the
    /// master never becomes a file server, never fills its disk, and never has to
    /// restart a 200 MB download. What the directory provides is discovery: a
    /// stable id, a name, an author, a size and a place to fetch from.
    ///
    /// Unlike sessions, projects are persisted. A published game should still be
    /// listed after the master restarts, which is the difference between a
    /// directory and a cache.
    /// </summary>
    internal sealed class ProjectCatalogue
    {
        private readonly object sync = new object();
        private readonly List<ProjectEntry> projects = new List<ProjectEntry>();
        private readonly MasterConfig config;
        private readonly string path;

        private bool dirty;

        internal ProjectCatalogue(MasterConfig config)
        {
            if (config == null) throw new ArgumentNullException("config");

            this.config = config;
            path = config.ResolveStateFile();
        }

        internal int Count
        {
            get { lock (sync) { return projects.Count; } }
        }

        // ---------------------------------------------------------------- queries

        internal MasterProjectInfo[] List()
        {
            lock (sync)
            {
                var result = new MasterProjectInfo[projects.Count];

                for (var i = 0; i < projects.Count; i++)
                {
                    result[i] = projects[i].Info;
                }

                return result;
            }
        }

        internal MasterProjectInfo Find(byte[] projectId)
        {
            if (projectId == null || projectId.Length != 16) return null;

            lock (sync)
            {
                for (var i = 0; i < projects.Count; i++)
                {
                    if (Same(projects[i].Info.Id, projectId)) return projects[i].Info;
                }

                return null;
            }
        }

        internal ProjectEntry FindByLease(byte[] lease)
        {
            if (lease == null || lease.Length != MasterProtocol.AuthSize) return null;

            lock (sync)
            {
                for (var i = 0; i < projects.Count; i++)
                {
                    if (Same(projects[i].Lease, lease)) return projects[i];
                }

                return null;
            }
        }

        // ---------------------------------------------------------------- mutation

        /// <summary>
        /// Publishes or updates a project.
        ///
        /// The id is assigned by the directory, not by the publisher: a client that
        /// could choose its own id could overwrite somebody else's entry. An update
        /// is identified by the lease, which is returned only to the publisher.
        /// </summary>
        internal MasterProjectInfo Publish(MasterProjectInfo info, byte[] lease, IPAddressLike address,
            out byte[] newLease, out string reason)
        {
            newLease = null;
            reason = null;

            if (info == null)
            {
                reason = "missing project record";
                return null;
            }

            if (info.Url.Length == 0)
            {
                reason = "a project must have a download URL";
                return null;
            }

            if (!IsHttpUrl(info.Url))
            {
                reason = "the download URL must be http or https";
                return null;
            }

            if (info.Bytes > config.MaxProjectBytes)
            {
                reason = "project is larger than the configured limit";
                return null;
            }

            lock (sync)
            {
                var existing = FindByLease(lease);

                if (existing != null)
                {
                    // Update: keep the id and creation time, refresh the rest.
                    existing.Info.Name = info.Name;
                    existing.Info.Author = info.Author;
                    existing.Info.Url = info.Url;
                    existing.Info.Description = info.Description;
                    existing.Info.Bytes = info.Bytes;
                    existing.Info.Version++;
                    existing.Info.Updated = NowSeconds();
                    existing.Info.Flags = info.Flags;
                    existing.Info.Kind = info.Kind;
                    existing.Info.Players = info.Players;

                    dirty = true;
                    newLease = existing.Lease;

                    return existing.Info;
                }

                if (projects.Count >= config.MaxProjects)
                {
                    reason = "the catalogue is full";
                    return null;
                }

                for (var i = 0; i < projects.Count; i++)
                {
                    if (!string.Equals(projects[i].Info.Name, info.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    reason = "a project with that name already exists";
                    return null;
                }

                var entry = new ProjectEntry();
                entry.Info = info;

                // Owner is a 16-byte publisher identity, not the address. The wire
                // format reserves sixteen bytes for it while an address is four, so
                // copying one into the other is a buffer overrun waiting to happen.
                entry.Info.Id = MasterProtocol.NewId();
                entry.Info.Owner = MasterProtocol.NewId();
                entry.Info.Created = NowSeconds();
                entry.Info.Updated = entry.Info.Created;
                entry.Info.Version = 1;
                entry.Info.Downloads = 0;
                entry.Lease = SessionDirectory.NewLease();

                projects.Add(entry);

                dirty = true;
                newLease = entry.Lease;

                return entry.Info;
            }
        }

        internal bool Remove(byte[] lease)
        {
            lock (sync)
            {
                for (var i = 0; i < projects.Count; i++)
                {
                    if (!Same(projects[i].Lease, lease)) continue;

                    projects.RemoveAt(i);
                    dirty = true;
                    return true;
                }

                return false;
            }
        }

        /// <summary>Counts a download. The client calls this after fetching the archive.</summary>
        internal bool Touch(byte[] projectId)
        {
            if (projectId == null || projectId.Length != 16) return false;

            lock (sync)
            {
                for (var i = 0; i < projects.Count; i++)
                {
                    if (!Same(projects[i].Info.Id, projectId)) continue;

                    projects[i].Info.Downloads++;
                    dirty = true;
                    return true;
                }

                return false;
            }
        }

        // ---------------------------------------------------------------- persistence

        /// <summary>
        /// Writes the catalogue if it changed.
        ///
        /// Called on a timer rather than on every change: a popular directory would
        /// otherwise rewrite the file on every download count, and the format is
        /// small enough that a periodic flush loses nothing worth keeping.
        /// </summary>
        internal void Flush()
        {
            lock (sync)
            {
                if (!dirty) return;

                dirty = false;

                try
                {
                    PortablePaths.EnsureParent(path);

                    var builder = new StringBuilder(1024);

                    // A simple line format: one project per line, fields separated
                    // by tabs. No JSON parser is needed to read it back and an
                    // operator can inspect or repair it with Notepad.
                    for (var i = 0; i < projects.Count; i++)
                    {
                        var entry = projects[i];
                        var info = entry.Info;

                        builder.Append(MasterProtocol.IdToHex(info.Id)).Append('\t');
                        builder.Append(MasterProtocol.IdToHex(info.Owner)).Append('\t');
                        builder.Append(MasterProtocol.IdToHex(entry.Lease)).Append('\t');
                        builder.Append(info.Created.ToString(CultureInfo.InvariantCulture)).Append('\t');
                        builder.Append(info.Updated.ToString(CultureInfo.InvariantCulture)).Append('\t');
                        builder.Append(info.Bytes.ToString(CultureInfo.InvariantCulture)).Append('\t');
                        builder.Append(info.Downloads.ToString(CultureInfo.InvariantCulture)).Append('\t');
                        builder.Append(info.Flags.ToString(CultureInfo.InvariantCulture)).Append('\t');
                        builder.Append(info.Version.ToString(CultureInfo.InvariantCulture)).Append('\t');
                        builder.Append(info.Kind.ToString(CultureInfo.InvariantCulture)).Append('\t');
                        builder.Append(info.Players.ToString(CultureInfo.InvariantCulture)).Append('\t');
                        builder.Append(Escape(info.Name)).Append('\t');
                        builder.Append(Escape(info.Author)).Append('\t');
                        builder.Append(Escape(info.Url)).Append('\t');
                        builder.Append(Escape(info.Description));

                        builder.Append('\n');
                    }

                    var temporary = path + ".tmp";
                    File.WriteAllText(temporary, builder.ToString(), new UTF8Encoding(false));

                    if (File.Exists(path)) File.Delete(path);
                    File.Move(temporary, path);
                }
                catch (Exception error)
                {
                    dirty = true;
                    Console.Error.WriteLine("Could not write " + path + ": " + error.Message);
                }
            }
        }

        internal void Load()
        {
            lock (sync)
            {
                projects.Clear();

                if (!File.Exists(path)) return;

                try
                {
                    var lines = File.ReadAllLines(path, Encoding.UTF8);

                    for (var i = 0; i < lines.Length; i++)
                    {
                        var line = lines[i];

                        if (line.Length == 0 || line[0] == '#') continue;

                        var fields = line.Split('\t');

                        if (fields.Length < 15) continue;

                        var entry = new ProjectEntry();

                        entry.Info = new MasterProjectInfo();
                        entry.Info.Id = MasterProtocol.IdFromHex(fields[0]) ?? new byte[16];
                        entry.Info.Owner = MasterProtocol.IdFromHex(fields[1]) ?? new byte[16];
                        entry.Lease = MasterProtocol.IdFromHex(fields[2]) ?? new byte[MasterProtocol.AuthSize];

                        entry.Info.Created = ParseUInt(fields[3]);
                        entry.Info.Updated = ParseUInt(fields[4]);
                        entry.Info.Bytes = ParseUInt(fields[5]);
                        entry.Info.Downloads = ParseUInt(fields[6]);
                        entry.Info.Flags = ParseUInt(fields[7]);
                        entry.Info.Version = (ushort)ParseUInt(fields[8]);
                        entry.Info.Kind = (ushort)ParseUInt(fields[9]);
                        entry.Info.Players = (ushort)ParseUInt(fields[10]);

                        entry.Info.Name = Unescape(fields[11]);
                        entry.Info.Author = Unescape(fields[12]);
                        entry.Info.Url = Unescape(fields[13]);
                        entry.Info.Description = Unescape(fields[14]);

                        projects.Add(entry);
                    }
                }
                catch (Exception error)
                {
                    Console.Error.WriteLine("Could not read " + path + ": " + error.Message);
                }
            }
        }

        // ---------------------------------------------------------------- helpers

        private static bool IsHttpUrl(string url)
        {
            return url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                   url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }

        private static uint ParseUInt(string text)
        {
            uint value;

            if (uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                return value;
            }

            return 0;
        }

        private static uint NowSeconds()
        {
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return (uint)(DateTime.UtcNow - epoch).TotalSeconds;
        }

        /// <summary>Escapes tabs and newlines so a name cannot break the line format.</summary>
        private static string Escape(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";

            return text.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\n", "\\n").Replace("\r", "");
        }

        private static string Unescape(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";

            var builder = new StringBuilder(text.Length);

            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] != '\\' || i + 1 >= text.Length)
                {
                    builder.Append(text[i]);
                    continue;
                }

                i++;

                switch (text[i])
                {
                    case 't': builder.Append('\t'); break;
                    case 'n': builder.Append('\n'); break;
                    case '\\': builder.Append('\\'); break;
                    default: builder.Append(text[i]); break;
                }
            }

            return builder.ToString();
        }

        private static bool Same(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;

            for (var i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }

            return true;
        }
    }

    /// <summary>A published project plus its lease.</summary>
    internal sealed class ProjectEntry
    {
        internal MasterProjectInfo Info;
        internal byte[] Lease;
    }

    /// <summary>
    /// The publisher's address, kept as raw bytes.
    ///
    /// A small type rather than IPAddress because the catalogue only ever needs the
    /// four bytes, and net40 and net9.0 disagree about which IPAddress members exist.
    /// </summary>
    internal struct IPAddressLike
    {
        internal byte[] Bytes;
    }
}
