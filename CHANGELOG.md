# Changelog

All notable changes to DPT.Engine. The format follows Keep a Changelog, and the project uses
semantic versioning.

## [1.0.0] - 2026-11-19

The initial port of the Addict2 demo engine from XNA to .NET Framework 4.0, Win32 and Direct3D 9,
and the first version that plays a project end to end.

### Added

**Shared layer (`DPT.Engine.Shared`)**

* `MasterProtocol`: fixed-size session and project directory records, 16-byte header, hex id
  helpers, and a request validator that rejects a malformed header before allocating anything.
* `MasterClient`: bounded, blocking, one request per connection.
* `EngineLog`: `System.Diagnostics.Trace` front end with a file listener, rotation, an in-memory
  ring, extra sinks and a `LineWritten` event.
* `FaultGuard`: guarded calls, subsystem state tracking with a three-failure disable threshold,
  guarded thread bodies, crash reports.
* `JsonValue`: strict JSON DOM that preserves member order.
* `PortablePaths`: portable-first data locations with an `%APPDATA%` fallback.
* `Branding`: name, version, executable names and the three colour themes in one place.

**Runtime (`DPT.Engine`)**

* Mathematics: row-vector `Matrix`, `Vector2/3/4`, `Quaternion`, `ColorRgba`, `MathHelper`.
* Win32: window creation, message pump, keyboard and mouse state, full-screen toggle, timers.
* Graphics: Direct3D 9 through COM vtables with cached delegates; device creation falling back
  from hardware to mixed to software to the reference rasteriser; render-state caching; resource
  wrappers for textures, vertex and index buffers and render targets; device loss and reset.
