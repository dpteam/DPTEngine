# DPT.Engine

DPT.Engine is a demo and game engine for Windows, ported off XNA onto .NET Framework 4.0,
Win32 and Direct3D 9 so that it runs on Windows XP SP3 and everything newer. A project is a
timeline of effects - clear, 2D layers, 3D scenes, render-to-texture captures, blur,
feedback, contrast, camera shake, particles - played back at a fixed frame rate into an
off-screen buffer and letterboxed to the window, which is the model the original Addict2
engine used and the one a demoscene production needs. It is written in plain C# with hand
written COM interop, no third-party runtime dependencies, and every failure is recorded and
survived rather than thrown: a missing sound device, a shader that will not compile or one
corrupt object in a project disables that subsystem and the rest keeps running.

* Repository: <https://github.com/dpteam/DPTEngine>
* Licence: GNU General Public License version 2 (see `LICENSE`)
* Version: 1.0.0

## Features

| Area | What is there |
| --- | --- |
| Graphics | Direct3D 9 device via COM vtables, hardware/mixed/software/reference fallback, cached delegates and render state, device-loss recovery |
| Shaders | HLSL compiled at start-up with `d3dcompiler_*.dll`, cached in memory and on disk, fixed-function fallback when no compiler is available |
| Scene | Mesh primitives (box, sphere, plane, custom), parent-id hierarchy, materials with blending/shading/depth flags, fog, directional light |
| Animation | 1D splines with step, linear, Hermite, Catmull-Rom, B-spline, sine, saw and square interpolation, sampled at seconds |
| Timeline | Nine effect types, each active over a frame span with an animatable rectangle in an 800x600 virtual space |
| Project | JSON file with embedded textures, scenes, animations and the timeline; hand editable and diffable in git |
| Playback | Fixed-step loop at the project frame rate, off-screen project buffer, letterboxed presentation, F1/F2/F11/F12 keys |
| Diagnostics | `System.Diagnostics.Trace` logging with file rotation and an in-memory ring, subsystem state tracking, crash reports |
| Tooling | 38 unit and interop tests (the graphics ones run against a real device), a headless verification pass, a self-test executable |

## Requirements

| | |
| --- | --- |
| Operating system | Windows XP SP3 or later |
| Runtime | .NET Framework 4.0 |
| Graphics | A Direct3D 9 capable driver, vertex and pixel shader model 2.0 |
| Shader compiler | Any `d3dcompiler_*.dll` (33 through 47) reachable by the loader; without one the engine still runs with fixed-function rendering |
| Build | .NET SDK 6.0 or newer, plus the `Microsoft.NETFramework.ReferenceAssemblies` package, which the projects reference and NuGet restores |

## Quick start

```bat
dotnet build src/DPT.Engine/DPT.Engine.csproj
.\src\DPT.Engine\bin\Debug\net40\dptengine.exe
```

That plays the built-in template. To play a project file:

```bat
.\src\DPT.Engine\bin\Debug\net40\dptengine.exe path\to\demo.dptproj
```

Command line:

| Argument | Meaning |
| --- | --- |
| `[project]` | Project file to play. Missing or broken falls back to the built-in template |
| `--headless` | Run the verification pass instead of opening a window |
| `--log-level=LEVEL` | `verbose`, `info`, `warning` or `error` (default `info`) |
| `--help`, `-h`, `/?` | Print usage |

## The template demo

`TemplateProject.Create()` builds the demo in code, so a fresh checkout always has something
to run. It is 1024x768, 900 frames at 100 fps (nine seconds) and contains:

* a 60x60 unit floor with a tiled 128x128 blue grid texture and fog from 6 to 34 units;
* nine cubes in two receding rows, bobbing, spinning and pulsing their blue channel, all
  driven by one scene animation whose tracks are named `nodeId.property`;
* a disc particle emitter in the middle of the corridor, 600 particles, blue sparks drifting
  up with gravity;
* a camera dolly down the corridor with a gentle FOV pulse, from `eyeZ` -12 to -4;
* a feedback trail over the second half of the timeline;
* a short camera shake at frames 600 to 640;
* a full-length clear, a full-length render-to-texture capture into slot 0 and a full-length
  particle step, so every effect family runs on every frame.

Two textures are generated procedurally: the grid and a soft radial glow for particles.

## Controls

