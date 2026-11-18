//
// DPT.Engine - the relay.
// Copyright (C) 2026 DartPower Team. GPLv2.
//

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

using DPT.Engine.Shared;

namespace DPT.Engine.Master
{
    /// <summary>
    /// A TCP relay for hosts that cannot accept incoming connections.
    ///
    /// The problem it solves is the common one: a player behind a home router, a
    /// CGNAT, or a corporate firewall can make outbound connections but nobody can
    /// reach them. The relay is a public meeting point - both ends connect out to
    /// it, and it copies bytes between them. Neither end needs a port forward.
    ///
    /// The protocol is deliberately minimal. A client opens a connection and sends
    /// a 24-byte hello: the 16-byte session id, a role byte (host or guest), and
    /// padding. The relay matches the two ends by session id and then becomes a
    /// dumb pipe. It never parses the game's traffic, which is what keeps the relay
    /// small and the game free to change its own protocol.
    ///
    /// A session is paired once and torn down when either side closes. Re-pairing
    /// after a drop is the game's problem, not the relay's: a relay that tried to
    /// be clever about reconnects would have to understand the game.
    /// </summary>
    internal sealed class RelayService : IDisposable
    {
        private const int HelloSize = 24;
        private const int BufferSize = 8192;
        private const int HelloTimeoutMs = 10000;
        private const int IdleTimeoutMs = 300000;

        private const byte RoleHost = 1;
        private const byte RoleGuest = 2;

        private readonly object sync = new object();
        private readonly Dictionary<string, PendingHost> waiting = new Dictionary<string, PendingHost>();

        private readonly MasterConfig config;

        private TcpListener listener;
        private Thread acceptThread;
        private volatile bool running;
        private bool disposed;

        private int pairedSessions;
        private long bytesRelayed;

        internal RelayService(MasterConfig config)
        {
            if (config == null) throw new ArgumentNullException("config");
            this.config = config;
        }

        internal int Port { get; private set; }
        internal int PairedSessions { get { lock (sync) { return pairedSessions; } } }
        internal long BytesRelayed { get { lock (sync) { return bytesRelayed; } } }

        // ---------------------------------------------------------------- lifecycle

