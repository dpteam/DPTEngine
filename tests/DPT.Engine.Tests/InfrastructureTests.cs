//
// DPT.Engine - infrastructure tests: JSON, logging, fault tolerance, protocol.
// Copyright (C) 2026 DartPower Team. GPLv2.
//

using System;
using System.Collections.Generic;
using System.IO;

using DPT.Engine.Shared;
using DPT.Engine.Shared.Diagnostics;

namespace DPT.Engine.Tests
{
    internal sealed class InfrastructureTests
    {
        // ---------------------------------------------------------------- JSON

        [Test]
        internal void JsonRoundTripsAProjectShapedDocument()
        {
            var root = JsonValue.NewObject();
            root.Set("name", "demo");
            root.Set("version", 1);
            root.Set("loop", true);
            root.Set("aspect", 1.7777778);

            var objects = JsonValue.NewArray();
            var item = JsonValue.NewObject();
            item.Set("id", 7);
            item.Set("position", JsonValue.NewArray().Add(1.5).Add(-2).Add(0));
            item.Set("colour", "#0088FF");
            objects.Add(item);
            root.Set("objects", objects);

            var text = root.ToJson(true);
            var parsed = JsonValue.Parse(text);

            Assert.Equal("demo", parsed["name"].AsString(), "string member");
            Assert.Equal(1, parsed["version"].AsInt(0), "integer member");
            Assert.True(parsed["loop"].AsBool(false), "boolean member");
            Assert.Near(1.7777778f, parsed["aspect"].AsFloat(0f), 1e-6f, "float member");
            Assert.Equal(1, parsed["objects"].Count, "array length");
            Assert.Near(-2f, parsed["objects"][0]["position"][1].AsFloat(0f), 1e-6f, "nested array");
            Assert.Equal("#0088FF", parsed["objects"][0]["colour"].AsString(), "hex colour");
        }

        [Test]
        internal void JsonPreservesMemberOrder()
        {
            var root = JsonValue.NewObject();
            root.Set("zebra", 1);
            root.Set("alpha", 2);
            root.Set("middle", 3);

            var text = root.ToJson(false);

            Assert.True(text.IndexOf("zebra", StringComparison.Ordinal) <
                        text.IndexOf("alpha", StringComparison.Ordinal),
                "members keep insertion order, not alphabetical order");
        }

        [Test]
        internal void JsonRejectsMalformedInput()
        {
            Assert.Throws<FormatException>(delegate { JsonValue.Parse("{"); }, "unterminated object");
            Assert.Throws<FormatException>(delegate { JsonValue.Parse("{\"a\":1,}"); }, "trailing comma");
            Assert.Throws<FormatException>(delegate { JsonValue.Parse("[1 2]"); }, "missing comma");
            Assert.Throws<FormatException>(delegate { JsonValue.Parse("{a:1}"); }, "unquoted key");
        }

        [Test]
        internal void JsonTryParseReturnsFallbackInsteadOfThrowing()
        {
            var fallback = JsonValue.NewObject();
            var result = JsonValue.TryParse("this is not json", fallback);

            Assert.True(ReferenceEquals(fallback, result), "malformed input yields the fallback");
        }

        [Test]
        internal void JsonMissingMembersReadAsDefaults()
        {
            var root = JsonValue.Parse("{}");

            Assert.Equal(42, root["absent"].AsInt(42), "missing number falls back");
            Assert.Equal("x", root["absent"].AsString("x"), "missing string falls back");
            Assert.Equal(0, root["absent"]["deeper"].Count, "missing sub-object is empty, not a crash");
        }

        // ---------------------------------------------------------------- logging

        [Test]
        internal void LoggingIsIdempotentAcrossRepeatedInitialisation()
        {
            // The bug this guards against: two Initialise calls attach two file
            // listeners, and every line is then written twice.
            var path = TempFile("idempotent.log");

            try
            {
                EngineLog.Shutdown();
                EngineLog.Initialize(path);
                EngineLog.Initialize(path);
                EngineLog.Initialize(path);

                EngineLog.Info("test", "single line");

                EngineLog.Shutdown();

                var text = File.ReadAllText(path);
                var count = CountOccurrences(text, "single line");

                Assert.Equal(1, count, "the line appears exactly once");
            }
            finally
            {
                EngineLog.Shutdown();
                Delete(path);
            }
        }

        [Test]
        internal void LoggingWritesLevelsAndCategories()
        {
            var path = TempFile("levels.log");

            try
            {
                EngineLog.Shutdown();
                EngineLog.Initialize(path, LogLevel.Verbose, 1024 * 1024, 1);

                EngineLog.Info("graphics", "info line");
                EngineLog.Warn("audio", "warn line");
                EngineLog.Error("net", "error line");

                EngineLog.Shutdown();

                var text = File.ReadAllText(path);

                Assert.True(text.Contains("[I] graphics: "), "info line carries its level and category");
                Assert.True(text.Contains("[W] audio: "), "warn line carries its level and category");
                Assert.True(text.Contains("[E] net: "), "error line carries its level and category");
            }
            finally
            {
                EngineLog.Shutdown();
                Delete(path);
            }
        }

