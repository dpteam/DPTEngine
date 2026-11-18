//
// DPT.Engine - master directory configuration.
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
    /// The master's settings, read from an ini file next to the executable.
    ///
    /// An ini rather than JSON because the same file has to be editable on a
    /// Windows XP server with Notepad and no JSON tooling, and because every
    /// setting here is a single scalar. The format is deliberately forgiving: an
    /// unknown key is ignored, a malformed value falls back to its default, and a
    /// missing file is normal rather than an error.
    /// </summary>
    internal sealed class MasterConfig
    {
        /// <summary>Port the directory listens on.</summary>
        internal int Port = MasterProtocol.DefaultPort;

        /// <summary>Port the relay listens on. Zero disables the relay.</summary>
        internal int RelayPort = MasterProtocol.DefaultRelayPort;

        /// <summary>Interface to bind. Empty means every interface.</summary>
        internal string BindAddress = "";

        /// <summary>Maximum simultaneous sessions.</summary>
        internal int MaxSessions = MasterProtocol.MaxServers;

        /// <summary>Maximum published projects.</summary>
        internal int MaxProjects = MasterProtocol.MaxProjects;

        /// <summary>Sessions per source address. Stops one host filling the list.</summary>
        internal int MaxSessionsPerAddress = MasterProtocol.MaxServersPerIp;

        /// <summary>Seconds without a heartbeat before a session is dropped.</summary>
        internal int SessionTimeoutSeconds = 90;

        /// <summary>Public host name advertised to clients, for relay replies.</summary>
        internal string PublicHost = "";

        /// <summary>Bytes a single project row may advertise.</summary>
        internal long MaxProjectBytes = 512L * 1024 * 1024;

        /// <summary>Log level: verbose, info, warning, error.</summary>
        internal string LogLevel = "info";

        /// <summary>Path of the log file. Empty means one beside the executable.</summary>
        internal string LogFile = "";

        /// <summary>Where the directory's state is written, so a restart keeps ids.</summary>
        internal string StateFile = "";

        /// <summary>
        /// A shared secret a host may present to publish without the address check.
        ///
        /// Empty disables it. It exists for the common case of a host behind NAT
        /// that publishes through a proxy: the proxy's address is not the host's,
        /// and without a token every such publish would be rejected.
        /// </summary>
        internal string PublishToken = "";

        internal static MasterConfig Load(string path)
        {
            var config = new MasterConfig();

            if (string.IsNullOrEmpty(path)) path = DefaultPath();

            if (!File.Exists(path))
            {
                // First run: write a commented default so an operator has something
                // to edit rather than an empty folder.
                try
                {
                    File.WriteAllText(path, DefaultText(), new UTF8Encoding(false));
                }
                catch
                {
                    // A read-only folder is fine; the defaults still apply.
                }

                return config;
            }

            try
            {
                var lines = File.ReadAllLines(path, Encoding.UTF8);

                for (var i = 0; i < lines.Length; i++)
                {
                    var line = lines[i].Trim();

                    if (line.Length == 0) continue;
                    if (line[0] == '#' || line[0] == ';') continue;
                    if (line[0] == '[') continue; // section headers are decoration

                    var split = line.IndexOf('=');
                    if (split <= 0) continue;

                    var key = line.Substring(0, split).Trim().ToLowerInvariant();
                    var value = line.Substring(split + 1).Trim();

                    config.Apply(key, value);
                }
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("Could not read " + path + ": " + error.Message);
            }

            return config;
        }

        private void Apply(string key, string value)
        {
            switch (key)
            {
                case "port": Port = ParseInt(value, Port); break;
                case "relayport": RelayPort = ParseInt(value, RelayPort); break;
                case "bind": BindAddress = value; break;
                case "maxsessions": MaxSessions = ParseInt(value, MaxSessions); break;
                case "maxprojects": MaxProjects = ParseInt(value, MaxProjects); break;
                case "maxsessionsperaddress": MaxSessionsPerAddress = ParseInt(value, MaxSessionsPerAddress); break;
                case "sessiontimeout": SessionTimeoutSeconds = ParseInt(value, SessionTimeoutSeconds); break;
                case "publichost": PublicHost = value; break;
                case "maxprojectbytes": MaxProjectBytes = ParseLong(value, MaxProjectBytes); break;
                case "loglevel": LogLevel = value; break;
                case "logfile": LogFile = value; break;
                case "statefile": StateFile = value; break;
                case "publishtoken": PublishToken = value; break;
            }
        }

        private static int ParseInt(string text, int fallback)
        {
            int value;

            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                return value;
            }

            return fallback;
        }

        private static long ParseLong(string text, long fallback)
        {
            long value;

            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                return value;
            }

            return fallback;
        }

        internal static string DefaultPath()
        {
            return Path.Combine(PortablePaths.ExecutableDirectory, "master.ini");
        }

        private static string DefaultText()
        {
            var builder = new StringBuilder();

            builder.AppendLine("# DPT.Engine master server");
            builder.AppendLine("#");
            builder.AppendLine("# Every value has a working default; delete a line to use it.");
            builder.AppendLine();
            builder.AppendLine("[network]");
            builder.AppendLine("port=" + MasterProtocol.DefaultPort);
            builder.AppendLine("relayPort=" + MasterProtocol.DefaultRelayPort);
            builder.AppendLine("# bind=            ; empty means every interface");
            builder.AppendLine("# publicHost=      ; advertised in relay replies, e.g. master.example.org");
            builder.AppendLine();
            builder.AppendLine("[limits]");
            builder.AppendLine("maxSessions=" + MasterProtocol.MaxServers);
            builder.AppendLine("maxProjects=" + MasterProtocol.MaxProjects);
            builder.AppendLine("maxSessionsPerAddress=" + MasterProtocol.MaxServersPerIp);
            builder.AppendLine("sessionTimeout=" + 90);
            builder.AppendLine("maxProjectBytes=" + (512L * 1024 * 1024));
            builder.AppendLine();
            builder.AppendLine("[security]");
            builder.AppendLine("# publishToken=    ; shared secret that lets a host publish through a proxy");
            builder.AppendLine();
            builder.AppendLine("[logging]");
            builder.AppendLine("logLevel=info      ; verbose, info, warning, error");
            builder.AppendLine("# logFile=");
            builder.AppendLine("# stateFile=");

            return builder.ToString();
        }

        internal string ResolveLogFile()
        {
            if (!string.IsNullOrEmpty(LogFile)) return LogFile;

            return Path.Combine(PortablePaths.DataDirectory, "master.log");
        }

        internal string ResolveStateFile()
        {
            if (!string.IsNullOrEmpty(StateFile)) return StateFile;

            return Path.Combine(PortablePaths.DataDirectory, "master.state");
        }

        /// <summary>Warnings for settings that will not do what the operator expects.</summary>
        internal IEnumerable<string> Validate()
        {
            if (Port <= 0 || Port > 65535)
            {
                yield return "port " + Port + " is not a valid port; using " + MasterProtocol.DefaultPort;
                Port = MasterProtocol.DefaultPort;
            }

            if (RelayPort != 0 && (RelayPort <= 0 || RelayPort > 65535))
            {
                yield return "relayPort " + RelayPort + " is not a valid port; disabling the relay";
                RelayPort = 0;
            }

            if (RelayPort != 0 && RelayPort == Port)
            {
                yield return "relayPort equals port; disabling the relay";
                RelayPort = 0;
            }

            if (MaxSessions < 1) MaxSessions = 1;
            if (MaxSessions > MasterProtocol.MaxServers)
            {
                yield return "maxSessions is capped at " + MasterProtocol.MaxServers +
                             " by the wire format";
                MaxSessions = MasterProtocol.MaxServers;
            }

            if (MaxProjects < 0) MaxProjects = 0;
            if (SessionTimeoutSeconds < 10) SessionTimeoutSeconds = 10;
        }
    }
}
