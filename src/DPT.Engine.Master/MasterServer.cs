//
// DPT.Engine - the master's TCP directory.
// Copyright (C) 2026 DartPower Team. GPLv2.
//

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

using DPT.Engine.Shared;

namespace DPT.Engine.Master
{
    /// <summary>
    /// Serves the directory protocol.
    ///
    /// One bounded request per connection: read a 16-byte header, validate it,
    /// read the body it announces, answer, close. There is no long-lived state on a
    /// connection, which means a stalled or hostile client can only ever occupy one
    /// thread for the duration of one timeout.
    ///
    /// The accept loop is deliberately not async: this has to compile for .NET
    /// Framework 4.0, and a thread per connection with a hard read timeout is both
    /// simpler to reason about and impossible to starve the thread pool with.
    /// </summary>
    internal sealed class MasterServer : IDisposable
    {
        /// <summary>How long a client gets to send a whole request.</summary>
        private const int ReadTimeoutMs = 10000;

        /// <summary>How long a client gets to accept the reply.</summary>
        private const int WriteTimeoutMs = 10000;

        private readonly MasterConfig config;
        private readonly SessionDirectory sessions;
        private readonly ProjectCatalogue projects;

        private TcpListener listener;
        private Thread acceptThread;
        private volatile bool running;
        private bool disposed;

        internal MasterServer(MasterConfig config, SessionDirectory sessions, ProjectCatalogue projects)
        {
            if (config == null) throw new ArgumentNullException("config");
            if (sessions == null) throw new ArgumentNullException("sessions");
            if (projects == null) throw new ArgumentNullException("projects");

            this.config = config;
            this.sessions = sessions;
            this.projects = projects;
        }

        internal int Port { get; private set; }

        // ---------------------------------------------------------------- lifecycle

        internal bool Start()
        {
            try
            {
                var address = string.IsNullOrEmpty(config.BindAddress)
                    ? IPAddress.Any
                    : IPAddress.Parse(config.BindAddress);

                listener = new TcpListener(address, config.Port);
                listener.Start(64);

                // Port 0 in the configuration means "any free port", which the tests
                // use to avoid colliding with a running master.
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;

                sessions.Start();
                running = true;

                acceptThread = new Thread(AcceptLoop);
                acceptThread.IsBackground = true;
                acceptThread.Name = "DPT.Engine.Master.Accept";
                acceptThread.Start();

                return true;
            }
            catch (SocketException error)
            {
                Console.Error.WriteLine("Could not listen on port " + config.Port + ": " + error.Message);

                if (error.SocketErrorCode == SocketError.AddressAlreadyInUse)
                {
                    Console.Error.WriteLine("Another process is using that port. Set a different one in master.ini.");
                }

                return false;
            }
        }

        internal void Stop()
        {
            running = false;

            if (listener != null)
            {
                try { listener.Stop(); }
                catch { }
                listener = null;
            }
        }