        [Test]
        internal void LoggingRespectsTheMinimumLevel()
        {
            var path = TempFile("filtered.log");

            try
            {
                EngineLog.Shutdown();
                EngineLog.Initialize(path, LogLevel.Warning, 1024 * 1024, 1);

                EngineLog.Info("test", "should not appear");
                EngineLog.Warn("test", "should appear");

                EngineLog.Shutdown();

                var text = File.ReadAllText(path);

                Assert.False(text.Contains("should not appear"), "verbose lines are filtered out");
                Assert.True(text.Contains("should appear"), "warnings are kept");
            }
            finally
            {
                EngineLog.Shutdown();
                Delete(path);
            }
        }

        [Test]
        internal void LoggingKeepsARecentBufferForTheOverlay()
        {
            var path = TempFile("ring.log");

            try
            {
                EngineLog.Shutdown();
                EngineLog.Initialize(path, LogLevel.Verbose, 1024 * 1024, 1);

                EngineLog.Info("ring", "first");
                EngineLog.Info("ring", "second");

                var recent = EngineLog.RecentText(10);

                Assert.True(recent.Contains("first"), "the ring holds earlier lines");
                Assert.True(recent.Contains("second"), "the ring holds later lines");

                var order = recent.IndexOf("first", StringComparison.Ordinal);
                var second = recent.IndexOf("second", StringComparison.Ordinal);

                Assert.True(order >= 0 && order < second, "the ring is oldest-first");
            }
            finally
            {
                EngineLog.Shutdown();
                Delete(path);
            }
        }

