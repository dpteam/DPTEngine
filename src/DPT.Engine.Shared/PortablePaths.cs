//
// DPT.Engine - portable data locations.
// Copyright (C) 2026 DartPower Team. GPLv2.
//

using System;
using System.IO;
using System.Reflection;

namespace DPT.Engine.Shared
{
    /// <summary>
    /// Where the engine keeps its files.
    ///
    /// The rule is "portable first": if the folder the executable lives in is
    /// writable, everything goes next to the executable, which is what a demo
    /// party machine, a USB stick or a Windows XP install expects. Only when that
    /// folder is read-only (Program Files on Vista and later) does the engine
    /// fall back to %APPDATA%.
    /// </summary>
    public static class PortablePaths
    {
        private static string dataDirectory;
        private static string executableDirectory;

        public static string ExecutableDirectory
        {
            get
            {
                if (executableDirectory != null) return executableDirectory;

                try
                {
                    var location = Assembly.GetEntryAssembly() != null
                        ? Assembly.GetEntryAssembly().Location
                        : Assembly.GetExecutingAssembly().Location;

                    executableDirectory = Path.GetDirectoryName(location);
                }
                catch
                {
                    executableDirectory = Directory.GetCurrentDirectory();
                }

                if (string.IsNullOrEmpty(executableDirectory))
                {
                    executableDirectory = Directory.GetCurrentDirectory();
                }

                return executableDirectory;
            }
        }

        /// <summary>The writable directory for logs, config and caches.</summary>
        public static string DataDirectory
        {
            get
            {
                if (dataDirectory != null) return dataDirectory;

                if (IsWritable(ExecutableDirectory))
                {
                    dataDirectory = ExecutableDirectory;
                    return dataDirectory;
                }

                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                if (string.IsNullOrEmpty(appData))
                {
                    appData = Path.Combine(ExecutableDirectory, "user");
                }

                dataDirectory = Path.Combine(appData, Branding.DataFolderName);
                Directory.CreateDirectory(dataDirectory);
                return dataDirectory;
            }
        }

        public static string Storage(string name)
        {
            return Path.Combine(DataDirectory, name);
        }

        /// <summary>
        /// Resolves a data file: next to the executable first, then under
        /// <c>Data\</c>. Returns the first path that exists, or the plain
        /// executable-relative path so callers can report a useful error.
        /// </summary>
        public static string Resolve(string relative)
        {
            if (string.IsNullOrEmpty(relative)) return ExecutableDirectory;

            if (Path.IsPathRooted(relative)) return relative;

            var beside = Path.Combine(ExecutableDirectory, relative);
            if (File.Exists(beside) || Directory.Exists(beside)) return beside;

            var data = Path.Combine(Path.Combine(ExecutableDirectory, "Data"), relative);
            if (File.Exists(data) || Directory.Exists(data)) return data;

            return beside;
        }

        /// <summary>Probes by writing a zero-byte file and deleting it again.</summary>
        public static bool IsWritable(string directory)
        {
            if (string.IsNullOrEmpty(directory)) return false;

            try
            {
                if (!Directory.Exists(directory)) return false;

                var probe = Path.Combine(directory, ".dptengine-write-test");
                using (var stream = new FileStream(probe, FileMode.Create, FileAccess.Write, FileShare.None, 1))
                {
                    stream.WriteByte(0);
                }

                File.Delete(probe);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Joins and creates the parent directory of a file path.</summary>
        public static string EnsureParent(string path)
        {
            var parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
            {
                Directory.CreateDirectory(parent);
            }

            return path;
        }
    }
}
