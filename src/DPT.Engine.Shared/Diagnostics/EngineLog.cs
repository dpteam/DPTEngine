//
// DPT.Engine - logging on top of System.Diagnostics.Trace.
// Copyright (C) 2026 DartPower Team. GPLv2.
//

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace DPT.Engine.Shared.Diagnostics
{
    public enum LogLevel
    {
        Verbose = 0,
        Info = 1,
        Warning = 2,
        Error = 3,
        Fatal = 4,
        Off = 5
    }

    /// <summary>
    /// The engine's logging front end.
    ///
    /// Everything goes through <see cref="Trace"/> - not a bespoke logger -
    /// because Trace already gives us levels, listeners and a debugger
    /// integration for free, and a profiler or an IDE attaches to it without any
    /// cooperation from us.
    ///
    /// Two rules shape the implementation, both learned from the usual failures:
    ///
    /// <b>No App.config.</b> Trace listeners are normally declared in
    /// configuration, which means shipping an .exe.config beside every program.
    /// That is a second file that can go missing in a copy, and it silently
    /// changes behaviour between a developer's machine and a user's. Listeners
    /// are attached in code instead, so a shipped program is a single file.
    ///
    /// <b>No duplicated lines.</b> The classic bug is calling Initialize from two
    /// places - the entry point and a library that wants to log early - which
    /// attaches a second file listener, so every line appears twice and the two
    /// listeners race on rotation. <see cref="Initialize"/> is idempotent: it
    /// checks a static flag *and* scans <see cref="Trace.Listeners"/> for an
    /// existing instance of our listener type, because a flag alone does not
    /// survive a test harness that clears the collection.
    ///
    /// Every method is safe to call from any thread and never throws. A log call
    /// that can take the engine down is worse than no log at all.
    /// </summary>
    public static class EngineLog
    {
        /// <summary>
        /// Longer messages are truncated. A stack trace logged inside a per-frame
        /// loop is how a log file reaches gigabytes.
        /// </summary>
        private const int MaxMessageLength = 8192;

        private static readonly object Sync = new object();
        private static readonly List<ILogSink> Sinks = new List<ILogSink>();

        private static bool initialized;
        private static LogLevel minimum = LogLevel.Info;
        private static string logFilePath;
        private static FileLogListener fileListener;
        private static MemoryLogListener memoryListener;

        /// <summary>
        /// Raised for every accepted line, on the writing thread.
        ///
        /// The in-game console and the editor's log pane subscribe here instead of
        /// tailing the file. A handler that throws is ignored: a broken UI must
        /// not be able to break logging.
        /// </summary>
        public static event Action<LogLevel, string, string> LineWritten;

        public static bool IsInitialized
        {
            get { lock (Sync) { return initialized; } }
        }

        public static string FilePath
        {
            get { lock (Sync) { return logFilePath; } }
        }

        public static LogLevel MinimumLevel
        {
            get { lock (Sync) { return minimum; } }
            set { lock (Sync) { minimum = value; } }
        }

        // ---------------------------------------------------------------- setup

        /// <summary>
        /// Attaches the file and memory listeners. Calling it again does nothing.
        /// </summary>
        /// <param name="path">
        /// The log file. When null, <c>&lt;exename&gt;.log</c> is used in the
        /// writable data directory.
        /// </param>
        public static void Initialize(string path)
        {
            Initialize(path, LogLevel.Info, 4 * 1024 * 1024, 3);
        }

        public static void Initialize(string path, LogLevel level, int maxFileBytes, int maxArchives)
        {
            lock (Sync)
            {
                minimum = level;

                // Idempotency. A second call is ignored unless the first attempt
                // never managed to open a file, in which case the caller may be
                // retrying with a different path.
                if (initialized && fileListener != null) return;

                if (string.IsNullOrEmpty(path))
                {
                    path = DefaultPath();
                }

                DetachListeners();

                // The default listener writes to OutputDebugString and can raise an
                // assertion dialog from a background thread. A game must never
                // block on a message box because of a log line.
                RemoveDefaultListeners();

                memoryListener = new MemoryLogListener(2048);
                Trace.Listeners.Add(memoryListener);

                fileListener = new FileLogListener(path, maxFileBytes, maxArchives);
                Trace.Listeners.Add(fileListener);

                // Without this a crash loses the last few lines, which are the only
                // ones anybody reads.
                Trace.AutoFlush = true;

                logFilePath = path;
                initialized = true;
            }

            Info("log", "Logging to " + FilePath);
        }

        /// <summary>Detaches everything. Used by tests that need to move the file.</summary>
        public static void Shutdown()
        {
            lock (Sync)
            {
                DetachListeners();
                logFilePath = null;
                initialized = false;
            }
        }

        private static void DetachListeners()
        {
            if (fileListener != null)
            {
                Trace.Listeners.Remove(fileListener);
                fileListener.Dispose();
                fileListener = null;
            }

            if (memoryListener != null)
            {
                Trace.Listeners.Remove(memoryListener);
                memoryListener = null;
            }

            // Belt and braces: a listener left behind by an earlier AppDomain or by
            // a host process that used the same types.
            for (var i = Trace.Listeners.Count - 1; i >= 0; i--)
            {
                if (Trace.Listeners[i] is FileLogListener || Trace.Listeners[i] is MemoryLogListener)
                {
                    Trace.Listeners.RemoveAt(i);
                }
            }
        }

        private static void RemoveDefaultListeners()
        {
            for (var i = Trace.Listeners.Count - 1; i >= 0; i--)
            {
                var listener = Trace.Listeners[i] as DefaultTraceListener;
                if (listener == null) continue;

                listener.AssertUiEnabled = false;
                Trace.Listeners.RemoveAt(i);
            }
        }

        private static string DefaultPath()
        {
            var name = "dptengine";

            try
            {
                var entry = System.Reflection.Assembly.GetEntryAssembly();
                if (entry != null)
                {
                    var simple = entry.GetName().Name;
                    if (!string.IsNullOrEmpty(simple)) name = simple;
                }
            }
            catch
            {
                // Keep the constant name.
            }

            return PortablePaths.EnsureParent(Path.Combine(PortablePaths.DataDirectory, name + ".log"));
        }

        // ---------------------------------------------------------------- sinks

        /// <summary>
        /// An extra destination besides the file and the ring buffer - a network
        /// feed for the editor, for example. Sinks are called synchronously and
        /// must not throw.
        /// </summary>
        public static void AddSink(ILogSink sink)
        {
            if (sink == null) return;

            lock (Sync)
            {
                if (!Sinks.Contains(sink)) Sinks.Add(sink);
            }
        }

        public static void RemoveSink(ILogSink sink)
        {
            if (sink == null) return;

            lock (Sync)
            {
                Sinks.Remove(sink);
            }
        }

        // ---------------------------------------------------------------- writers

        public static void Verbose(string category, string message)
        {
            Write(LogLevel.Verbose, category, message);
        }

        public static void Info(string category, string message)
        {
            Write(LogLevel.Info, category, message);
        }

        public static void Warn(string category, string message)
        {
            Write(LogLevel.Warning, category, message);
        }

        public static void Error(string category, string message)
        {
            Write(LogLevel.Error, category, message);
        }

        public static void Fatal(string category, string message)
        {
            Write(LogLevel.Fatal, category, message);
        }

        /// <summary>
        /// Logs an exception with its whole inner-exception chain. The category
        /// defaults to the exception type, so grepping for "NullReferenceException"
        /// finds every occurrence.
        /// </summary>
        public static void Exception(string category, Exception error)
        {
            if (error == null) return;

            if (string.IsNullOrEmpty(category)) category = error.GetType().Name;
            Write(LogLevel.Error, category, Describe(error));
        }

        public static void Fatal(string category, string message, Exception error)
        {
            var text = message ?? "fatal";

            if (error != null)
            {
                text += Environment.NewLine + Describe(error);
            }

            Write(LogLevel.Fatal, category, text);
        }

        private static string Describe(Exception error)
        {
            var builder = new StringBuilder(256);
            var current = error;
            var depth = 0;

            while (current != null && depth < 8)
            {
                if (depth > 0) builder.Append(" ---> ");
                builder.Append(current.GetType().Name);

                if (!string.IsNullOrEmpty(current.Message))
                {
                    builder.Append(": ").Append(current.Message);
                }

                if (!string.IsNullOrEmpty(current.StackTrace))
                {
                    builder.Append(Environment.NewLine).Append(current.StackTrace);
                }

                current = current.InnerException;
                depth++;
            }

            return builder.ToString();
        }

        public static void Write(LogLevel level, string category, string message)
        {
            if (level < minimum) return;

            var text = message ?? "";

            if (text.Length > MaxMessageLength)
            {
                text = text.Substring(0, MaxMessageLength) + " ... (truncated)";
            }

            var formatted = Format(level, category, text);

            var handler = LineWritten;

            if (handler != null)
            {
                try
                {
                    handler(level, category ?? "", text);
                }
                catch
                {
                    // A subscriber must not be able to break the caller.
                }
            }

            try
            {
                switch (level)
                {
                    case LogLevel.Warning:
                        Trace.TraceWarning(formatted);
                        break;
                    case LogLevel.Error:
                    case LogLevel.Fatal:
                        Trace.TraceError(formatted);
                        break;
                    default:
                        Trace.TraceInformation(formatted);
                        break;
                }
            }
            catch
            {
                // A disposed listener can make Trace throw. A log line is never
                // worth taking the engine down for.
            }

            ILogSink[] snapshot = null;

            lock (Sync)
            {
                if (Sinks.Count > 0) snapshot = Sinks.ToArray();
            }

            if (snapshot == null) return;

            for (var i = 0; i < snapshot.Length; i++)
            {
                try
                {
                    snapshot[i].Write(level, category ?? "", text);
                }
                catch
                {
                    // Same rule as above.
                }
            }
        }

        private static string Format(LogLevel level, string category, string message)
        {
            var builder = new StringBuilder(message.Length + 72);

            builder.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
            builder.Append(" [").Append(Letter(level)).Append("] ");

            if (!string.IsNullOrEmpty(category))
            {
                builder.Append(category).Append(": ");
            }

            // The managed thread id is what makes a multi-threaded log readable:
            // the audio thread and the render thread interleave, and without it
            // the file reads like nonsense.
            builder.Append("(t").Append(Thread.CurrentThread.ManagedThreadId).Append(") ");
            builder.Append(message);

            return builder.ToString();
        }

        private static char Letter(LogLevel level)
        {
            switch (level)
            {
                case LogLevel.Verbose: return 'V';
                case LogLevel.Warning: return 'W';
                case LogLevel.Error: return 'E';
                case LogLevel.Fatal: return 'F';
                default: return 'I';
            }
        }

        // ---------------------------------------------------------------- readers

        /// <summary>Recent lines, oldest first. Used by the in-game console.</summary>
        public static string[] Recent(int count)
        {
            lock (Sync)
            {
                return memoryListener == null ? new string[0] : memoryListener.Recent(count);
            }
        }

        public static string RecentText(int count)
        {
            var lines = Recent(count);
            if (lines.Length == 0) return "";

            var builder = new StringBuilder(lines.Length * 64);

            for (var i = 0; i < lines.Length; i++)
            {
                if (i > 0) builder.Append('\n');
                builder.Append(lines[i]);
            }

            return builder.ToString();
        }

        /// <summary>An additional log destination, such as a network feed.</summary>
        public interface ILogSink
        {
            void Write(LogLevel level, string category, string message);
        }

        // ---------------------------------------------------------------- listeners

        /// <summary>
        /// Keeps the last N lines in memory, so the overlay and the editor's log
        /// pane can show them without re-reading the file.
        /// </summary>
        private sealed class MemoryLogListener : TraceListener
        {
            private readonly string[] lines;
            private readonly object gate = new object();

            private int next;
            private int count;

            public MemoryLogListener(int capacity)
            {
                lines = new string[capacity < 16 ? 16 : capacity];
                Name = "DPT.Engine.Memory";
            }

            public override void Write(string message)
            {
                // TraceInformation goes through WriteLine; nothing uses Write.
            }

            public override void WriteLine(string message)
            {
                lock (gate)
                {
                    lines[next] = message;
                    next = (next + 1) % lines.Length;
                    if (count < lines.Length) count++;
                }
            }

            public string[] Recent(int wanted)
            {
                lock (gate)
                {
                    if (wanted <= 0 || count == 0) return new string[0];

                    var total = wanted < count ? wanted : count;
                    var result = new string[total];

                    for (var i = 0; i < total; i++)
                    {
                        // Oldest first: walk back from the newest entry.
                        var index = next - total + i;
                        while (index < 0) index += lines.Length;
                        result[i] = lines[index];
                    }

                    return result;
                }
            }
        }

        /// <summary>
        /// A file listener with size rotation and no surprises.
        ///
        /// The file is opened with FileShare.ReadWrite | Delete so the launcher can
        /// tail it while the game writes, and so the editor and the runtime can
        /// share a folder. Rotation closes, renames and reopens under the writer's
        /// lock; a crash in that window costs at most one line, which is better
        /// than a lock held forever by a process that has gone away.
        /// </summary>
        private sealed class FileLogListener : TraceListener
        {
            private readonly object gate = new object();
            private readonly string path;
            private readonly int maxBytes;
            private readonly int maxArchives;

            private StreamWriter writer;
            private long written;
            private bool broken;

            public FileLogListener(string path, int maxBytes, int maxArchives)
            {
                this.path = path;
                this.maxBytes = maxBytes < 64 * 1024 ? 64 * 1024 : maxBytes;
                this.maxArchives = maxArchives < 0 ? 0 : maxArchives;

                Name = "DPT.Engine.File";
                Open();
            }

            private void Open()
            {
                try
                {
                    PortablePaths.EnsureParent(path);

                    written = File.Exists(path) ? new FileInfo(path).Length : 0;

                    var stream = new FileStream(path, FileMode.Append, FileAccess.Write,
                        FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.None);

                    writer = new StreamWriter(stream, new UTF8Encoding(false));
                    writer.AutoFlush = true;
                    broken = false;
                }
                catch
                {
                    // A read-only install folder must not stop the game. Logging
                    // degrades to the memory ring and the debugger.
                    broken = true;
                    writer = null;
                }
            }

            public override void Write(string message)
            {
                // EngineLog only ever calls TraceInformation/TraceWarning/TraceError,
                // all of which arrive through WriteLine.
            }

            public override void WriteLine(string message)
            {
                lock (gate)
                {
                    if (broken) return;

                    if (writer == null)
                    {
                        Open();
                        if (writer == null) return;
                    }

                    try
                    {
                        writer.WriteLine(message);
                        written += message.Length + Environment.NewLine.Length;

                        if (written >= maxBytes) Rotate();
                    }
                    catch
                    {
                        broken = true;

                        try
                        {
                            if (writer != null) writer.Dispose();
                        }
                        catch
                        {
                        }

                        writer = null;
                    }
                }
            }

            private void Rotate()
            {
                try
                {
                    if (writer != null)
                    {
                        writer.Flush();
                        writer.Dispose();
                        writer = null;
                    }

                    if (maxArchives > 0)
                    {
                        // log -> log.1 -> log.2 ... and drop the oldest.
                        for (var i = maxArchives; i >= 1; i--)
                        {
                            var from = i == 1 ? path : path + "." + (i - 1);
                            var to = path + "." + i;

                            if (!File.Exists(from)) continue;
                            if (File.Exists(to)) File.Delete(to);
                            File.Move(from, to);
                        }
                    }
                    else
                    {
                        File.Delete(path);
                    }

                    written = 0;
                    Open();
                }
                catch
                {
                    broken = true;
                }
            }

            public override void Flush()
            {
                lock (gate)
                {
                    if (writer == null) return;

                    try
                    {
                        writer.Flush();
                    }
                    catch
                    {
                        broken = true;
                    }
                }
            }

            protected override void Dispose(bool disposing)
            {
                lock (gate)
                {
                    if (writer != null)
                    {
                        try
                        {
                            writer.Flush();
                            writer.Dispose();
                        }
                        catch
                        {
                        }

                        writer = null;
                    }

                    broken = true;
                }

                base.Dispose(disposing);
            }
        }
    }
}
