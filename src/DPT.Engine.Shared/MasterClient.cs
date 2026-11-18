//
// DPT.Engine - master directory client.
// Copyright (C) 2026 DartPower Team. GPLv2.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace DPT.Engine.Shared
{
    /// <summary>A relay endpoint returned by the directory.</summary>
    public struct RelayRoute
    {
        public byte[] Address;
        public ushort Port;

        public string AddressText
        {
            get { return Address == null ? "0.0.0.0" : Address[0] + "." + Address[1] + "." + Address[2] + "." + Address[3]; }
        }
    }

    /// <summary>
    /// Blocking client for the directory.
    ///
    /// The engine's network code runs on its own thread and is written with
    /// blocking sockets on purpose: a demo or a game loop that has to poll a
    /// socket every frame is a worse design than one that asks once and gets an
    /// answer. Every call has a bounded timeout, so a dead master cannot hang a
    /// launcher or the runtime.
    /// </summary>
    public sealed class MasterClient : IDisposable
    {
        private readonly string host;
        private readonly int port;
        private readonly int timeoutMs;
        private bool disposed;

        public MasterClient(string host, int port)
            : this(host, port, 4000)
        {
        }

        public MasterClient(string host, int port, int timeoutMs)
        {
            if (string.IsNullOrEmpty(host)) throw new ArgumentNullException("host");

            this.host = host;
            this.port = port <= 0 ? MasterProtocol.DefaultPort : port;
            this.timeoutMs = timeoutMs < 500 ? 500 : timeoutMs;
        }

        public string Host
        {
            get { return host; }
        }

        public int Port
        {
            get { return port; }
        }

        // ------------------------------------------------------------- sessions

        public MasterSessionInfo[] List()
        {
            var body = Call(MasterProtocol.OpList, new byte[0], MasterProtocol.StatusOk);
            var count = body.Length / MasterProtocol.InfoSize;
            var result = new MasterSessionInfo[count];

            for (var i = 0; i < count; i++)
            {
                result[i] = MasterSessionInfo.Decode(body, i * MasterProtocol.InfoSize);
            }

            return result;
        }

        /// <summary>Registers a session and returns the 48-byte lease that owns it.</summary>
        public byte[] Register(MasterSessionInfo info)
        {
            var body = new byte[MasterProtocol.InfoSize];
            info.Encode(body, 0);
            var reply = Call(MasterProtocol.OpRegister, body, MasterProtocol.StatusOk);

            if (reply.Length != MasterProtocol.AuthSize)
            {
                throw new InvalidDataException("Master returned a " + reply.Length + "-byte lease.");
            }

            return reply;
        }

        public void Heartbeat(byte[] lease)
        {
            Call(MasterProtocol.OpHeartbeat, LeaseBody(lease), MasterProtocol.StatusOk);
        }

        public void Remove(byte[] lease)
        {
            Call(MasterProtocol.OpRemove, LeaseBody(lease), MasterProtocol.StatusOk);
        }

        public void RelayHost(byte[] lease)
        {
            Call(MasterProtocol.OpRelayHost, LeaseBody(lease), MasterProtocol.StatusOk);
        }

        /// <summary>Asks the directory where the relay route for a session is.</summary>
        public RelayRoute RelayJoin(byte[] sessionId)
        {
            if (sessionId == null || sessionId.Length != 16) throw new ArgumentException("Session id must be 16 bytes.", "sessionId");

            var reply = Call(MasterProtocol.OpRelayJoin, sessionId, MasterProtocol.StatusOk);
            return DecodeRoute(reply);
        }

        // ------------------------------------------------------------- manifests

        public void PublishManifest(byte[] lease, byte[] manifest)
        {
            if (manifest == null) throw new ArgumentNullException("manifest");
            if (manifest.Length > MasterProtocol.MaxManifestSize) throw new ArgumentException("Manifest is too large.", "manifest");

            var body = new byte[MasterProtocol.AuthSize + manifest.Length];
            Array.Copy(LeaseBody(lease), body, MasterProtocol.AuthSize);
            Array.Copy(manifest, 0, body, MasterProtocol.AuthSize, manifest.Length);
            Call(MasterProtocol.OpManifest, body, MasterProtocol.StatusOk);
        }

        public byte[] FetchManifest(byte[] sessionId)
        {
            if (sessionId == null || sessionId.Length != 16) throw new ArgumentException("Session id must be 16 bytes.", "sessionId");
            return Call(MasterProtocol.OpManifest, sessionId, MasterProtocol.StatusOk);
        }

        // ------------------------------------------------------------- projects

        public MasterProjectInfo[] ProjectList()
        {
            var body = Call(MasterProtocol.OpProjectList, new byte[0], MasterProtocol.StatusOk);
            var count = body.Length / MasterProtocol.ProjectRowSize;
            var result = new MasterProjectInfo[count];

            for (var i = 0; i < count; i++)
            {
                result[i] = MasterProjectInfo.Decode(body, i * MasterProtocol.ProjectRowSize);
            }

            return result;
        }

        /// <summary>Publishes (or updates) a project record and returns the stored row.</summary>
        public MasterProjectInfo ProjectPublish(byte[] lease, MasterProjectInfo info)
        {
            var body = new byte[MasterProtocol.AuthSize + MasterProtocol.ProjectRowSize];
            Array.Copy(LeaseBody(lease), body, MasterProtocol.AuthSize);
            info.Encode(body, MasterProtocol.AuthSize);

            var reply = Call(MasterProtocol.OpProjectPublish, body, MasterProtocol.StatusOk);
            if (reply.Length != MasterProtocol.ProjectRowSize) return info;
            return MasterProjectInfo.Decode(reply, 0);
        }

        public MasterProjectInfo ProjectFetch(byte[] projectId)
        {
            if (projectId == null || projectId.Length != 16) throw new ArgumentException("Project id must be 16 bytes.", "projectId");
            var reply = Call(MasterProtocol.OpProjectFetch, projectId, MasterProtocol.StatusOk);
            return MasterProjectInfo.Decode(reply, 0);
        }

        public void ProjectRemove(byte[] lease)
        {
            Call(MasterProtocol.OpProjectRemove, LeaseBody(lease), MasterProtocol.StatusOk);
        }

        /// <summary>Counts a download; the client calls this after fetching a project.</summary>
        public void ProjectTouch(byte[] projectId)
        {
            if (projectId == null || projectId.Length != 16) throw new ArgumentException("Project id must be 16 bytes.", "projectId");
            Call(MasterProtocol.OpProjectTouch, projectId, MasterProtocol.StatusOk);
        }

        // ------------------------------------------------------------- transport

        private static byte[] LeaseBody(byte[] lease)
        {
            if (lease == null || lease.Length != MasterProtocol.AuthSize)
            {
                throw new ArgumentException("Lease must be " + MasterProtocol.AuthSize + " bytes.", "lease");
            }

            return lease;
        }

        private static RelayRoute DecodeRoute(byte[] body)
        {
            if (body.Length < 6) throw new InvalidDataException("Relay reply is too short.");

            var route = new RelayRoute();
            route.Address = new[] { body[0], body[1], body[2], body[3] };
            route.Port = (ushort)MasterProtocol.Get16(body, 4);
            return route;
        }

        /// <summary>
        /// One request, one response. Any status other than the expected one
        /// throws: callers should not have to check a status code they cannot act
        /// on differently.
        /// </summary>
        private byte[] Call(ushort operation, byte[] request, ushort expectedStatus)
        {
            if (disposed) throw new ObjectDisposedException("MasterClient");

            var header = MasterProtocol.Header(operation, request.Length, 0);
            byte[] responseHeader;

            using (var client = new TcpClient())
            {
                client.NoDelay = true;

                var connect = client.BeginConnect(host, port, null, null);
                if (!connect.AsyncWaitHandle.WaitOne(timeoutMs))
                {
                    throw new IOException("Master " + host + ":" + port + " did not accept the connection in time.");
                }

                client.EndConnect(connect);

                using (var stream = client.GetStream())
                {
                    stream.WriteTimeout = timeoutMs;
                    stream.ReadTimeout = timeoutMs;

                    stream.Write(header, 0, header.Length);
                    if (request.Length > 0) stream.Write(request, 0, request.Length);
                    stream.Flush();

                    responseHeader = ReadExact(stream, MasterProtocol.HeaderSize);

                    var error = MasterProtocol.HeaderError(responseHeader);
                    if (error != null) throw new InvalidDataException("Master replied with " + error + ".");

                    var length = (int)MasterProtocol.Get32(responseHeader, 8);
                    if (length < 0 || length > MasterProtocol.MaxBodySize)
                    {
                        throw new InvalidDataException("Master replied with an oversized body (" + length + ").");
                    }

                    var status = (ushort)MasterProtocol.Get16(responseHeader, 12);
                    var body = length == 0 ? new byte[0] : ReadExact(stream, length);

                    if (status != expectedStatus)
                    {
                        throw new MasterException(status, "Master refused the request: " + Describe(status) + ".");
                    }

                    return body;
                }
            }
        }

        private static string Describe(ushort status)
        {
            switch (status)
            {
                case MasterProtocol.StatusBadRequest: return "bad request";
                case MasterProtocol.StatusUnauthorized: return "not the owner of this entry";
                case MasterProtocol.StatusConflict: return "name or id already taken";
                case MasterProtocol.StatusCapacity: return "directory is full";
                case MasterProtocol.StatusRedirect: return "look elsewhere";
                case MasterProtocol.StatusNotFound: return "no such entry";
                default: return "status " + status;
            }
        }

        private static byte[] ReadExact(Stream stream, int count)
        {
            var buffer = new byte[count];
            var read = 0;

            while (read < count)
            {
                var chunk = stream.Read(buffer, read, count - read);
                if (chunk <= 0) throw new EndOfStreamException("Master closed the connection mid-reply.");
                read += chunk;
            }

            return buffer;
        }

        public void Dispose()
        {
            disposed = true;
        }
    }

    /// <summary>A non-success status from the directory.</summary>
    public sealed class MasterException : Exception
    {
        public readonly ushort Status;

        public MasterException(ushort status, string message)
            : base(message)
        {
            Status = status;
        }
    }
}
