//
// DPT.Engine - master logging.
// Copyright (C) 2026 DartPower Team. GPLv2.
//

using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace DPT.Engine.Master
{
    /// <summary>
    /// A small console-and-file logger for the master.
    ///
    /// Separate from the engine's Trace-based logger on purpose: the master has to
    /// build for net40 and net9.0, is often run as a service with its output
    /// redirected to a file, and needs nothing the engine logger offers beyond
    /// levels and a timestamp. Keeping it self-contained means the master has no
    /// dependency on the engine assembly at all.
    /// </summary>
    internal static class MasterLog
    {
        private static readonly object Sync = new object();

        private static int minimum = 1; // 0 verbose, 1 info, 2 warning, 3 error
        private static string path;
        private static bool toConsole = true;

        internal static void Initialize(string file, string level)
        {
            lock (Sync)
            {
                path = file;
                minimum = ParseLevel(level);

                if (string.IsNullOrEmpty(path)) return;

                try
                {
                    var directory = Path.GetDirectoryName(path);

                    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }
                }
                catch
                {
                    // A read-only folder means console-only logging.
                    path = null;
                }
            }
        }

        internal static void UseConsole(bool enabled)
        {
            lock (Sync) { toConsole = enabled; }
        }

        private static int ParseLevel(string level)
        {
            switch ((level ?? "").ToLowerInvariant())
            {
                case "verbose": return 0;
                case "warning":
                case "warn": return 2;
                case "error": return 3;
                default: return 1;
            }
        }

        internal static void Verbose(string message) { Write(0, "V", message); }
        internal static void Info(string message) { Write(1, "I", message); }
        internal static void Warn(string message) { Write(2, "W", message); }
        internal static void Error(string message) { Write(3, "E", message); }

        internal static void Exception(string context, Exception error)
        {
            if (error == null) return;

            Write(3, "E", context + ": " + error.GetType().Name + ": " + error.Message);

            if (!string.IsNullOrEmpty(error.StackTrace))
            {
                Write(3, "E", error.StackTrace);
            }
        }

        private static void Write(int level, string letter, string message)
        {
            if (level < minimum) return;

            var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) +
                       " [" + letter + "] (t" + Thread.CurrentThread.ManagedThreadId + ") " + message;

            lock (Sync)
            {
                if (toConsole)
                {
                    try
                    {
                        Console.WriteLine(line);
                    }
                    catch
                    {
                    }
                }

                if (string.IsNullOrEmpty(path)) return;

                try
                {
                    File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
                }
                catch
                {
                    // A failing log must never take the directory down; the console
                    // copy above is the fallback.
                }
            }
        }
    }
}
