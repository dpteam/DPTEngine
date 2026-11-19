//
// DPT.Engine - CPU-level fault capture.
// Copyright (C) 2026 DartPower Team. GPLv2.
//

using System;
using System.IO;
using System.Runtime.InteropServices;

namespace DPT.Engine.Shared.Diagnostics
{
    /// <summary>
    /// Records the exact address of a native fault, before anything else sees it.
    ///
    /// A managed program that dies with 0xC0000005 or 0x80131506 leaves almost nothing
    /// behind. The CLR's unhandled-exception hook never runs - the fault happens below
    /// it - and Windows shows a dialog whose only content is the code. The log simply
    /// stops. That is the worst possible position to debug from: no stack, no module,
    /// no idea whether the bug is in this code or in a driver.
    ///
    /// A vectored exception handler runs earlier than every other handler, including
    /// the CLR's, and is given the raw <c>EXCEPTION_POINTERS</c>. From that it can read
    /// the faulting instruction pointer and resolve it to a module and an offset. One
    /// line - <c>d3d9.dll+0x1A4C2</c> or <c>dptengine.exe+0x9F31</c> - turns an
    /// unfalsifiable guess into a decision: fix the code, or work around the driver.
    ///
    /// The handler never handles anything. It records and returns
    /// <c>EXCEPTION_CONTINUE_SEARCH</c>, so normal processing is untouched; if the
    /// fault is fatal the process still dies exactly as it would have.
    /// </summary>
    internal static class NativeFaultTracer
    {
        private const int ExceptionContinueSearch = 0;
        private const uint ExceptionAccessViolation = 0xC0000005;
        private const uint ExceptionIllegalInstruction = 0xC000001D;
        private const uint ExceptionInPageError = 0xC0000006;
        private const uint ExceptionStackOverflow = 0xC00000FD;
        private const uint ExceptionBreakpoint = 0x80000003;

        private const uint GetModuleHandleFromAddress = 0x00000004;
        private const uint GetModuleHandleUnchangedRefCount = 0x00000002;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int VectoredHandler(IntPtr exceptionPointers);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr AddVectoredExceptionHandler(uint first, VectoredHandler handler);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetModuleHandleExW(uint flags, IntPtr moduleName, out IntPtr module);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetModuleFileNameW(IntPtr module, [Out] char[] name, uint size);

        /// <summary>Rooted for the life of the process: the OS holds only the function pointer.</summary>
        private static VectoredHandler handler;

        private static IntPtr registration;

        private static string reportPath;

        private static bool installed;

        /// <summary>
        /// Installs the tracer. Safe to call more than once and safe to call on a
        /// platform without kernel32 - it simply does nothing there.
        /// </summary>
        internal static void Install(string directory)
        {
            if (installed) return;

            installed = true;

            try
            {
                reportPath = string.IsNullOrEmpty(directory)
                    ? Path.Combine(PortablePaths.DataDirectory, "native-fault.txt")
                    : Path.Combine(directory, "native-fault.txt");

                handler = OnException;
                registration = AddVectoredExceptionHandler(1, handler);

                if (registration == IntPtr.Zero)
                {
                    EngineLog.Warn("fault", "Could not install the native fault tracer.");
                }
            }
            catch (Exception error)
            {
                // A missing kernel32 (another OS) or a locked directory must not stop
                // the program from starting.
                EngineLog.Warn("fault", "Native fault tracer unavailable: " + error.Message);
            }
        }

        private static int OnException(IntPtr exceptionPointers)
        {
            try
            {
                if (exceptionPointers == IntPtr.Zero) return ExceptionContinueSearch;

                // EXCEPTION_POINTERS is { EXCEPTION_RECORD*; CONTEXT* }.
                var record = Marshal.ReadIntPtr(exceptionPointers);

                if (record == IntPtr.Zero) return ExceptionContinueSearch;

                var code = (uint)Marshal.ReadInt32(record);

                if (!IsInteresting(code)) return ExceptionContinueSearch;

                // EXCEPTION_RECORD: Code, Flags, ExceptionRecord*, ExceptionAddress.
                // The address sits at 8 + pointer size on both x86 and x64.
                var address = Marshal.ReadIntPtr(record, 8 + IntPtr.Size);

                var description = Describe(code, record, address);

                Write(description);
            }
            catch
            {
                // A diagnostic that throws inside a fault handler turns a clean crash
                // into an unexplainable one.
            }

            return ExceptionContinueSearch;
        }

