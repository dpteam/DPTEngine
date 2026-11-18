//
// DPT.Engine - master server entry point.
// Copyright (C) 2026 DartPower Team. GPLv2.
//

using System;
using System.Globalization;
using System.Threading;

using DPT.Engine.Shared;

namespace DPT.Engine.Master
{
    /// <summary>
    /// The master executable.
    ///
    /// Runs a directory and, optionally, a relay. Two processes would be cleaner,
    /// but one executable with two listeners is what an operator actually wants to
    /// deploy: copy one file, start it, open two ports.
    ///
    /// Arguments:
    ///   --config=path     use a specific ini file
    ///   --port=n          override the directory port
    ///   --relay-port=n    override the relay port (0 disables it)
    ///   --quiet           no console output
    ///   --self-test       run the built-in checks and exit
    ///   --help
    /// </summary>
    internal static class Program
    {
        private static volatile bool stopping;

        private static int Main(string[] args)
        {
            string configPath = null;
            var quiet = false;
            var selfTest = false;
            var portOverride = -1;
            var relayOverride = -1;

            for (var i = 0; i < args.Length; i++)
            {
                var argument = args[i] ?? "";

                if (argument.StartsWith("--config=", StringComparison.OrdinalIgnoreCase))
                {
                    configPath = argument.Substring("--config=".Length);
                    continue;
                }

                if (argument.StartsWith("--port=", StringComparison.OrdinalIgnoreCase))
                {
                    portOverride = ParseInt(argument.Substring("--port=".Length), -1);
                    continue;
                }

                if (argument.StartsWith("--relay-port=", StringComparison.OrdinalIgnoreCase))
                {
                    relayOverride = ParseInt(argument.Substring("--relay-port=".Length), -1);
                    continue;
                }

                if (string.Equals(argument, "--quiet", StringComparison.OrdinalIgnoreCase))
                {
                    quiet = true;
                    continue;
                }

                if (string.Equals(argument, "--self-test", StringComparison.OrdinalIgnoreCase))
                {
                    selfTest = true;
                    continue;
                }

                if (string.Equals(argument, "--help", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(argument, "-h", StringComparison.OrdinalIgnoreCase))
                {
                    PrintUsage();
                    return 0;
                }

                Console.Error.WriteLine("Unknown option: " + argument);
                PrintUsage();
                return 2;
            }

            var config = MasterConfig.Load(configPath);

            if (portOverride > 0) config.Port = portOverride;
            if (relayOverride >= 0) config.RelayPort = relayOverride;

            foreach (var warning in config.Validate())
            {
                Console.Error.WriteLine("Config: " + warning);
            }

            MasterLog.Initialize(config.ResolveLogFile(), config.LogLevel);
            MasterLog.UseConsole(!quiet);

            if (selfTest)
            {
                return MasterSelfTest.Run();
            }

            return RunServer(config);
        }

        private static int RunServer(MasterConfig config)
        {
            Console.WriteLine(Branding.MasterName + " " + Branding.Version);
            Console.WriteLine();

            var sessions = new SessionDirectory(config);
            var projects = new ProjectCatalogue(config);

            projects.Load();

            using (var server = new MasterServer(config, sessions, projects))
            using (var relay = new RelayService(config))
            {
                if (!server.Start())
                {
                    Console.Error.WriteLine("The directory could not start. Nothing to do.");
                    return 1;
                }

                if (config.RelayPort != 0 && !relay.Start())
                {
                    Console.Error.WriteLine("The relay could not start; sessions will be listed " +
                                            "without relay support.");
                }

                Console.WriteLine("Directory : port " + server.Port);
                Console.WriteLine("Relay     : " + (config.RelayPort == 0
                    ? "disabled"
                    : "port " + relay.Port));
                Console.WriteLine("Projects  : " + projects.Count + " published");
                Console.WriteLine("Log       : " + config.ResolveLogFile());
                Console.WriteLine("State     : " + config.ResolveStateFile());
                Console.WriteLine();
                Console.WriteLine("Press Ctrl+C to stop.");
                Console.WriteLine();

                MasterLog.Info(Branding.MasterName + " listening on port " + server.Port +
                               (config.RelayPort == 0 ? "" : ", relay on " + relay.Port));

                // Ctrl+C is the documented way to stop a console server, and it has
                // to flush the catalogue before the process dies.
                Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs e)
                {
                    e.Cancel = true;
                    stopping = true;
                };

                var flushTimer = new Thread(delegate()
                {
                    while (!stopping)
                    {
                        Thread.Sleep(15000);

                        if (stopping) break;

                        projects.Flush();
                    }
                });

                flushTimer.IsBackground = true;
                flushTimer.Name = "DPT.Engine.Master.Flush";
                flushTimer.Start();

                while (!stopping)
                {
                    Thread.Sleep(200);
                }

                Console.WriteLine();
                Console.WriteLine("Stopping...");

                projects.Flush();

                MasterLog.Info("Stopped.");
            }

            return 0;
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

        private static void PrintUsage()
        {
            Console.WriteLine(Branding.MasterName + " " + Branding.Version);
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  dptengine-master                 start with master.ini beside the exe");
            Console.WriteLine("  dptengine-master --port=25150    override the directory port");
            Console.WriteLine("  dptengine-master --relay-port=0  disable the relay");
            Console.WriteLine("  dptengine-master --self-test     run the built-in checks and exit");
            Console.WriteLine("  dptengine-master --quiet         no console output");
            Console.WriteLine();
            Console.WriteLine("Ports to open: the directory port, and the relay port if enabled.");
        }
    }
}
