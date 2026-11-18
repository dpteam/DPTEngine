//
// DPT.Engine - the session directory.
// Copyright (C) 2026 DartPower Team. GPLv2.
//

using System;
using System.Collections.Generic;
using System.Net;

using DPT.Engine.Shared;

namespace DPT.Engine.Master
{
    /// <summary>A registered session, plus the secrets and state the directory keeps about it.</summary>
    internal sealed class SessionEntry
    {
        internal MasterSessionInfo Info;

        /// <summary>The 48-byte lease the host must present to keep or remove the entry.</summary>
        internal byte[] Lease = new byte[MasterProtocol.AuthSize];

        /// <summary>Last heartbeat, in seconds since the directory started.</summary>
        internal long LastSeen;

        /// <summary>Manifest the host published, or null.</summary>
        internal byte[] Manifest;

        /// <summary>True when the host asked for a relay route.</summary>
        internal bool WantsRelay;

        /// <summary>Set when a relay route was handed out, so the relay can match them up.</summary>
        internal byte[] RelaySessionId = new byte[16];
    }

    /// <summary>
    /// The in-memory directory of live sessions.
    ///
    /// Deliberately in memory and deliberately dumb: a directory is a cache of
    /// things that are true right now, and persisting it would mean serving stale
    /// sessions after a restart. Projects, which are not ephemeral, are kept
    /// separately and can be persisted.
    ///
    /// Every method locks. A directory is touched by one thread per connection and
    /// the lists are small, so a single lock is faster and far easier to reason
    /// about than fine-grained synchronisation.
    /// </summary>
    internal sealed class SessionDirectory
    {
        private readonly object sync = new object();
        private readonly List<SessionEntry> sessions = new List<SessionEntry>();
        private readonly MasterConfig config;

        private long started;

        internal SessionDirectory(MasterConfig config)
        {
            if (config == null) throw new ArgumentNullException("config");
            this.config = config;
        }

        internal long UptimeSeconds
        {
            get { return Now() - started; }
        }

        internal void Start()
        {
            lock (sync)
            {
                started = Now();
            }
        }

        internal int Count
        {
            get { lock (sync) { return sessions.Count; } }
        }

        // ---------------------------------------------------------------- queries

        internal MasterSessionInfo[] List()
        {
            lock (sync)
            {
                Expire();

                var result = new MasterSessionInfo[sessions.Count];

                for (var i = 0; i < sessions.Count; i++)
                {
                    result[i] = sessions[i].Info;
                }

                return result;
            }
        }

        internal SessionEntry Find(byte[] sessionId)
        {
            if (sessionId == null || sessionId.Length != 16) return null;

            lock (sync)
            {
                for (var i = 0; i < sessions.Count; i++)
                {
                    if (Same(sessions[i].Info.Id, sessionId)) return sessions[i];
                }

                return null;
            }
        }

        internal SessionEntry FindByLease(byte[] lease)
        {
            if (lease == null || lease.Length != MasterProtocol.AuthSize) return null;

            lock (sync)
            {
                for (var i = 0; i < sessions.Count; i++)
                {
                    if (Same(sessions[i].Lease, lease)) return sessions[i];
                }

                return null;
            }
        }

        // ---------------------------------------------------------------- mutation

