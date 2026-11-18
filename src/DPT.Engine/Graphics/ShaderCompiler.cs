//
// DPT.Engine - HLSL compilation.
// Copyright (C) 2026 DartPower Team. GPLv2.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

using DPT.Engine.Shared.Diagnostics;
using DPT.Engine.Shared;

namespace DPT.Engine.Graphics
{
    /// <summary>
    /// Compiles HLSL to Direct3D 9 shader bytecode.
    ///
    /// The original engine shipped precompiled Xbox 360 shaders (.vso.xbox /
    /// .pso.xbox), which a PC cannot use: different bytecode format, different
    /// shader model. So the port keeps its shaders as source and compiles them
    /// here.
    ///
    /// Compilation happens once per process and is then cached on disk, because
    /// D3DCompile is not cheap (tens of milliseconds per shader) and a demo that
    /// restarts during a competition should not pay that every time.
    ///
    /// The compiler itself is d3dcompiler_*.dll, tried newest first. Vista and
    /// later have one built in; XP has one as soon as any D3D9 game has been
    /// installed, which is the same precondition D3D9 itself has. If no compiler
    /// is present at all the engine reports it as a failed subsystem and falls
    /// back to the fixed-function path rather than refusing to start.
    /// </summary>
    internal static class ShaderCompiler
    {
        private const uint CompileEnableBackwardsCompatibility = 0x1000;
        private const uint CompileOptimizationLevel3 = 0x8000;
        private const uint CompileDebug = 0x0001;

        /// <summary>
        /// Compiler DLLs, newest first. The version numbers are the ones Windows
        /// ships; 43 is the last one that supports the D3D9-era shader models
        /// without a separate redistributable.
        /// </summary>
        private static readonly string[] CompilerLibraries =
        {
            "d3dcompiler_47.dll",
            "d3dcompiler_46.dll",
            "d3dcompiler_43.dll",
            "d3dcompiler_42.dll",
            "d3dcompiler_41.dll",
            "d3dcompiler_40.dll",
            "d3dcompiler_39.dll",
            "d3dcompiler_38.dll",
            "d3dcompiler_37.dll",
            "d3dcompiler_36.dll",
            "d3dcompiler_35.dll",
            "d3dcompiler_34.dll",
            "d3dcompiler_33.dll"
        };

        private static readonly object Sync = new object();
        private static readonly Dictionary<string, byte[]> MemoryCache =
            new Dictionary<string, byte[]>(StringComparer.Ordinal);

        private static CompileDelegate compile;
        private static bool compilerResolved;
        private static string compilerName;

        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        private delegate int CompileDelegate(IntPtr source, IntPtr sourceSize, string sourceName,
            IntPtr defines, IntPtr include, string entryPoint, string target, uint flags1, uint flags2,
            out IntPtr code, out IntPtr errors);

        /// <summary>True when a compiler DLL was found and the entry point resolved.</summary>
        internal static bool IsAvailable
        {
            get
            {
                lock (Sync)
                {
                    Resolve();
                    return compile != null;
                }
            }
        }

        internal static string CompilerName
        {
            get
            {
                lock (Sync)
                {
                    Resolve();
                    return compilerName;
                }
            }
        }

        private static void Resolve()
        {
            if (compilerResolved) return;
            compilerResolved = true;

            for (var i = 0; i < CompilerLibraries.Length; i++)
            {
                var name = CompilerLibraries[i];

                try
                {
                    var module = NativeMethods_LoadLibrary(name);
                    if (module == IntPtr.Zero) continue;

                    var address = NativeMethods_GetProcAddress(module, "D3DCompile");
                    if (address == IntPtr.Zero) continue;

                    compile = (CompileDelegate)Marshal.GetDelegateForFunctionPointer(
                        address, typeof(CompileDelegate));
                    compilerName = name;

                    EngineLog.Info("shader", "Using " + name + " for HLSL compilation.");
                    return;
                }
                catch (Exception error)
                {
                    EngineLog.Verbose("shader", "Compiler " + name + " unusable: " + error.Message);
                }
            }

            EngineLog.Warn("shader",
                "No d3dcompiler_*.dll found; shaders cannot be compiled and the engine " +
                "will use the fixed-function fallback.");
            FaultGuard.MarkFailed("shader", "no d3dcompiler available");
        }

