//
// DPT.Engine - runtime entry point.
// Copyright (C) 2026 DartPower Team. GPLv2.
//

using System;
using System.IO;

using DPT.Engine.Import;
using DPT.Engine.Project;
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
            // The error-mode flags suppress the Windows dialogs a crash would otherwise
            // raise, which is what a player wants. They also suppress Windows Error
            // Reporting, and therefore the crash dump - so DPT_ALLOW_CRASH_DIALOGS=1
            // turns them off when someone needs a dump to debug with.
            if (Environment.GetEnvironmentVariable("DPT_ALLOW_CRASH_DIALOGS") != "1")
            {
                SetErrorMode(SemFailCriticalErrors | SemNoGpfaultErrorBox |
                             SemNoAlignmentFaultExcept | SemNoOpenFileErrorBox);
            }
            string projectPath = null;
            string importPath = null;
            string exportPath = null;
            var logLevel = LogLevel.Info;
            var headless = false;
            var overlay = true;

            for (var i = 0; i < args.Length; i++)
            {
                var argument = args[i] ?? "";

                if (argument.StartsWith("--log-level=", StringComparison.OrdinalIgnoreCase))
                {
                    logLevel = ParseLevel(argument.Substring("--log-level=".Length));
                    continue;
                }

                if (argument.StartsWith("--import=", StringComparison.OrdinalIgnoreCase))
                {
                    importPath = argument.Substring("--import=".Length);
                    continue;
                }

                if (argument.StartsWith("--export=", StringComparison.OrdinalIgnoreCase))
                {
                    exportPath = argument.Substring("--export=".Length);
                    continue;
                }

                if (string.Equals(argument, "--headless", StringComparison.OrdinalIgnoreCase))
                {
                    headless = true;
                    continue;
                }

                if (string.Equals(argument, "--no-overlay", StringComparison.OrdinalIgnoreCase))
                {
                    overlay = false;
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

            // Installed before anything can fail, not when the game loop starts: a
            // crash while loading a project is exactly as interesting as one while
            // rendering, and until this line runs a native fault leaves no trace at all.
            FaultGuard.InstallCrashHandler(null);

            EngineLog.Info("startup", Branding.Title + " starting.");
            EngineLog.Info("startup", "Data directory: " + PortablePaths.DataDirectory);
            EngineLog.Info("startup", "Log file: " + EngineLog.FilePath);

            if (!string.IsNullOrEmpty(importPath))
            {
                return ImportProject(importPath, exportPath);
            }

            // A deliberate fault, to prove the native fault tracer works.
            //
            // Instrumentation that has never been seen to fire is not instrumentation,
            // it is a comforting assumption. This reads address zero, which raises
            // exactly the exception a real crash does, and the tracer must name it.
            if (Environment.GetEnvironmentVariable("DPT_CRASH_TEST") == "1")
            {
                Console.WriteLine("Crashing on purpose to test the fault tracer.");
                System.Runtime.InteropServices.Marshal.ReadInt32(IntPtr.Zero);
            }

            if (headless)
            {
                return Headless.Run(projectPath);
            }

            using (var app = new EngineApp(projectPath))
            {
                app.OverlayAllowed = overlay;

                int minimal;
                int.TryParse(Environment.GetEnvironmentVariable("DPT_MINIMAL"), out minimal);
                app.MinimalMode = minimal;

                return app.Run();
            }
        }

        /// <summary>
        /// Converts an Addict2 .dmo project into the engine's own format.
        ///
        /// This is the migration path for everything made with the original editor:
        /// the importer reads the chunked binary file, bakes the procedural textures,
        /// regenerates the primitive geometry, and writes a .dptproj the editor and
        /// the runtime both understand.
        /// </summary>
        private static int ImportProject(string input, string output)
        {
            try
            {
                if (!File.Exists(input))
                {
                    Console.Error.WriteLine("No such file: " + input);
                    return 1;
                }

                Console.WriteLine("Importing " + input);

                var project = DmoReader.Load(input);

                if (string.IsNullOrEmpty(output))
                {
                    output = Path.ChangeExtension(input, ".dptproj");
                }

                ProjectSerializer.Save(project, output);

                Console.WriteLine("  scenes   : " + project.Scenes.Count);
                Console.WriteLine("  textures : " + project.Textures.Count);
                Console.WriteLine("  effects  : " + project.Timeline.Count);
                Console.WriteLine("  duration : " + project.Duration + " frames at " +
                                  project.FramesPerSecond + " fps");
                Console.WriteLine("Wrote " + output);

                EngineLog.Info("import", "Imported " + input + " to " + output + ".");

                return 0;
            }
            catch (Exception error)
            {
                EngineLog.Exception("import", error);
                Console.Error.WriteLine("Import failed: " + error.Message);
                return 1;
            }
        }

        private static LogLevel ParseLevel(string text)        {
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
            Console.WriteLine("  dptengine [project]            play a .dptproj or an Addict2 .dmo");
            Console.WriteLine("  dptengine --headless           verify a project without a window");
            Console.WriteLine("  dptengine --project=FILE       the project to play or verify");
            Console.WriteLine("  dptengine --import=FILE.dmo    convert an Addict2 project to .dptproj");
            Console.WriteLine("  dptengine --export=FILE        where the import is written");
            Console.WriteLine("  dptengine --log-level=LEVEL    verbose, info, warning or error");
            Console.WriteLine();
            Console.WriteLine("Keys:");
            Console.WriteLine("  F1   toggle the overlay      F2  restart the timeline");
            Console.WriteLine("  F11  fullscreen              F12 screenshot");
            Console.WriteLine("  Esc  quit");
        }
    }
}
