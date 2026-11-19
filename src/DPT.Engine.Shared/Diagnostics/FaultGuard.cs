//
// DPT.Engine - fault tolerance.
// Copyright (C) 2026 DartPower Team. GPLv2.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace DPT.Engine.Shared.Diagnostics
{
    /// <summary>
    /// How a subsystem is currently behaving.
    ///
    /// The engine never dies because one part of it failed. A missing sound
    /// device, a shader that will not compile, one corrupt object in a project:
    /// each of those disables a named subsystem and the rest keeps running. The
    /// launcher shows this table, which is the difference between "it crashed" and
    /// "it runs, without audio".
    /// </summary>
    public enum SubsystemState
    {
        /// <summary>Never started, or deliberately disabled.</summary>
        Idle = 0,

        /// <summary>Started and believed to be working.</summary>
        Running = 1,

        /// <summary>Failed at least once but recovered; still usable.</summary>
        Degraded = 2,

        /// <summary>Failed repeatedly and disabled itself. The engine continues.</summary>
        Failed = 3
    }

    /// <summary>A named part of the engine that is allowed to fail.</summary>
    public sealed class Subsystem
    {
        internal Subsystem(string name)
        {
            Name = name;
            State = SubsystemState.Idle;
        }

        public string Name { get; private set; }
        public SubsystemState State { get; internal set; }
        public int FailureCount { get; internal set; }
        public string LastError { get; internal set; }

        /// <summary>True when callers should skip this subsystem entirely.</summary>
        public bool IsDisabled
        {
            get { return State == SubsystemState.Failed; }
        }
    }

    /// <summary>
    /// The engine's safety net.
    ///
    /// Three jobs, all of them about staying alive:
    ///
    /// 1. <see cref="Guard"/> and <see cref="Attempt{T}"/> - a call that is allowed
    ///    to fail. Used at every boundary where data enters the engine from
    ///    outside it: project files, scripts, network packets, shader compilation.
    ///
    /// 2. <see cref="Subsystem"/> tracking - a failure is remembered and reported,
    ///    not retried sixty times a second and not silently swallowed.
    ///
    /// 3. Crash capture - the last-resort handler that writes down what happened
    ///    before the process disappears. A full-screen demo on somebody else's
    ///    machine has no console to read.
    /// </summary>
    public static class FaultGuard
    {
        private static readonly object Sync = new object();

        private static readonly Dictionary<string, Subsystem> Subsystems =
            new Dictionary<string, Subsystem>(StringComparer.OrdinalIgnoreCase);

        private static bool crashHandlerInstalled;
        private static volatile bool shuttingDown;
        private static string crashDirectory;

        /// <summary>
        /// Guards the first-chance handler against re-entering itself.
        ///
        /// A first-chance handler runs for *every* exception, including the ones
        /// thrown by the handler's own logging. Without this flag a broken log sink
        /// turns into unbounded recursion, and the stack overflows before anything
        /// is written.
        /// </summary>
        [ThreadStatic] private static bool inFirstChance;

        /// <summary>
        /// How many first-chance exceptions to report at warning level before the
        /// rest go to the verbose log. A busy program throws a lot of expected
        /// exceptions - a cancelled operation, a file probe - and a log that is
        /// nothing but those is useless when something real happens.
        /// </summary>
        private static int firstChanceReports;
        private const int FirstChanceReportLimit = 40;

        /// <summary>
        /// How many consecutive failures a subsystem tolerates before it disables
        /// itself. Three rides out a transient device loss and still stops a
        /// permanently broken shader from spamming the log.
        /// </summary>
        public static int FailureThreshold = 3;

        /// <summary>Raised when a subsystem changes state. Used by the overlay.</summary>
        public static event Action<Subsystem> SubsystemChanged;

        // ------------------------------------------------------------- subsystems

        public static Subsystem Get(string name)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentNullException("name");

            lock (Sync)
            {
                Subsystem subsystem;

                if (!Subsystems.TryGetValue(name, out subsystem))
                {
                    subsystem = new Subsystem(name);
                    Subsystems[name] = subsystem;
                }

                return subsystem;
            }
        }

        public static bool IsDisabled(string name)
        {
            return Get(name).IsDisabled;
        }

        public static void MarkRunning(string name)
        {
            var subsystem = Get(name);
            var changed = false;

            lock (Sync)
            {
                if (subsystem.State != SubsystemState.Running)
                {
                    subsystem.State = SubsystemState.Running;
                    subsystem.LastError = null;
                    changed = true;
                }
            }

            if (changed) Raise(subsystem);
        }

        public static void MarkDegraded(string name, string reason)
        {
            var subsystem = Get(name);
            var changed = false;

            lock (Sync)
            {
                subsystem.LastError = reason;

                if (subsystem.State != SubsystemState.Degraded)
                {
                    subsystem.State = SubsystemState.Degraded;
                    changed = true;
                }
            }

            if (changed) Raise(subsystem);
        }

        public static void MarkFailed(string name, string reason)
        {
            var subsystem = Get(name);
            var changed = false;

            lock (Sync)
            {
                subsystem.LastError = reason;

                if (subsystem.State != SubsystemState.Failed)
                {
                    subsystem.State = SubsystemState.Failed;
                    changed = true;
                }
            }

            if (changed) Raise(subsystem);
        }

        public static Subsystem[] Snapshot()
        {
            lock (Sync)
            {
                var result = new Subsystem[Subsystems.Count];
                Subsystems.Values.CopyTo(result, 0);
                return result;
            }
        }

        /// <summary>One line per subsystem, for the overlay and the launcher.</summary>
        public static string Describe()
        {
            var all = Snapshot();
            if (all.Length == 0) return "no subsystems";

            var builder = new StringBuilder(all.Length * 48);

            for (var i = 0; i < all.Length; i++)
            {
                if (i > 0) builder.Append('\n');
                builder.Append(all[i].Name).Append(": ").Append(all[i].State);

                if (!string.IsNullOrEmpty(all[i].LastError))
                {
                    builder.Append(" (").Append(all[i].LastError).Append(')');
                }
            }

            return builder.ToString();
        }

        private static void Raise(Subsystem subsystem)
        {
            var handler = SubsystemChanged;
            if (handler == null) return;

            try
            {
                handler(subsystem);
            }
            catch
            {
                // Observers are not allowed to break the guard.
            }
        }

        // ------------------------------------------------------------- guarded calls

        /// <summary>
        /// Runs an action and swallows a failure, recording it against a
        /// subsystem. Returns false when the action threw.
        /// </summary>
        public static bool Guard(string subsystem, string operation, Action action)
        {
            if (action == null) return true;

            try
            {
                action();
                return true;
            }
            catch (Exception error)
            {
                Record(subsystem, operation, error);
                return false;
            }
        }

        /// <summary>Runs a function, returning a fallback when it throws.</summary>
        public static T Attempt<T>(string subsystem, string operation, Func<T> function, T fallback)
        {
            if (function == null) return fallback;

            try
            {
                return function();
            }
            catch (Exception error)
            {
                Record(subsystem, operation, error);
                return fallback;
            }
        }

        /// <summary>Runs a predicate, treating a throw as false.</summary>
        public static bool Attempt(string subsystem, string operation, Func<bool> function)
        {
            return Attempt(subsystem, operation, function, false);
        }

        /// <summary>
        /// Records a failure and decides whether the subsystem should now be
        /// disabled.
        ///
        /// The policy is deliberately asymmetric: an exception marks the subsystem
        /// degraded immediately, but disabling takes
        /// <see cref="FailureThreshold"/> failures. A device lost on an alt-tab is
        /// one failure, and it must not cost the user their audio for the rest of
        /// the session.
        /// </summary>
        public static void Record(string subsystem, string operation, Exception error)
        {
            var name = string.IsNullOrEmpty(subsystem) ? "engine" : subsystem;
            var reason = (operation ?? "operation") + ": " +
                         (error == null
                             ? "unknown error"
                             : error.GetType().Name + ": " + error.Message);

            EngineLog.Exception(name + "." + (operation ?? "operation"), error);

            var target = Get(name);
            var disabled = false;

            lock (Sync)
            {
                target.FailureCount++;
                target.LastError = reason;

                if (target.FailureCount >= FailureThreshold)
                {
                    target.State = SubsystemState.Failed;
                    disabled = true;
                }
                else if (target.State != SubsystemState.Failed)
                {
                    target.State = SubsystemState.Degraded;
                }
            }

            if (disabled)
            {
                EngineLog.Error(name, "Subsystem disabled after " + target.FailureCount +
                                      " failures; the engine continues without it. Last: " + reason);
            }

            Raise(target);
        }

        /// <summary>Clears the failure count once a subsystem works again.</summary>
        public static void Recovered(string subsystem)
        {
            var target = Get(subsystem);

            lock (Sync)
            {
                if (target.State == SubsystemState.Failed) return;

                target.FailureCount = 0;
                target.LastError = null;
                target.State = SubsystemState.Running;
            }

            Raise(target);
        }

        // ------------------------------------------------------------- threads

        /// <summary>
        /// Wraps a thread body so an exception in a worker is reported instead of
        /// silently killing the thread.
        ///
        /// A dead audio thread used to look like "the sound stopped and nothing was
        /// logged", which is the hardest kind of bug to diagnose from a report.
        /// </summary>
        public static ThreadStart GuardThread(string name, ThreadStart body)
        {
            if (body == null) throw new ArgumentNullException("body");

            return delegate
            {
                try
                {
                    body();
                }
                catch (ThreadAbortException)
                {
                    // Normal shutdown; not a failure.
#if NETFRAMEWORK
                    Thread.ResetAbort();
#endif
                }
                catch (Exception error)
                {
                    Record(name, "thread", error);
                }
            };
        }

        // ------------------------------------------------------------- crash capture

        /// <summary>
        /// Installs the last-resort handlers. Safe to call more than once.
        /// </summary>
        /// <param name="directory">
        /// Where crash reports go. Defaults to <c>Storage/Crashes</c>.
        /// </param>
        public static void InstallCrashHandler(string directory)
        {
            lock (Sync)
            {
                if (crashHandlerInstalled) return;
                crashHandlerInstalled = true;

                crashDirectory = string.IsNullOrEmpty(directory)
                    ? Path.Combine(PortablePaths.DataDirectory, "Crashes")
                    : directory;
            }

            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;

            NativeFaultTracer.Install(crashDirectory);
        }

        /// <summary>
        /// Logs an exception the moment it is thrown, before anything has had a
        /// chance to handle it.
        ///
        /// This exists because the exceptions that matter most are the ones that never
        /// reach a handler. An access violation, a corrupted-state exception or a fault
        /// raised inside a native callback can terminate the process without ever
        /// touching <see cref="OnUnhandledException"/> - Windows shows its own error
        /// dialog and the log ends mid-sentence, which tells you nothing. First chance
        /// is the last point at which the stack is still intact and still managed.
        ///
        /// The trade-off is volume: this fires for every exception, including ones
        /// that are thrown and caught on purpose. So the first few are reported at
        /// warning level with a full stack, and after that only the serious types keep
        /// appearing - a log full of expected exceptions is as useless as an empty one.
        /// </summary>
        private static void OnFirstChanceException(object sender, FirstChanceExceptionEventArgs e)
        {
            if (inFirstChance) return;

            var error = e.Exception;
            if (error == null) return;

            inFirstChance = true;

            try
            {
                var serious = IsSerious(error);

                if (!serious && firstChanceReports >= FirstChanceReportLimit) return;

                firstChanceReports++;

                var text = "First chance: " + error.GetType().Name + ": " + error.Message +
                           (error.StackTrace == null ? "" : Environment.NewLine + error.StackTrace);

                if (serious)
                {
                    // Always visible: these are the ones that kill a process.
                    EngineLog.Error("crash", text);
                }
                else if (firstChanceReports <= 5)
                {
                    EngineLog.Warn("crash", text);
                }
                else
                {
                    EngineLog.Verbose("crash", text);
                }
            }
            catch
            {
                // A diagnostic must never be the reason a program dies.
            }
            finally
            {
                inFirstChance = false;
            }
        }

        /// <summary>
        /// True for exception types that mean the process is in trouble, as opposed to
        /// the ordinary exceptions a program throws and catches as control flow.
        /// </summary>
        private static bool IsSerious(Exception error)
        {
            return error is AccessViolationException
                   || error is SEHException
                   || error is StackOverflowException
                   || error is OutOfMemoryException
                   || error is TypeInitializationException
                   || error is BadImageFormatException;
        }

        /// <summary>Marks the process as exiting, so a normal shutdown files no report.</summary>
        public static void BeginShutdown()
        {
            shuttingDown = true;
        }

        private static void OnProcessExit(object sender, EventArgs e)
        {
            shuttingDown = true;
        }

        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (shuttingDown) return;

            var error = e.ExceptionObject as Exception;

            try
            {
                EngineLog.Fatal("crash", "Unhandled exception" + (e.IsTerminating ? " (terminating)" : ""), error);
            }
            catch
            {
            }

            WriteCrashReport(error, "unhandled");
        }

        /// <summary>
        /// Writes a crash report next to the logs and returns its path, or null
        /// when even that failed.
        ///
        /// Separate from the log file on purpose: the log may be the thing that is
        /// broken (disk full, file locked), and the report also carries the
        /// subsystem table, which is what actually explains a partial failure.
        /// </summary>
        public static string WriteCrashReport(Exception error, string reason)
        {
            try
            {
                var directory = crashDirectory;

                if (string.IsNullOrEmpty(directory))
                {
                    directory = Path.Combine(PortablePaths.DataDirectory, "Crashes");
                }

                Directory.CreateDirectory(directory);

                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                var path = Path.Combine(directory, "crash-" + stamp + ".txt");

                var builder = new StringBuilder(4096);

                builder.AppendLine("DPT.Engine crash report");
                builder.AppendLine("Reason   : " + (reason ?? "unknown"));
                builder.AppendLine("Time     : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                builder.AppendLine("Version  : " + Branding.Title);
                builder.AppendLine("OS       : " + Environment.OSVersion);
                builder.AppendLine("CLR      : " + Environment.Version + ", " +
                                   (IntPtr.Size == 8 ? "64-bit" : "32-bit"));
                builder.AppendLine("Command  : " + Environment.CommandLine);
                builder.AppendLine("Directory: " + PortablePaths.ExecutableDirectory);
                builder.AppendLine();

                builder.AppendLine("--- subsystems ---");
                builder.AppendLine(Describe());
                builder.AppendLine();

                builder.AppendLine("--- exception ---");

                if (error == null)
                {
                    builder.AppendLine("(none reported)");
                }
                else
                {
                    var current = error;
                    var depth = 0;

                    while (current != null && depth < 8)
                    {
                        builder.AppendLine(current.GetType().FullName + ": " + current.Message);

                        if (!string.IsNullOrEmpty(current.StackTrace))
                        {
                            builder.AppendLine(current.StackTrace);
                        }

                        current = current.InnerException;
                        depth++;

                        if (current != null) builder.AppendLine("--- inner ---");
                    }
                }

                builder.AppendLine();
                builder.AppendLine("--- last log lines ---");
                builder.AppendLine(EngineLog.RecentText(200));

                File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
                return path;
            }
            catch
            {
                return null;
            }
        }
    }
}