        internal bool Start()
        {
            if (config.RelayPort == 0) return true; // deliberately disabled

            try
            {
                var address = string.IsNullOrEmpty(config.BindAddress)
                    ? IPAddress.Any
                    : IPAddress.Parse(config.BindAddress);

                listener = new TcpListener(address, config.RelayPort);
                listener.Start(32);

                Port = ((IPEndPoint)listener.LocalEndpoint).Port;
                running = true;

                acceptThread = new Thread(AcceptLoop);
                acceptThread.IsBackground = true;
                acceptThread.Name = "DPT.Engine.Relay.Accept";
                acceptThread.Start();

                return true;
            }
            catch (SocketException error)
            {
                MasterLog.Warn("Relay could not listen on port " + config.RelayPort + ": " + error.Message);
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

            lock (sync)
            {
                foreach (var host in waiting.Values)
                {
                    Close(host.Client);
                }

                waiting.Clear();
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
                    if (!running) return;
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (Exception error)
                {
                    MasterLog.Error("Relay accept failed: " + error.Message);
                    Thread.Sleep(50);
                    continue;
                }

                var worker = new Thread(HandleClient);
                worker.IsBackground = true;
                worker.Name = "DPT.Engine.Relay.Client";
                worker.Start(client);
            }
        }

        // ---------------------------------------------------------------- pairing

        private void HandleClient(object state)
        {
            var client = state as TcpClient;

            if (client == null) return;

            try
            {
                client.NoDelay = true;
                client.ReceiveTimeout = HelloTimeoutMs;

                var hello = new byte[HelloSize];

                if (!ReadExact(client, hello, hello.Length))
                {
                    Close(client);
                    return;
                }

                var sessionId = new byte[16];
                Array.Copy(hello, sessionId, 16);

                var role = hello[16];
                var key = MasterProtocol.IdToHex(sessionId);

                if (role == RoleHost)
                {
                    WaitForGuest(key, client);
                }
                else if (role == RoleGuest)
                {
                    JoinHost(key, client);
                }
                else
                {
                    MasterLog.Warn("Relay: unknown role " + role);
                    Close(client);
                }
            }
            catch (Exception error)
            {
                if (!(error is System.IO.IOException) && !(error is SocketException))
                {
                    MasterLog.Error("Relay client error: " + error.Message);
                }

                Close(client);
            }
        }

        private void WaitForGuest(string key, TcpClient host)
        {
            PendingHost previous;

            lock (sync)
            {
                if (waiting.TryGetValue(key, out previous))
                {
                    // A host that reconnects replaces its own stale entry rather
                    // than being refused: a dropped connection is normal.
                    waiting.Remove(key);
                    Close(previous.Client);
                }

                var pending = new PendingHost();
                pending.Client = host;
                pending.Since = Environment.TickCount;

                waiting[key] = pending;
            }

            MasterLog.Verbose("Relay: host waiting for " + key.Substring(0, 8) + "...");

            // The host stays parked here until a guest arrives. The wait is bounded:
            // a host that gave up long ago must not hold a socket forever.
            var deadline = Environment.TickCount + IdleTimeoutMs;

            while (running && Environment.TickCount < deadline)
            {
                lock (sync)
                {
                    PendingHost pending;

                    if (!waiting.TryGetValue(key, out pending) || pending.Client != host)
                    {
                        // A guest paired us; the pairing thread owns the socket now.
                        return;
                    }
                }

                Thread.Sleep(100);
            }

            lock (sync)
            {
                PendingHost pending;

                if (waiting.TryGetValue(key, out pending) && pending.Client == host)
                {
                    waiting.Remove(key);
                }
            }

            Close(host);
        }

        private void JoinHost(string key, TcpClient guest)
        {
            TcpClient host = null;

            lock (sync)
            {
                PendingHost pending;

                if (waiting.TryGetValue(key, out pending))
                {
                    host = pending.Client;
                    waiting.Remove(key);
                    pairedSessions++;
                }
            }

            if (host == null)
            {
                MasterLog.Verbose("Relay: no host waiting for " + key.Substring(0, 8));
                Close(guest);
                return;
            }

            MasterLog.Info("Relay: paired " + key.Substring(0, 8) + "...");

            var pump = new Thread(delegate() { Pump(host, guest); });
            pump.IsBackground = true;
            pump.Name = "DPT.Engine.Relay.Pipe";
            pump.Start();
        }

        // ---------------------------------------------------------------- piping

        /// <summary>
        /// Copies bytes in both directions until either side closes.
        ///
        /// Two threads rather than one select loop: blocking sockets and a thread
        /// per direction is the simplest correct thing on .NET Framework 4.0, and a
        /// relay session is two threads for a few minutes at most.
        /// </summary>
        private void Pump(TcpClient host, TcpClient guest)
        {
            host.ReceiveTimeout = IdleTimeoutMs;
            guest.ReceiveTimeout = IdleTimeoutMs;
            host.SendTimeout = IdleTimeoutMs;
            guest.SendTimeout = IdleTimeoutMs;

            var forward = new Thread(delegate() { Copy(host, guest); });
            forward.IsBackground = true;
            forward.Name = "DPT.Engine.Relay.HostToGuest";
            forward.Start();

            Copy(guest, host);

            try { forward.Join(5000); }
            catch { }

            Close(host);
            Close(guest);

            MasterLog.Info("Relay: session closed, " + BytesRelayed + " byte(s) relayed in total");
        }

        private void Copy(TcpClient from, TcpClient to)
        {
            var buffer = new byte[BufferSize];

            try
            {
                var input = from.GetStream();
                var output = to.GetStream();

                while (running)
                {
                    var read = input.Read(buffer, 0, buffer.Length);

                    if (read <= 0) return;

                    output.Write(buffer, 0, read);
                    output.Flush();

                    lock (sync)
                    {
                        bytesRelayed += read;
                    }
                }
            }
            catch (Exception)
            {
                // Either side closing, or a timeout, ends the pipe. Both are normal.
            }
            finally
            {
                Close(to);
            }
        }

        // ---------------------------------------------------------------- helpers

        private static bool ReadExact(TcpClient client, byte[] buffer, int count)
        {
            var stream = client.GetStream();
            var read = 0;

            while (read < count)
            {
                var chunk = stream.Read(buffer, read, count - read);

                if (chunk <= 0) return false;

                read += chunk;
            }

            return true;
        }

        private static void Close(TcpClient client)
        {
            if (client == null) return;

            try { client.Close(); }
            catch { }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            Stop();
        }

        private sealed class PendingHost
        {
            internal TcpClient Client;
            internal int Since;
        }
    }
}