        private static bool IsInteresting(uint code)
        {
            return code == ExceptionAccessViolation
                   || code == ExceptionIllegalInstruction
                   || code == ExceptionInPageError
                   || code == ExceptionStackOverflow;
        }

        private static string Describe(uint code, IntPtr record, IntPtr address)
        {
            var text = new System.Text.StringBuilder();

            text.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            text.Append("  ");
            text.Append(Name(code));
            text.Append(" (0x").Append(code.ToString("X8")).Append(")");

            text.Append("  at ").Append(Format(address));

            var parameters = Marshal.ReadInt32(record, 8 + IntPtr.Size * 2);

            if (code == ExceptionAccessViolation && parameters >= 2)
            {
                // ExceptionInformation[0] is 0 for a read, 1 for a write and 8 for an
                // execute; [1] is the address that could not be touched. "write to
                // 0x0000000C" names the bug; "access violation" does not.
                var info = 8 + IntPtr.Size * 2 + (IntPtr.Size == 8 ? 8 : 4);
                var operation = Marshal.ReadIntPtr(record, info).ToInt64();
                var target = Marshal.ReadIntPtr(record, info + IntPtr.Size);

                text.Append("  ").Append(OperationName(operation));
                text.Append(" ").Append(Format(target));
            }

            return text.ToString();
        }

        private static string OperationName(long operation)
        {
            if (operation == 0) return "reading";
            if (operation == 1) return "writing";
            if (operation == 8) return "executing";
            return "accessing";
        }

        /// <summary>
        /// Formats an address as <c>module+0xoffset</c>.
        ///
        /// The offset is the part that means anything: a module base changes on every
        /// run because of ASLR, so a bare address cannot be looked up twice.
        /// </summary>
        private static string Format(IntPtr address)
        {
            if (address == IntPtr.Zero) return "0x00000000";

            IntPtr module;

            try
            {
                if (!GetModuleHandleExW(GetModuleHandleFromAddress | GetModuleHandleUnchangedRefCount,
                        address, out module) || module == IntPtr.Zero)
                {
                    return "0x" + address.ToInt64().ToString("X");
                }

                var buffer = new char[512];
                var length = GetModuleFileNameW(module, buffer, (uint)buffer.Length);

                var name = length > 0
                    ? new string(buffer, 0, (int)length)
                    : "(unknown module)";

                var offset = address.ToInt64() - module.ToInt64();

                return Path.GetFileName(name) + "+0x" + offset.ToString("X");
            }
            catch
            {
                return "0x" + address.ToInt64().ToString("X");
            }
        }

        private static string Name(uint code)
        {
            if (code == ExceptionAccessViolation) return "ACCESS_VIOLATION";
            if (code == ExceptionIllegalInstruction) return "ILLEGAL_INSTRUCTION";
            if (code == ExceptionInPageError) return "IN_PAGE_ERROR";
            if (code == ExceptionStackOverflow) return "STACK_OVERFLOW";
            if (code == ExceptionBreakpoint) return "BREAKPOINT";
            return "EXCEPTION";
        }

        /// <summary>
        /// Appends to the fault file and to the log.
        ///
        /// The file write comes first and is deliberately dumb - a fresh stream, opened
        /// and closed, with no buffering of its own - because this runs in a process
        /// that may already be corrupt, and the logging system allocates.
        /// </summary>
        private static void Write(string text)
        {
            if (!string.IsNullOrEmpty(reportPath))
            {
                try
                {
                    using (var stream = new FileStream(reportPath, FileMode.Append, FileAccess.Write,
                        FileShare.ReadWrite))
                    using (var writer = new StreamWriter(stream))
                    {
                        writer.WriteLine(text);
                        writer.Flush();
                        stream.Flush();
                    }
                }
                catch
                {
                }
            }

            try
            {
                EngineLog.Error("fault", text);
            }
            catch
            {
            }
        }
    }
}
