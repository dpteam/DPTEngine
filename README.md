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
| Import | Reads the original Addict2 `.dmo` binary format, including procedural textures and generated primitives, and converts it to a `.dptproj` |
| Playback | Fixed-step loop at the project frame rate, off-screen project buffer, letterboxed presentation, F1/F2/F11/F12 keys |
| Diagnostics | `System.Diagnostics.Trace` logging with file rotation and an in-memory ring, subsystem state tracking, crash reports |
| Tooling | 40 unit and interop tests (the graphics ones run against a real device), a headless verification pass, a self-test executable |

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
| `--import=FILE.dmo` | Convert an original Addict2 project to `.dptproj` and exit |
| `--export=FILE` | Where the import is written (default: alongside the input) |
| `--log-level=LEVEL` | `verbose`, `info`, `warning` or `error` (default `info`) |
| `--help`, `-h`, `/?` | Print usage |

A `.dmo` file plays directly too, so `dptengine demo.dmo` works without converting first.
`--import` exists for the cases where you want a file the editor can open and edit.

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
  DPTEngine.slnx                 solution file
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
    DPT.Engine.Master/           master directory server and relay
    DPT.Engine.Editor/           editor host: an MSHTML view of the web editor
    DPT.Engine.Launcher/         launcher host: projects and the master catalogue
  tests/
    DPT.Engine.Tests/            unit and interop tests, net40, x86
  resources/                     the application icon
  tools/                         build, verification and icon generation
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
* The Addict2 importer: the original chunked `.dmo` format, all 34 chunk types, the texture
  recipe evaluator (37 filters) and the primitive generators. All nineteen projects that ship
  with the original sources parse, and the chunk-length self-check verifies every struct size
  against real data rather than against a hand-written fixture.
* Timeline playback into an off-screen buffer with letterboxing, the overlay, screenshots,
  the fixed-step loop.
* Logging, fault guard, subsystem reporting, crash reports.
* The editor: an MSHTML host around a web page with a scene tree, a timeline with draggable
  effect bars, an inspector, a schematic preview and three themes.
* The launcher: an MSHTML host listing local projects and, when a master is configured, the
  published catalogue.
* The master server: a session directory, a project catalogue and a relay, with its own
  self-test.
* 40 unit tests, the engine self-test, the headless verification and the master self-test,
  all green. `tools/build.ps1` runs the lot.

Not there yet, stated plainly:

* **No audio.** There is no sound device, no music player and no audio timeline events. The
  original's FMOD/BASS path was not ported.
* **No networking in the runtime.** The protocol, the records, the client and the master all
  exist and the master is tested, but the runtime never opens a socket: there is no in-game
  session browser, no hosting and no joining. The launcher can list a master's catalogue;
  the engine cannot join a session.
* **The editor's preview is a layout schematic**, not a render. It draws effect rectangles,
  their colours and a wireframe box so the user can see framing and timing; the real picture
  comes from pressing Play, which launches the runtime on the project as it stands.
* **No undo, no keyframe editor, no texture import** in the editor.
* Screenshots and pixel readback work **once per device**, because a second readback wedges
  the driver this was developed against. The engine disables readback rather than freezing.
  See `docs/TROUBLESHOOTING.md`.
* The same driver occasionally wedges inside `LockRect` while uploading a large texture
  mip chain. It is the same defect as the readback one - a kernel wait that cannot be timed
  out - and it is environmental, not a bug in the upload code: the identical code path
  succeeds for other projects. See `docs/TROUBLESHOOTING.md`.
* The importer carries over geometry, materials, cameras, splines and the timeline. It does
  not carry over Addict2's deflectors and gravity wells (imported as empty parent nodes that
  still pass their transform to children), nor per-vertex colours, nor the map-transform
  modifier (recorded, not applied).
* `display3d` stores a camera index but only one camera per scene is used.
* `Material.Shading` is stored but the renderer picks its pixel shader from whether a texture
  or environment map is present, so shading modes do not currently change the result.

## Building and verifying

```
dotnet build DPTEngine.slnx
.\tools\build.ps1
```

The first builds everything. The second is the real check: it builds, runs the unit tests,
the engine self-test, the headless verification and the master self-test, and reports one
verdict. `-Pack` additionally stages a runnable distribution under `dist\net40`.