| Key | Action |
| --- | --- |
| Esc | Quit |
| F1 | Toggle the overlay (title, FPS, frame number, device, key list, failing subsystems) |
| F2 | Restart the timeline and reset all particle emitters |
| F11 | Toggle borderless full screen. Alt+Enter does the same |
| F12 | Save a screenshot as BMP into the data directory under `Screenshots\` |

Full screen is a borderless window covering the monitor, not a display mode change: the
device stays windowed and the swap chain is resized to the client area.

## Project structure

```
DPTEngine/
  DPTEngine.slnx                 solution file (see Status: lists projects not yet present)
  LICENSE                        GPLv2
  README.md  README.ru.md        this file, and its Russian translation
  CHANGELOG.md
  PROMPT.md                      the original brief
  docs/
    ARCHITECTURE.md              layering, conventions, renderer and timeline model
    BUILDING.md                  build, test and release commands
    PROJECT_FORMAT.md            .dptproj reference
    LOGGING.md                   Trace-based logging
    TROUBLESHOOTING.md           the real failures and their fixes
  src/
    DPT.Engine.Shared/           protocol, JSON, paths, logging, fault guard, branding
    DPT.Engine/                  the runtime: math, Win32, D3D9, scenes, rendering,
                                 project format, timeline playback
    DPT.Engine.SelfTest/         draws a frame and asserts on pixels
    DPT.Engine.Master/           master directory server, its own executable
  tests/
    DPT.Engine.Tests/            unit and interop tests, net40, x86
  refs/                          legacy Addict2 sources, read-only reference (gitignored)
```

## Documentation

* [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) - how the layers fit together
* [docs/BUILDING.md](docs/BUILDING.md) - build, test, release
* [docs/PROJECT_FORMAT.md](docs/PROJECT_FORMAT.md) - the `.dptproj` format, field by field
* [docs/LOGGING.md](docs/LOGGING.md) - log levels, rotation, sinks
* [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md) - device loss, readback, shader and driver problems
* [CHANGELOG.md](CHANGELOG.md)

## Licence

DPT.Engine is free software under the GNU General Public License version 2. You may
redistribute it and/or modify it under those terms; there is no warranty. The full text is
in `LICENSE`.

The engine is a port of the Addict2 demo engine by Alexander Kiselev (2004). Those sources
are kept under `refs/legacy-addict2-xna` for reading only, are not part of the build, and
carry their own terms.

## Status

Working today:

* Direct3D 9 device, interop, resource wrappers, shader compilation with disk cache,
  fixed-function fallback, device loss and recovery, window resize.
* Scene graph, meshes, materials, fog, two-pass rendering with transparent sorting,
  billboarded particles.
* Splines and scene/camera animation.
* All nine timeline effects, the JSON project format with embedded textures, save and load
  with normalisation and repair.
* Timeline playback into an off-screen buffer with letterboxing, the overlay, screenshots,
  the fixed-step loop.
* Logging, fault guard, subsystem reporting, crash reports.
* 38 tests, all green, and the headless verification pass, also green.

Not there yet, stated plainly:

* **No audio.** There is no sound device, no music player and no audio timeline events. The
  original's FMOD/BASS path was not ported.
* **No networking in the runtime.** The wire protocol, the session and project records and a
  blocking client live in `DPT.Engine.Shared`, and the master server is its own project, but
  the runtime never opens a socket: there is no session browser, no hosting and no joining.
* **No editor yet.** The editor is planned as a web page (HTML/CSS/JS with a timeline, an
  object list and a shader panel) served by a small host executable. Neither the page nor the
  host exists in this tree, and neither does the launcher. Until then, projects are written by
  hand in the format described in `docs/PROJECT_FORMAT.md`.
* `DPTEngine.slnx` lists `DPT.Engine.Editor` and `DPT.Engine.Launcher`, which are not present,
  so building the solution file fails. Build the project files directly, as in
  `docs/BUILDING.md`.
* Screenshots and pixel readback work **once per device**, because a second readback wedges
  the driver this was developed against. See `docs/TROUBLESHOOTING.md`.
* Colours do not survive a save/load round trip: the writer emits `#RRGGBBAA` and the reader
  expects `#AARRGGBB`. See the note in `docs/PROJECT_FORMAT.md`.
* `display3d` stores a camera index but only one camera per scene is used.