        private void AcceptLoop()
        {
            while (running)
            {
                TcpClient client = null;

                try
                {
                    client = listener.AcceptTcpClient();
                }
                catch (SocketException)
                {
                    // Stopping the listener makes Accept throw; that is the normal
                    // shutdown path, not an error.
                    if (!running) return;
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (Exception error)
                {
                    Console.Error.WriteLine("Accept failed: " + error.Message);
                    Thread.Sleep(50);
                    continue;
                }

                // Each connection gets its own thread. A connection is short by
                // design, so the thread count is bounded by the accept rate, and a
                // hard timeout means a silent client cannot hold one forever.
                var worker = new Thread(HandleClient);
                worker.IsBackground = true;
                worker.Name = "DPT.Engine.Master.Client";
                worker.Start(client);
            }
        }

        // ---------------------------------------------------------------- request handling

        private void HandleClient(object state)
        {
            var client = state as TcpClient;

            if (client == null) return;

            try
            {
                client.NoDelay = true;
                client.ReceiveTimeout = ReadTimeoutMs;
                client.SendTimeout = WriteTimeoutMs;

                using (var stream = client.GetStream())
                {
                    var header = new byte[MasterProtocol.HeaderSize];

                    if (!ReadExact(stream, header, header.Length)) return;

                    var error = MasterProtocol.HeaderError(header);

                    if (error != null)
                    {
                        // The header names the operation and the length, so a bad
                        // header cannot be answered in kind - the reply is a bare
                        // bad-request with an empty body.
                        Reply(stream, MasterProtocol.OpList, MasterProtocol.StatusBadRequest, new byte[0]);
                        Log("Rejected header: " + error);
                        return;
                    }

                    var operation = (ushort)MasterProtocol.Get16(header, 6);
                    var length = (int)MasterProtocol.Get32(header, 8);

                    var body = new byte[length];

                    if (length > 0 && !ReadExact(stream, body, length)) return;

                    var address = client.Client.RemoteEndPoint as IPEndPoint;
                    var from = address == null ? IPAddress.None : address.Address;

                    Dispatch(stream, operation, body, from);
                }
            }
            catch (Exception error)
            {
                // A client that disconnects mid-request is normal; anything else is
                // worth a line but never worth taking the directory down.
                if (!(error is IOException) && !(error is SocketException))
                {
                    MasterLog.Exception("Client error", error);
                }
            }
            finally
            {
                try { client.Close(); }
                catch { }
            }
        }

        private void Dispatch(Stream stream, ushort operation, byte[] body, IPAddress from)
        {
            switch (operation)
            {
                case MasterProtocol.OpList:
                    HandleList(stream);
                    break;

                case MasterProtocol.OpRegister:
                    HandleRegister(stream, body, from);
                    break;

                case MasterProtocol.OpHeartbeat:
                    HandleSimple(stream, operation, sessions.Heartbeat(body), "heartbeat");
                    break;

                case MasterProtocol.OpRemove:
                    HandleSimple(stream, operation, sessions.Remove(body), "remove");
                    break;

                case MasterProtocol.OpRelayHost:
                    HandleRelayHost(stream, body, from);
                    break;

                case MasterProtocol.OpRelayJoin:
                    HandleRelayJoin(stream, body, from);
                    break;

                case MasterProtocol.OpManifest:
                    HandleManifest(stream, body);
                    break;

                case MasterProtocol.OpProjectList:
                    HandleProjectList(stream);
                    break;

                case MasterProtocol.OpProjectPublish:
                    HandleProjectPublish(stream, body, from);
                    break;

                case MasterProtocol.OpProjectFetch:
                    HandleProjectFetch(stream, body);
                    break;

                case MasterProtocol.OpProjectRemove:
                    HandleSimple(stream, operation, projects.Remove(body), "project remove");
                    break;

                case MasterProtocol.OpProjectTouch:
                    HandleSimple(stream, operation, projects.Touch(body), "project touch");
                    break;

                default:
                    Reply(stream, operation, MasterProtocol.StatusBadRequest, new byte[0]);
                    break;
            }
        }

        private void HandleList(Stream stream)
        {
            var list = sessions.List();
            var body = new byte[list.Length * MasterProtocol.InfoSize];

            for (var i = 0; i < list.Length; i++)
            {
                list[i].Encode(body, i * MasterProtocol.InfoSize);
            }

            Reply(stream, MasterProtocol.OpList, MasterProtocol.StatusOk, body);
        }

        private void HandleRegister(Stream stream, byte[] body, IPAddress from)
        {
            var info = MasterSessionInfo.Decode(body, 0);

            if (!MasterProtocol.IsPrintableAscii(info.Name))
            {
                Reply(stream, MasterProtocol.OpRegister, MasterProtocol.StatusBadRequest, new byte[0]);
                return;
            }

            string reason;
            var lease = sessions.Register(info, from, out reason);

            if (lease == null)
            {
                Log("Registration refused from " + SessionDirectory.AddressText(from) + ": " + reason);

                // A full directory and a duplicate name are different problems for
                // the client: one is "try later", the other is "pick another name".
                var status = reason != null && reason.IndexOf("full", StringComparison.Ordinal) >= 0
                    ? MasterProtocol.StatusCapacity
                    : MasterProtocol.StatusConflict;

                Reply(stream, MasterProtocol.OpRegister, status, new byte[0]);
                return;
            }

            Log("Registered '" + info.Name + "' from " + SessionDirectory.AddressText(from) +
                " port " + info.Port + " (" + sessions.Count + " session(s))");

            Reply(stream, MasterProtocol.OpRegister, MasterProtocol.StatusOk, lease);
        }

        private void HandleSimple(Stream stream, ushort operation, bool ok, string what)
        {
            Reply(stream, operation, ok ? MasterProtocol.StatusOk : MasterProtocol.StatusUnauthorized,
                new byte[0]);

            if (!ok) Log("Refused " + what + ": unknown or expired lease");
        }

        private void HandleRelayHost(Stream stream, byte[] body, IPAddress from)
        {
            if (!sessions.RequestRelay(body))
            {
                Reply(stream, MasterProtocol.OpRelayHost, MasterProtocol.StatusUnauthorized, new byte[0]);
                return;
            }

            Log("Relay requested by " + SessionDirectory.AddressText(from));
            Reply(stream, MasterProtocol.OpRelayHost, MasterProtocol.StatusOk, new byte[0]);
        }

        private void HandleRelayJoin(Stream stream, byte[] body, IPAddress from)
        {
            var entry = sessions.Find(body);

            if (entry == null)
            {
                Reply(stream, MasterProtocol.OpRelayJoin, MasterProtocol.StatusNotFound, new byte[0]);
                return;
            }

            if (config.RelayPort == 0)
            {
                // No relay configured: the client is told to connect to the host
                // directly. That is a normal answer, not an error - most sessions
                // are directly reachable.
                var direct = new byte[6];
                Array.Copy(entry.Info.Address, direct, 4);
                MasterProtocol.Put16(direct, 4, entry.Info.Port);

                Reply(stream, MasterProtocol.OpRelayJoin, MasterProtocol.StatusOk, direct);
                return;
            }

            // A relay route: the relay's own address and port. The relay matches the
            // two ends by session id, which the join request already carried.
            var route = new byte[6];
            var address = SessionDirectory.AddressBytes(from);

            // The client is told to connect to the master's own public address on
            // the relay port, which is where the relay listens.
            if (!string.IsNullOrEmpty(config.PublicHost))
            {
                var resolved = TryResolve(config.PublicHost);

                if (resolved != null) address = resolved;
            }
            else
            {
                address = SessionDirectory.AddressBytes(LocalAddressFor(from));
            }

            Array.Copy(address, route, 4);
            MasterProtocol.Put16(route, 4, config.RelayPort);

            Reply(stream, MasterProtocol.OpRelayJoin, MasterProtocol.StatusOk, route);
        }

        private void HandleManifest(Stream stream, byte[] body)
        {
            if (body.Length == 16)
            {
                var manifest = sessions.FetchManifest(body);

                if (manifest == null)
                {
                    Reply(stream, MasterProtocol.OpManifest, MasterProtocol.StatusNotFound, new byte[0]);
                    return;
                }

                Reply(stream, MasterProtocol.OpManifest, MasterProtocol.StatusOk, manifest);
                return;
            }

            var lease = new byte[MasterProtocol.AuthSize];
            Array.Copy(body, lease, MasterProtocol.AuthSize);

            var payload = new byte[body.Length - MasterProtocol.AuthSize];
            Array.Copy(body, MasterProtocol.AuthSize, payload, 0, payload.Length);

            var ok = sessions.PublishManifest(lease, payload);

            Reply(stream, MasterProtocol.OpManifest,
                ok ? MasterProtocol.StatusOk : MasterProtocol.StatusUnauthorized, new byte[0]);
        }

        private void HandleProjectList(Stream stream)
        {
            var list = projects.List();
            var body = new byte[list.Length * MasterProtocol.ProjectRowSize];

            for (var i = 0; i < list.Length; i++)
            {
                list[i].Encode(body, i * MasterProtocol.ProjectRowSize);
            }

            Reply(stream, MasterProtocol.OpProjectList, MasterProtocol.StatusOk, body);
        }

        private void HandleProjectPublish(Stream stream, byte[] body, IPAddress from)
        {
            var lease = new byte[MasterProtocol.AuthSize];
            Array.Copy(body, lease, MasterProtocol.AuthSize);

            var info = MasterProjectInfo.Decode(body, MasterProtocol.AuthSize);

            var publisher = new IPAddressLike();
            publisher.Bytes = SessionDirectory.AddressBytes(from);

            byte[] newLease;
            string reason;

            var stored = projects.Publish(info, lease, publisher, out newLease, out reason);

            if (stored == null)
            {
                Log("Project publish refused: " + reason);
                Reply(stream, MasterProtocol.OpProjectPublish, MasterProtocol.StatusConflict, new byte[0]);
                return;
            }

            // The lease is returned in the body so a first-time publisher learns it;
            // an existing publisher already has one and gets the same value back.
            var reply = new byte[MasterProtocol.AuthSize + MasterProtocol.ProjectRowSize];
            Array.Copy(newLease, reply, MasterProtocol.AuthSize);
            stored.Encode(reply, MasterProtocol.AuthSize);

            Log("Published '" + stored.Name + "' (" + stored.KindText + ", " + stored.Bytes + " bytes)");

            Reply(stream, MasterProtocol.OpProjectPublish, MasterProtocol.StatusOk, reply);
        }

        private void HandleProjectFetch(Stream stream, byte[] body)
        {
            var info = projects.Find(body);

            if (info == null)
            {
                Reply(stream, MasterProtocol.OpProjectFetch, MasterProtocol.StatusNotFound, new byte[0]);
                return;
            }

            var reply = new byte[MasterProtocol.ProjectRowSize];
            info.Encode(reply, 0);

            Reply(stream, MasterProtocol.OpProjectFetch, MasterProtocol.StatusOk, reply);
        }

        // ---------------------------------------------------------------- transport

        private static bool ReadExact(Stream stream, byte[] buffer, int count)
        {
            var read = 0;

            while (read < count)
            {
                var chunk = stream.Read(buffer, read, count - read);

                if (chunk <= 0) return false;

                read += chunk;
            }

            return true;
        }

        private static void Reply(Stream stream, ushort operation, ushort status, byte[] body)
        {
            var header = MasterProtocol.Header(operation, body.Length, status);

            stream.Write(header, 0, header.Length);

            if (body.Length > 0)
            {
                stream.Write(body, 0, body.Length);
            }

            stream.Flush();
        }

        private static IPAddress LocalAddressFor(IPAddress remote)
        {
            // Ask the routing table which local address would reach this client.
            // A UDP socket is enough: no packet is sent, but the OS resolves the
            // route and reports the source address it would use.
            try
            {
                using (var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    probe.Connect(remote, 9);
                    return ((IPEndPoint)probe.LocalEndPoint).Address;
                }
            }
            catch
            {
                return IPAddress.Loopback;
            }
        }

        private static byte[] TryResolve(string host)
        {
            try
            {
                var addresses = Dns.GetHostAddresses(host);

                for (var i = 0; i < addresses.Length; i++)
                {
                    if (addresses[i].AddressFamily == AddressFamily.InterNetwork)
                    {
                        return addresses[i].GetAddressBytes();
                    }
                }
            }
            catch
            {
                // A host name that does not resolve falls back to the local address.
            }

            return null;
        }

        private static void Log(string message)
        {
            MasterLog.Info(message);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            Stop();
        }
    }
}
