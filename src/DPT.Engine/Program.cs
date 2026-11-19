//
// DPT.Engine - runtime entry point.
// Copyright (C) 2026 DartPower Team. GPLv2.
//

using System;
using System.IO;

using DPT.Engine.Runtime;
using DPT.Engine.Shared;
using DPT.Engine.Shared.Diagnostics;

namespace DPT.Engine
{
    /// <summary>
    /// The engine executable.
    ///
    /// Arguments:
    ///
    ///   dptengine [project]        play a project, or the built-in template
    ///   --log-level=verbose|info|warning|error
    ///   --headless                 run the self-test instead of the window
    ///   --help
    /// </summary>
    internal static class Program
    {
        /// <summary>
        /// Suppresses the Windows crash dialog.
        ///
        /// A native fault otherwise blocks the process on a modal message box: the
        /// engine appears to hang, the crash handler never runs, and no report is
        /// written. On a demo machine the user sees a frozen full-screen window and
        /// nothing else. With this mode set, the fault propagates, the crash report
        /// is written, and the process exits.
        /// </summary>
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern uint SetErrorMode(uint mode);

        private const uint SemFailCriticalErrors = 0x0001;
        private const uint SemNoGpfaultErrorBox = 0x0002;
        private const uint SemNoAlignmentFaultExcept = 0x0004;
        private const uint SemNoOpenFileErrorBox = 0x8000;

        private static int Main(string[] args)
        {
            SetErrorMode(SemFailCriticalErrors | SemNoGpfaultErrorBox |
                         SemNoAlignmentFaultExcept | SemNoOpenFileErrorBox);
            string projectPath = null;
            var logLevel = LogLevel.Info;
            var headless = false;

            for (var i = 0; i < args.Length; i++)
            {
                var argument = args[i] ?? "";

                if (argument.StartsWith("--log-level=", StringComparison.OrdinalIgnoreCase))
                {
                    logLevel = ParseLevel(argument.Substring("--log-level=".Length));
                    continue;
                }

                if (string.Equals(argument, "--headless", StringComparison.OrdinalIgnoreCase))
                {
                    headless = true;
                    continue;
                }

                if (argument.StartsWith("--project=", StringComparison.OrdinalIgnoreCase))
                {
                    projectPath = argument.Substring("--project=".Length);
                    continue;
                }

                if (string.Equals(argument, "--help", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(argument, "-h", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(argument, "/?", StringComparison.Ordinal))
                {
                    PrintUsage();
                    return 0;
                }

                if (argument.StartsWith("--", StringComparison.Ordinal))
                {
                    Console.Error.WriteLine("Unknown option: " + argument);
                    PrintUsage();
                    return 2;
                }

                projectPath = argument;
            }

            EngineLog.Initialize(null, logLevel, 4 * 1024 * 1024, 3);

            EngineLog.Info("startup", Branding.Title + " starting.");
            EngineLog.Info("startup", "Data directory: " + PortablePaths.DataDirectory);
            EngineLog.Info("startup", "Log file: " + EngineLog.FilePath);

            if (headless)
            {
                return Headless.Run(projectPath);
            }

            using (var app = new EngineApp(projectPath))
            {
                return app.Run();
            }
        }

        private static LogLevel ParseLevel(string text)
        {
            switch ((text ?? "").ToLowerInvariant())
            {
                case "verbose": return LogLevel.Verbose;
                case "warning":
                case "warn": return LogLevel.Warning;
                case "error": return LogLevel.Error;
                default: return LogLevel.Info;
            }
        }

        private static void PrintUsage()
        {
            Console.WriteLine(Branding.Title);
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  dptengine [project]            play a project (or the built-in template)");
            Console.WriteLine("  dptengine --headless           verify a project without a window");
            Console.WriteLine("  dptengine --project=FILE       the project to play or verify");
            Console.WriteLine("  dptengine --log-level=LEVEL    verbose, info, warning or error");
            Console.WriteLine();
            Console.WriteLine("Keys:");
            Console.WriteLine("  F1   toggle the overlay      F2  restart the timeline");
            Console.WriteLine("  F11  fullscreen              F12 screenshot");
            Console.WriteLine("  Esc  quit");
        }
    }
}
