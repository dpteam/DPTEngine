//
// DPT.Engine - master self-test.
// Copyright (C) 2026 DartPower Team. GPLv2.
//

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

using DPT.Engine.Shared;

namespace DPT.Engine.Master
{
    /// <summary>
    /// Exercises the directory over a real socket.
    ///
    /// A server that only compiles is a server nobody has tried. This starts the
    /// real listener on a free port, talks to it with the real protocol code, and
    /// checks the answers - including the paths that are supposed to fail.
    ///
    /// Runs on loopback only and shuts everything down, so it is safe on a build
    /// machine.
    /// </summary>
    internal static class MasterSelfTest
    {
        private static int failures;

        internal static int Run()
        {
            Console.WriteLine("DPT.Engine master self-test");
            Console.WriteLine();

            Run("protocol framing", TestFraming);
            Run("register and list", TestRegisterAndList);
            Run("duplicate name refused", TestDuplicateName);
            Run("heartbeat and remove", TestHeartbeatAndRemove);
            Run("lease ownership", TestLeaseOwnership);
            Run("session expiry", TestExpiry);
            Run("project publish and fetch", TestProjectPublish);
            Run("manifest round trip", TestManifest);

            Console.WriteLine();

            if (failures == 0)
            {
                Console.WriteLine("master self-test passed");
                return 0;
            }

            Console.WriteLine("master self-test FAILED: " + failures + " check(s)");
            return 1;
        }

        private static void Run(string name, Action test)
        {
            try
            {
                test();
                Console.WriteLine("  PASS  " + name);
            }
            catch (Exception error)
            {
                failures++;
                Console.WriteLine("  FAIL  " + name);
                Console.WriteLine("        " + error.GetType().Name + ": " + error.Message);
            }
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        // ---------------------------------------------------------------- harness

        /// <summary>A directory on a free loopback port, torn down at the end.</summary>
        private sealed class Harness : IDisposable
        {
            internal readonly MasterConfig Config;
            internal readonly SessionDirectory Sessions;
            internal readonly ProjectCatalogue Projects;
            internal readonly MasterServer Server;
            internal readonly int Port;

            internal Harness()
            {
                Config = new MasterConfig();
                Config.Port = 0;              // any free port
                Config.RelayPort = 0;         // relay is tested separately
                Config.BindAddress = "127.0.0.1";
                Config.SessionTimeoutSeconds = 10;
                Config.MaxSessions = 8;
                Config.MaxSessionsPerAddress = 4;
                Config.MaxProjects = 8;

                // A private state file per harness, so a test run cannot read or
                // overwrite a real directory's catalogue.
                Config.StateFile = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    "dptengine-master-test-" + Guid.NewGuid().ToString("N") + ".state");

                Sessions = new SessionDirectory(Config);
                Projects = new ProjectCatalogue(Config);
                Server = new MasterServer(Config, Sessions, Projects);

                if (!Server.Start()) throw new InvalidOperationException("the test directory did not start");

                Port = Server.Port;
            }

            internal MasterClient Client()
            {
                return new MasterClient("127.0.0.1", Port, 4000);
            }

            internal MasterSessionInfo NewSession(string name)
            {
                var info = new MasterSessionInfo();
                info.Id = MasterProtocol.NewId();
                info.Address = new byte[] { 127, 0, 0, 1 };
                info.Port = 25000;
                info.MaxPlayers = 8;
                info.Name = name;
                info.Map = "tunnel";
                return info;
            }

            public void Dispose()
            {
                Server.Dispose();

                try
                {
                    if (System.IO.File.Exists(Config.StateFile)) System.IO.File.Delete(Config.StateFile);
                }
                catch
                {
                }
            }
        }

        // ---------------------------------------------------------------- tests

        private static void TestFraming()
        {
            // A malformed header must be refused without reading a body, so a
            // hostile client cannot make the server allocate.
            using (var harness = new Harness())
            using (var client = new TcpClient())
            {
                client.Connect("127.0.0.1", harness.Port);

                var stream = client.GetStream();

                var bad = MasterProtocol.Header(MasterProtocol.OpRegister, 0, 0); // wrong length
                stream.Write(bad, 0, bad.Length);
                stream.Flush();

                var reply = new byte[MasterProtocol.HeaderSize];
                var read = stream.Read(reply, 0, reply.Length);

                Check(read == MasterProtocol.HeaderSize, "the server answers a bad header");

                var status = (ushort)MasterProtocol.Get16(reply, 12);
                Check(status == MasterProtocol.StatusBadRequest,
                    "a bad header is refused with bad-request, got " + status);

                var length = (int)MasterProtocol.Get32(reply, 8);
                Check(length == 0, "a refused request carries no body");
            }
        }

        private static void TestRegisterAndList()
        {
            using (var harness = new Harness())
            using (var client = harness.Client())
            {
                var before = client.List();
                Check(before.Length == 0, "a fresh directory is empty");

                var info = harness.NewSession("Test Session");
                var lease = client.Register(info);

                Check(lease != null && lease.Length == MasterProtocol.AuthSize,
                    "register returns a lease");

                var after = client.List();
                Check(after.Length == 1, "the session is listed, got " + after.Length);
                Check(after[0].Name == "Test Session", "the name round-trips");
                Check(after[0].Port == 25000, "the port round-trips");
                Check(after[0].Protocol == MasterProtocol.GameProtocol,
                    "the directory stamps its own protocol id");
                Check(after[0].AddressText == "127.0.0.1",
                    "the address comes from the socket, got " + after[0].AddressText);
            }
        }