        // The kernel32 imports live here rather than in Win32.NativeMethods because
        // only the shader compiler needs them and they are a fixed pair.
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "LoadLibraryW")]
        private static extern IntPtr NativeMethods_LoadLibrary(string fileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, EntryPoint = "GetProcAddress")]
        private static extern IntPtr NativeMethods_GetProcAddress(IntPtr module, string name);

        /// <summary>
        /// Compiles <paramref name="source"/> and returns the bytecode.
        ///
        /// Results are cached twice: in memory for the process, and on disk under
        /// <c>Storage/ShaderCache</c> keyed by a hash of the source, entry point
        /// and target. Editing a shader in the editor therefore invalidates
        /// exactly one cache entry.
        /// </summary>
        internal static byte[] Compile(string name, string source, string entryPoint, string target)
        {
            if (string.IsNullOrEmpty(source)) throw new ArgumentNullException("source");
            if (string.IsNullOrEmpty(entryPoint)) throw new ArgumentNullException("entryPoint");
            if (string.IsNullOrEmpty(target)) throw new ArgumentNullException("target");

            var key = name + "|" + entryPoint + "|" + target + "|" + source.GetHashCode().ToString("X8") +
                      "|" + source.Length.ToString("X8");

            lock (Sync)
            {
                byte[] cached;
                if (MemoryCache.TryGetValue(key, out cached)) return cached;
            }

            var diskPath = CachePath(key);

            var fromDisk = TryReadCache(diskPath);
            if (fromDisk != null)
            {
                lock (Sync) { MemoryCache[key] = fromDisk; }
                return fromDisk;
            }

            var bytecode = CompileNow(name, source, entryPoint, target);

            lock (Sync) { MemoryCache[key] = bytecode; }
            TryWriteCache(diskPath, bytecode);

            return bytecode;
        }

        private static byte[] CompileNow(string name, string source, string entryPoint, string target)
        {
            lock (Sync) { Resolve(); }

            if (compile == null)
            {
                throw new InvalidOperationException(
                    "No HLSL compiler is available (d3dcompiler_*.dll not found).");
            }

            var sourceBytes = Encoding.ASCII.GetBytes(source);
            var sourceHandle = GCHandle.Alloc(sourceBytes, GCHandleType.Pinned);

            IntPtr code = IntPtr.Zero;
            IntPtr errors = IntPtr.Zero;

            try
            {
                var flags = CompileEnableBackwardsCompatibility | CompileOptimizationLevel3;

                var result = compile(
                    sourceHandle.AddrOfPinnedObject(),
                    new IntPtr(sourceBytes.Length),
                    name,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    entryPoint,
                    target,
                    flags,
                    0,
                    out code,
                    out errors);

                if (result != 0)
                {
                    var message = "shader compilation failed";

                    if (errors != IntPtr.Zero)
                    {
                        using (var blob = new D3DBlob(errors))
                        {
                            message = blob.ToText();
                        }
                    }

                    EngineLog.Error("shader", "Compiling " + name + " (" + entryPoint + ", " + target +
                                              ") failed: 0x" + result.ToString("X8") + Environment.NewLine + message);

                    throw new InvalidOperationException(
                        "Shader '" + name + "' (" + entryPoint + ", " + target + ") failed to compile: " + message);
                }

                if (code == IntPtr.Zero)
                {
                    throw new InvalidOperationException("Shader '" + name + "' compiled to nothing.");
                }

                using (var blob = new D3DBlob(code))
                {
                    var bytecode = blob.ToArray();
                    code = IntPtr.Zero; // owned by the blob wrapper now
                    return bytecode;
                }
            }
            finally
            {
                if (errors != IntPtr.Zero)
                {
                    try
                    {
                        using (var blob = new D3DBlob(errors)) { }
                    }
                    catch
                    {
                    }
                }

                sourceHandle.Free();
            }
        }

        private static string CachePath(string key)
        {
            var hash = Hash(key);
            return Path.Combine(PortablePaths.DataDirectory, "ShaderCache", hash + ".dptshader");
        }

        private static byte[] TryReadCache(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;

                var bytes = File.ReadAllBytes(path);
                return bytes.Length < 16 ? null : bytes;
            }
            catch
            {
                return null;
            }
        }

        private static void TryWriteCache(string path, byte[] bytecode)
        {
            try
            {
                PortablePaths.EnsureParent(path);
                File.WriteAllBytes(path, bytecode);
            }
            catch (Exception error)
            {
                // A read-only install directory just means no disk cache.
                EngineLog.Verbose("shader", "Shader cache not written: " + error.Message);
            }
        }

        /// <summary>
        /// FNV-1a over the key. Not a security hash: it only has to avoid
        /// collisions between the engine's own shader variants on disk.
        /// </summary>
        private static string Hash(string text)
        {
            var hash = 2166136261u;

            for (var i = 0; i < text.Length; i++)
            {
                hash ^= text[i];
                hash *= 16777619u;
            }

            return hash.ToString("X8");
        }

        internal static void ClearCache()
        {
            lock (Sync) { MemoryCache.Clear(); }

            try
            {
                var directory = Path.Combine(PortalSafeData(), "ShaderCache");
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
            catch
            {
            }
        }

        private static string PortalSafeData()
        {
            return PortablePaths.DataDirectory;
        }
    }
}