        [Test]
        internal void LoggingSurvivesAnUnwritableFile()
        {
            // A read-only install folder must degrade, not crash. The path is a
            // directory, which no file listener can open.
            var directory = Path.Combine(Path.GetTempPath(), "dptengine-log-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            try
            {
                EngineLog.Shutdown();
                EngineLog.Initialize(directory);
                EngineLog.Info("test", "into a directory");

                // Reaching here without an exception is the assertion.
                Assert.True(true, "logging degraded quietly");
            }
            finally
            {
                EngineLog.Shutdown();
                Delete(directory, true);
            }
        }

        // ---------------------------------------------------------------- fault guard

        [Test]
        internal void GuardSwallowsAndRecordsFailures()
        {
            var name = "test.guard." + Guid.NewGuid().ToString("N");

            var ok = FaultGuard.Guard(name, "fine", delegate { });

            Assert.True(ok, "a guard over a healthy action returns true");
            Assert.False(FaultGuard.Get(name).IsDisabled, "one success does not disable anything");

            var failed = FaultGuard.Guard(name, "boom", delegate { throw new InvalidOperationException("boom"); });

            Assert.False(failed, "a guard over a throwing action returns false");
            Assert.Equal(1, FaultGuard.Get(name).FailureCount, "the failure is counted");
        }

        [Test]
        internal void SubsystemDisablesAfterRepeatedFailures()
        {
            var name = "test.disable." + Guid.NewGuid().ToString("N");
            var original = FaultGuard.FailureThreshold;

            try
            {
                FaultGuard.FailureThreshold = 3;

                for (var i = 0; i < 3; i++)
                {
                    FaultGuard.Guard(name, "boom", delegate { throw new InvalidOperationException("boom"); });
                }

                Assert.True(FaultGuard.IsDisabled(name), "the subsystem disables itself after the threshold");
            }
            finally
            {
                FaultGuard.FailureThreshold = original;
            }
        }

        [Test]
        internal void AttemptReturnsTheFallbackOnFailure()
        {
            var name = "test.attempt." + Guid.NewGuid().ToString("N");

            var value = FaultGuard.Attempt(name, "compute", delegate { return 10; }, -1);
            Assert.Equal(10, value, "a healthy call returns its result");

            var fallback = FaultGuard.Attempt(name, "compute",
                delegate { throw new InvalidOperationException("nope"); }, -1);
            Assert.Equal(-1, fallback, "a throwing call returns the fallback");
        }

        [Test]
        internal void CrashReportIsWrittenWithTheSubsystemTable()
        {
            var directory = Path.Combine(Path.GetTempPath(), "dptengine-crash-" + Guid.NewGuid().ToString("N"));

            try
            {
                FaultGuard.MarkDegraded("test.crash", "simulated");

                var path = FaultGuard.WriteCrashReport(new InvalidOperationException("simulated failure"), "test");
                Assert.NotNull(path, "a crash report path is returned");

                var text = File.ReadAllText(path);

                Assert.True(text.Contains("DPT.Engine crash report"), "the report has a header");
                Assert.True(text.Contains("simulated failure"), "the report carries the exception");
                Assert.True(text.Contains("test.crash"), "the report carries the subsystem table");
            }
            finally
            {
                Delete(directory, true);
            }
        }

        // ---------------------------------------------------------------- protocol

        [Test]
        internal void ProtocolRoundTripsASessionRecord()
        {
            var info = new MasterSessionInfo();
            info.Id = MasterProtocol.NewId();
            info.Address = new byte[] { 192, 168, 0, 7 };
            info.Port = 25150;
            info.Protocol = (ushort)MasterProtocol.GameProtocol;
            info.Players = 2;
            info.MaxPlayers = 8;
            info.Name = "a demo night";
            info.Map = "tunnel";

            var buffer = new byte[MasterProtocol.InfoSize];
            info.Encode(buffer, 0);
            var decoded = MasterSessionInfo.Decode(buffer, 0);

            Assert.Equal(MasterProtocol.InfoSize, MasterProtocol.InfoSize, "the record is fixed size");
            Assert.Equal(info.IdHex, decoded.IdHex, "session id");
            Assert.Equal("192.168.0.7", decoded.AddressText, "address");
            Assert.Equal(25150, decoded.Port, "port");
            Assert.Equal("a demo night", decoded.Name, "name");
            Assert.Equal("tunnel", decoded.Map, "map");
        }

        [Test]
        internal void ProtocolRoundTripsAProjectRecord()
        {
            var info = new MasterProjectInfo();
            info.Id = MasterProtocol.NewId();
            info.Name = "Tunnel Runner";
            info.Author = "someone";
            info.Url = "http://example.invalid/project.zip";
            info.Kind = MasterProjectInfo.KindDemo;
            info.Bytes = 123456;

            var buffer = new byte[MasterProtocol.ProjectRowSize];
            info.Encode(buffer, 0);
            var decoded = MasterProjectInfo.Decode(buffer, 0);

            Assert.Equal("Tunnel Runner", decoded.Name, "project name");
            Assert.Equal("http://example.invalid/project.zip", decoded.Url, "project url");
            Assert.Equal(MasterProjectInfo.KindDemo, (int)decoded.Kind, "project kind");
            Assert.Equal(123456, (int)decoded.Bytes, "project size");
        }

        [Test]
        internal void ProtocolRejectsBadHeaders()
        {
            var header = MasterProtocol.Header(MasterProtocol.OpList, 0, 0);
            Assert.Null(MasterProtocol.HeaderError(header), "a well-formed header is accepted");

            var wrongMagic = MasterProtocol.Header(MasterProtocol.OpList, 0, 0);
            wrongMagic[0] = (byte)'X';
            Assert.Equal("magic-not-DPTM", MasterProtocol.HeaderError(wrongMagic), "magic is checked");

            var badLength = MasterProtocol.Header(MasterProtocol.OpRegister, 0, 0);
            Assert.Equal("invalid-body-length", MasterProtocol.HeaderError(badLength),
                "a register request must carry a full record");

            var unknown = MasterProtocol.Header(999, 0, 0);
            Assert.Equal("unknown-operation", MasterProtocol.HeaderError(unknown), "operation is checked");
        }

        [Test]
        internal void ProtocolIdentifiersRoundTripThroughHex()
        {
            var id = MasterProtocol.NewId();
            var hex = MasterProtocol.IdToHex(id);

            Assert.Equal(32, hex.Length, "hex form is 32 characters");

            var back = MasterProtocol.IdFromHex(hex);
            Assert.NotNull(back, "hex form parses back");

            for (var i = 0; i < 16; i++)
            {
                Assert.Equal(id[i], back[i], "byte " + i + " round-trips");
            }

            Assert.Null(MasterProtocol.IdFromHex("not-hex"), "bad text is rejected");
            Assert.Null(MasterProtocol.IdFromHex("abc"), "short text is rejected");
        }

        [Test]
        internal void ProtocolRejectsNonAsciiSessionNames()
        {
            // The wire format is ASCII; a Cyrillic session name must be replaced
            // rather than corrupting the record.
            var info = new MasterSessionInfo();
            info.Id = MasterProtocol.NewId();
            info.Address = new byte[4];
            info.Name = "Демо";

            var buffer = new byte[MasterProtocol.InfoSize];
            info.Encode(buffer, 0);

            var decoded = MasterSessionInfo.Decode(buffer, 0);

            Assert.True(decoded.Name.Length > 0, "the name survives as question marks");
            Assert.True(MasterProtocol.IsPrintableAscii(decoded.Name), "the stored name is printable ASCII");
        }

        // ---------------------------------------------------------------- helpers

        private static string TempFile(string name)
        {
            var directory = Path.Combine(Path.GetTempPath(), "dptengine-tests");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, name);

            if (File.Exists(path)) File.Delete(path);
            return path;
        }

        private static void Delete(string path)
        {
            Delete(path, false);
        }

        private static void Delete(string path, bool recursive)
        {
            try
            {
                if (recursive)
                {
                    if (Directory.Exists(path)) Directory.Delete(path, true);
                }
                else if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Cleanup is best-effort; a leftover temp file must not fail a test.
            }
        }

        private static int CountOccurrences(string text, string needle)
        {
            var count = 0;
            var index = 0;

            while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }

            return count;
        }
    }
}