        /// <summary>
        /// Registers a session and returns its lease, or null when the request is
        /// refused.
        ///
        /// The refusal cases are all real abuse vectors: too many sessions from one
        /// address, the directory being full, or a duplicate name that would make
        /// the list confusing to a player.
        /// </summary>
        internal byte[] Register(MasterSessionInfo info, IPAddress address, out string reason)
        {
            reason = null;

            if (info == null)
            {
                reason = "missing session record";
                return null;
            }

            lock (sync)
            {
                Expire();

                if (sessions.Count >= config.MaxSessions)
                {
                    reason = "directory full";
                    return null;
                }

                var fromAddress = 0;

                for (var i = 0; i < sessions.Count; i++)
                {
                    if (sessions[i].Info.AddressText == AddressText(address)) fromAddress++;
                }

                if (fromAddress >= config.MaxSessionsPerAddress)
                {
                    reason = "too many sessions from " + AddressText(address);
                    return null;
                }

                for (var i = 0; i < sessions.Count; i++)
                {
                    if (string.Equals(sessions[i].Info.Name, info.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        reason = "name already in use";
                        return null;
                    }
                }

                var entry = new SessionEntry();
                entry.Info = info;
                entry.Info.Protocol = (ushort)MasterProtocol.GameProtocol;

                // The address is taken from the socket, never from the request: a
                // host must not be able to advertise somebody else's address.
                entry.Info.Address = AddressBytes(address);

                // A lease is AuthSize bytes, not 16: it is a bearer token the host
                // presents on every subsequent call, and it is deliberately longer
                // than a session id so the two cannot be confused for one another.
                entry.Lease = NewLease();
                entry.LastSeen = Now();

                sessions.Add(entry);

                return entry.Lease;
            }
        }

        internal bool Heartbeat(byte[] lease)
        {
            var entry = FindByLease(lease);

            if (entry == null) return false;

            lock (sync)
            {
                entry.LastSeen = Now();
            }

            return true;
        }

        internal bool Remove(byte[] lease)
        {
            lock (sync)
            {
                for (var i = 0; i < sessions.Count; i++)
                {
                    if (!Same(sessions[i].Lease, lease)) continue;

                    sessions.RemoveAt(i);
                    return true;
                }

                return false;
            }
        }

        internal bool PublishManifest(byte[] lease, byte[] manifest)
        {
            var entry = FindByLease(lease);

            if (entry == null) return false;

            lock (sync)
            {
                entry.Manifest = manifest;
                entry.LastSeen = Now();
            }

            return true;
        }

        internal byte[] FetchManifest(byte[] sessionId)
        {
            var entry = Find(sessionId);

            if (entry == null) return null;

            lock (sync)
            {
                return entry.Manifest;
            }
        }

        internal bool RequestRelay(byte[] lease)
        {
            var entry = FindByLease(lease);

            if (entry == null) return false;

            lock (sync)
            {
                entry.WantsRelay = true;
                entry.LastSeen = Now();
            }

            return true;
        }

        /// <summary>
        /// Drops sessions that have not sent a heartbeat.
        ///
        /// This is the only thing keeping the list honest: a host that crashes or
        /// loses its network never sends a goodbye, and without expiry its entry
        /// would sit there forever advertising a game nobody can join.
        /// </summary>
        private void Expire()
        {
            var now = Now();

            for (var i = sessions.Count - 1; i >= 0; i--)
            {
                if (now - sessions[i].LastSeen <= config.SessionTimeoutSeconds) continue;

                sessions.RemoveAt(i);
            }
        }

        // ---------------------------------------------------------------- helpers

        private long Now()
        {
            // Environment.TickCount wraps every 49 days; the subtraction used here
            // is wrap-safe because both operands come from the same clock.
            return Environment.TickCount / 1000L;
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

        /// <summary>
        /// A fresh lease: AuthSize random bytes.
        ///
        /// Generated by concatenating three 16-byte ids rather than by a bespoke
        /// call, because MasterProtocol.NewId is the only place that reaches the
        /// platform CSPRNG and duplicating that would be the sort of thing that
        /// silently falls back to System.Random on one target framework.
        /// </summary>
        internal static byte[] NewLease()
        {
            var lease = new byte[MasterProtocol.AuthSize];
            var filled = 0;

            while (filled < lease.Length)
            {
                var chunk = MasterProtocol.NewId();
                var count = Math.Min(chunk.Length, lease.Length - filled);

                Array.Copy(chunk, 0, lease, filled, count);
                filled += count;
            }

            return lease;
        }

        internal static byte[] AddressBytes(IPAddress address)
        {
            if (address == null) return new byte[4];

            var bytes = address.GetAddressBytes();

            if (bytes.Length == 4) return bytes;

            // An IPv4-mapped IPv6 address (::ffff:a.b.c.d) is what a dual-stack
            // socket reports; the directory stores IPv4 because the wire format is
            // four bytes.
            if (bytes.Length == 16)
            {
                var mapped = new byte[4];
                Array.Copy(bytes, 12, mapped, 0, 4);
                return mapped;
            }

            return new byte[4];
        }

        internal static string AddressText(IPAddress address)
        {
            var bytes = AddressBytes(address);
            return bytes[0] + "." + bytes[1] + "." + bytes[2] + "." + bytes[3];
        }
    }
}