* HLSL compilation at start-up through whichever `d3dcompiler_*.dll` is available, with an
  in-memory cache, an on-disk cache in `ShaderCache\`, and a fixed-function fallback.
* Scene: mesh primitives (box, sphere, plane, custom), a parent-id hierarchy, materials with
  blend, shading, depth and culling flags, fog, a directional light, cameras with roll.
* Rendering: two-pass opaque/transparent renderer with back-to-front sorting, box-filtered mip
  generation on upload, 16-bit indices when they fit, billboarded particles, a screen-space quad
  renderer with blur, feedback, contrast and texture-copy shaders, a GDI+-backed text overlay.
* Timeline: splines with step, linear, Hermite, Catmull-Rom, B-spline, sine, saw and square
  interpolation; scene animations keyed by `nodeId.property`; camera tracks.
* Project: JSON project file with embedded ARGB textures, scenes, animations and the timeline;
  atomic save; load-time normalisation that repairs resolution, frame rate, duration, textures,
  effect spans, timeline order and dangling parents.
* Effects: `clear`, `layer2d`, `display3d`, `rendertotexture`, `blur`, `feedback`, `contrast`,
  `camerashake`, `particles`, each active over a frame span with an interpolated rectangle in an
  800x600 virtual space.
* Playback: `TimelinePlayer` rendering into an off-screen project buffer and letterboxing it to
  the window; six shared half-resolution post-process slots.
* Host: `EngineApp` with a fixed-step loop at the project frame rate, device-loss recovery,
  resize handling, the overlay, screenshots, and the F1/F2/F11/F12/Esc keys.
* `TemplateProject`: the built-in demo - a grid corridor, nine animated cubes, a particle emitter,
  a camera dolly, a feedback trail and a camera shake.
* `Headless`: a windowless verification pass that round-trips the project, renders frames across
  the timeline, checks brightness and colour variety, and writes the checked frame as a BMP.

**Tooling**

* 38 unit and interop tests running on .NET Framework 4.0 against a real device, with graphics
  tests skipped rather than failed on machines without one.
* `dptengine-selftest`: eight checks that render a real frame and assert on specific pixels.
* Master server project: configuration, session directory, project catalogue and relay
  components, sharing the wire protocol by source link.

### Changed

* The engine is no longer XNA. Everything that touched `Microsoft.Xna.Framework` was rewritten on
  the BCL, `user32`/`gdi32`/`winmm` and `d3d9.dll`.
* `Addict2.*` names became `DPT.Engine.*`, and the executable names follow `dptengine`,
  `dptengine-launcher`, `dptengine-editor`, `dptengine-master`.
* Projects are JSON rather than a packed binary, so the editor can send the same document over the
  wire, a project produces a readable git diff, and a hand-edited file still loads.
* Shaders ship as HLSL source and are compiled at start-up, replacing the Xbox 360 bytecode
  (`.vso.xbox` / `.pso.xbox`) that no PC driver can load.
* Procedural meshes are stored as their recipe rather than baked geometry.
* Textures are embedded in the project instead of referenced by path.
* Logging attaches its listeners in code. There is no `App.config` and no listener declared in
  configuration.
* Readback is a one-shot diagnostic per device instead of a per-frame facility.

### Fixed

Bugs found by the headless run, the self-test and the unit tests, all fixed in this release:

* **Back-buffer readback stalled forever.** Locking the back buffer directly blocks on a vblank
  that never arrives under load on WDDM. Readback now goes through `GetRenderTargetData` into a
  system-memory surface and locks that, with `D3DLOCK_DONOTWAIT` and a deadline.
* **A surface was a render target and a shader input at once.** The post-processes bound their own
  slot as a texture and then copied into it, which crashes on several drivers. Samplers are now
  unbound before a target is bound or copied into.
* **`GetRenderTargetData` refused a bound source.** The frame buffer stayed bound after the final
  blit, so the verification's read failed and some drivers stalled. The texture is now unbound
  after presenting.
* **The timeline captured the wrong image.** Effects captured from the window back buffer instead
  of the project's own frame buffer, so composites showed a previous frame at the wrong
  resolution.
* **The depth-stencil did not follow the render target.** Rendering into the 1024x768 project
  buffer while the 512x384 window depth buffer stayed bound discarded every pixel outside that
  corner. A matching depth surface is now created per target size.
* **Textures were never uploaded.** Every textured surface drew untextured because the player
  never pushed the project's textures into the renderer.
* **Every mesh upload threw.** `Buffer.BlockCopy` rejects arrays of structs, and the fault guard
  silently skipped the draw. `StructsToBytes` now pins the array and uses `Marshal.Copy`.
* **HLSL matrices were transposed.** HLSL defaults to `column_major` while the CPU is row-vector.
  Every shader now declares `row_major`.
* **A vertex shader ignored `SetTransform`.** With a shader bound, Direct3D 9 reads its constant
  registers and ignores the fixed-function transform state. The renderer now sets both, so the
  shader path and the fallback each get what they need.
* **A shader failed to compile on a reserved word.** The blur shader named its offset `step`,
  shadowing the HLSL intrinsic; it is now `offset`.
* **A capture could grab the previous frame.** A back-buffer copy is not ordered against a
  preceding clear in the same batch; the copy is now flushed, and the timeline captures its own
  buffer.
* **Draw failures were swallowed.** A mesh that failed to draw produced no log line. `DrawPass`
  now logs the node name and records the failure against the renderer.
* **The second readback of a process wedged the driver.** A kernel-mode wait that cannot be timed
  out, holding the device's lock. Readback is now one-shot per device; later callers get an empty
  array immediately and the engine keeps running.
* **A 3D effect drew into a corner.** The renderer set the viewport to the window size instead of
  leaving it to the caller; `display3d` now sets and restores its own rectangle and scissor.

### Known limitations

* No audio.
* No networking in the runtime; the protocol and client exist in the shared assembly and the
  master is a separate project, but nothing in the runtime connects.
* No editor and no launcher; `DPTEngine.slnx` lists both, so the solution build fails.
* Screenshots and pixel readback work once per device, as a workaround for the driver bug above.
* Colours do not survive a save/load round trip: the writer emits `#RRGGBBAA`, the reader expects
  `#AARRGGBB`.
* `display3d` stores a camera index that is not used; a scene has one camera.
* `shading` is stored but not honoured; lighting is disabled globally.
