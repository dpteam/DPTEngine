# Building

Everything builds with the .NET SDK and plain `dotnet` commands. There is no MSBuild-only step,
no script to run first, and no generated source.

## Prerequisites

| Requirement | Notes |
| --- | --- |
| .NET SDK | 6.0 or newer. Verified with 10.0.114 |
| NuGet access (first build only) | Restores `Microsoft.NETFramework.ReferenceAssemblies` 1.0.3 |
| Windows | The runtime and the tests need Windows and a Direct3D 9 driver |

The projects target `net40` so the output runs on .NET Framework 4.0, which the SDK cannot
build against out of the box. `Microsoft.NETFramework.ReferenceAssemblies` supplies the
reference assemblies; it is a `PrivateAssets="all"` package reference in every project that
targets net40, so it is never shipped and never becomes a transitive dependency.

`DPT.Engine.Shared` and `DPT.Engine.Master` also target `net9.0`. A plain `dotnet build` builds
both target frameworks of those projects.

## Build

```bat
dotnet build src/DPT.Engine/DPT.Engine.csproj
```

Output: `src\DPT.Engine\bin\Debug\net40\dptengine.exe`, next to `DPT.Engine.Shared.dll`. The
executable is a normal framework-dependent .NET Framework 4.0 program: it needs the .NET
Framework 4.0 runtime installed on the machine that runs it, not the SDK.

Do not build `DPTEngine.slnx` yet. It lists `src/DPT.Engine.Editor` and
`src/DPT.Engine.Launcher`, which do not exist, so the solution build fails with MSB3202. Build
the project files directly until those projects land.

## Tests

```bat
dotnet build tests/DPT.Engine.Tests/DPT.Engine.Tests.csproj
.\tests\DPT.Engine.Tests\bin\Debug\net40\dptengine-tests.exe
```

Expected tail of the output:

```
passed 38, failed 0, skipped 0
```

The runner is a hand-written reflection loop (`TestFramework.cs`), not xUnit or NUnit: it has
to run on .NET Framework 4.0 against the shipping assembly with no runner host. The test
assembly is `x86` on purpose, because the 32-bit struct layouts and the COM vtable calling
convention are what the tests exist to verify.

A single test or a group can be selected by a substring of `Type.Method`:

```bat
.\tests\DPT.Engine.Tests\bin\Debug\net40\dptengine-tests.exe Mathematics
.\tests\DPT.Engine.Tests\bin\Debug\net40\dptengine-tests.exe ClearAndReadBack
```

Tests marked `[RequiresGraphics]` create a hidden window and a real device. On a machine with no
Direct3D 9 driver they are reported as `SKIP`, not as failures, and the exit code stays 0.

## Headless verification

```bat
.\src\DPT.Engine\bin\Debug\net40\dptengine.exe --headless
```

This is the pre-commit check. It builds the template project, saves and reloads it to prove the
format round-trips, opens a hidden 512x384 window and a device, renders five frames spread
across the timeline, and checks each one:

* frame 0 is read back and must have non-trivial average brightness and more than eight
  distinct colours;
* every sampled frame must have at least one active effect and must not throw;
* the checked frame is written as a BMP into `Screenshots\` in the data directory so a human
  can look at what the machine approved.

Expected output ends with `headless verification passed` and exit code 0. Pixels are read once
per run, not once per frame; the reason is in `docs/TROUBLESHOOTING.md`.

## Self-test

```bat
dotnet build src/DPT.Engine.SelfTest/DPT.Engine.SelfTest.csproj
.\src\DPT.Engine.SelfTest\bin\Debug\net40\dptengine-selftest.exe
```

Eight checks - splines, JSON, mesh, scene graph, shader path, render, animation, readback - that
run against a real device with a hidden 320x240 window and assert on pixels. Where the unit
tests prove the pieces, this proves they work together. It writes `selftest.log` into the data
directory.

## Release build

```bat
dotnet build src/DPT.Engine/DPT.Engine.csproj -c Release
dotnet build tests/DPT.Engine.Tests/DPT.Engine.Tests.csproj -c Release
```

Output lands in `bin\Release\net40\`. To produce a copy you can zip and hand out:

```bat
dotnet publish src/DPT.Engine/DPT.Engine.csproj -c Release -o dist
```

`dist\` is gitignored. Ship the whole folder: `dptengine.exe` needs `DPT.Engine.Shared.dll`. If
you want a single file, build `src/DPT.Engine.Master` instead - it links the shared sources in -
or add the shared sources to the runtime the same way. The runtime does not do that yet, because
the shared assembly is also what the tests and the editor host will reference.

There is no code signing, no installer and no packaging script.

## What the build produces

| Project | Assembly | Kind |
| --- | --- | --- |
| `src/DPT.Engine.Shared` | `DPT.Engine.Shared.dll` | Library, net40 + net9.0 |
| `src/DPT.Engine` | `dptengine.exe` | Runtime, net40 |
| `src/DPT.Engine.SelfTest` | `dptengine-selftest.exe` | Self-test, net40 |
| `src/DPT.Engine.Master` | `dptengine-master.exe` | Master server, net40 + net9.0 |
| `tests/DPT.Engine.Tests` | `dptengine-tests.exe` | Tests, net40, x86 |

`DPT.Engine` exposes its internals to `dptengine-tests` and `dptengine-selftest` through
`InternalsVisibleTo`, because both drive the device and the resource wrappers directly. Nothing
else should: bypassing the renderer's state tracking is how a driver-level bug becomes a
mystery.

## Build problems

**`error MSB3202` about `DPT.Engine.Editor.csproj`.** You built the solution file. Build the
individual projects instead; the editor and launcher projects are not in the tree yet.

**`error NETSDK1045` or missing `System.Drawing` on a non-Windows machine.** The net40 targeting
packs and the Win32 interop assume Windows. Build and run on Windows.

**Restore fails offline.** The first build needs the reference-assemblies package. Once it is in
the NuGet cache, later builds work without a network.

**Tests report `SKIP` for every graphics test.** The machine has no Direct3D 9 device: a bare
VM, a session over RDP without hardware acceleration, or a container. The tests are correct;
the machine cannot run them.