        private static void TestDuplicateName()
        {
            using (var harness = new Harness())
            using (var client = harness.Client())
            {
                client.Register(harness.NewSession("Same Name"));

                try
                {
                    client.Register(harness.NewSession("Same Name"));
                    throw new InvalidOperationException("a duplicate name was accepted");
                }
                catch (MasterException error)
                {
                    Check(error.Status == MasterProtocol.StatusConflict,
                        "a duplicate name is a conflict, got " + error.Status);
                }

                Check(client.List().Length == 1, "the refused session was not added");
            }
        }

        private static void TestHeartbeatAndRemove()
        {
            using (var harness = new Harness())
            using (var client = harness.Client())
            {
                var lease = client.Register(harness.NewSession("Heartbeat"));

                client.Heartbeat(lease);
                Check(client.List().Length == 1, "the session survives a heartbeat");

                client.Remove(lease);
                Check(client.List().Length == 0, "the session is gone after remove");
            }
        }

        private static void TestLeaseOwnership()
        {
            using (var harness = new Harness())
            using (var client = harness.Client())
            {
                client.Register(harness.NewSession("Owned"));

                // A lease nobody was issued must not be able to touch the entry.
                // It has to be the right size, or the client rejects it before it
                // ever reaches the server and the test proves nothing.
                var forged = SessionDirectory.NewLease();

                try
                {
                    client.Remove(forged);
                    throw new InvalidOperationException("a forged lease removed a session");
                }
                catch (MasterException error)
                {
                    Check(error.Status == MasterProtocol.StatusUnauthorized,
                        "a forged lease is unauthorized, got " + error.Status);
                }

                Check(client.List().Length == 1, "the session is still there");
            }
        }

        private static void TestExpiry()
        {
            using (var harness = new Harness())
            using (var client = harness.Client())
            {
                // A host that crashes never says goodbye; the directory has to
                // expire it or the list fills with games nobody can join.
                harness.Config.SessionTimeoutSeconds = 1;

                client.Register(harness.NewSession("Short Lived"));
                Check(client.List().Length == 1, "the session is listed immediately");

                // Sleep past the timeout with margin: the clock the directory uses
                // has one-second resolution, so 1.4s lands exactly on the boundary.
                Thread.Sleep(2600);

                Check(client.List().Length == 0, "the session expired after its timeout");
            }
        }

        private static void TestProjectPublish()
        {
            using (var harness = new Harness())
            using (var client = harness.Client())
            {
                var lease = client.Register(harness.NewSession("Publisher"));

                var project = new MasterProjectInfo();
                project.Name = "Tunnel Runner";
                project.Author = "tester";
                project.Url = "http://example.invalid/tunnel.zip";
                project.Kind = MasterProjectInfo.KindDemo;
                project.Bytes = 4096;
                project.Flags = MasterProjectInfo.FlagPublished;

                var published = client.ProjectPublish(lease, project);

                Check(published.IdHex.Length == 32, "the directory assigns an id");
                Check(published.Name == "Tunnel Runner", "the name round-trips");
                Check(published.Version == 1, "a first publish is version 1");

                var listed = client.ProjectList();
                Check(listed.Length == 1, "the project is listed");

                var fetched = client.ProjectFetch(published.Id);
                Check(fetched.Url == "http://example.invalid/tunnel.zip", "the url round-trips");

                // A URL the client cannot fetch is not a project.
                var bad = new MasterProjectInfo();
                bad.Name = "Bad";
                bad.Url = "file:///etc/passwd";

                try
                {
                    client.ProjectPublish(lease, bad);
                    throw new InvalidOperationException("a non-http url was accepted");
                }
                catch (MasterException error)
                {
                    Check(error.Status == MasterProtocol.StatusConflict,
                        "a bad url is refused, got " + error.Status);
                }
            }
        }

        private static void TestManifest()
        {
            using (var harness = new Harness())
            using (var client = harness.Client())
            {
                var info = harness.NewSession("Manifest");
                var lease = client.Register(info);

                var manifest = new byte[64];

                for (var i = 0; i < manifest.Length; i++)
                {
                    manifest[i] = (byte)i;
                }

                client.PublishManifest(lease, manifest);

                var listed = client.List();
                Check(listed.Length == 1, "the session is listed");

                var fetched = client.FetchManifest(listed[0].Id);
                Check(fetched.Length == manifest.Length, "the manifest round-trips");

                for (var i = 0; i < manifest.Length; i++)
                {
                    Check(fetched[i] == manifest[i], "manifest byte " + i + " round-trips");
                }

                // A session with no manifest is "not found", not an error.
                var unknown = MasterProtocol.NewId();

                try
                {
                    client.FetchManifest(unknown);
                    throw new InvalidOperationException("a missing manifest was returned");
                }
                catch (MasterException error)
                {
                    Check(error.Status == MasterProtocol.StatusNotFound,
                        "a missing manifest is not-found, got " + error.Status);
                }
            }
        }
    }
}
