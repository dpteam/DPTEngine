# Architecture

## Layering

```
+-------------------------------------------------------------+
|  executables                                                |
|  dptengine        dptengine-tests    dptengine-selftest      |
|  (runtime)        (unit + interop)   (frame + pixel probe)   |
|  dptengine-master (directory server, own protocol build)     |
+-------------------------------------------------------------+
|  DPT.Engine                                                  |
|  Runtime    EngineApp, Headless                              |
|  Engine     TimelinePlayer, TemplateProject                  |
|  Project    ProjectFile, ProjectSerializer, Effects          |
|  Rendering  SceneRenderer, QuadRenderer, TextRenderer, BMP   |
|  Scenes     Mesh, SceneGraph, ParticleEmitter                |
|  Timeline   Spline, Key, Interpolation                       |
|  Graphics   D3D9Device, interop, resources, shaders           |
|  Win32      window, input, virtual keys, P/Invoke             |
|  Mathematics Matrix, Vector2/3/4, Quaternion, ColorRgba      |
+-------------------------------------------------------------+
|  DPT.Engine.Shared                                           |
|  Branding, MasterProtocol, MasterClient, Json, PortablePaths |
|  Diagnostics: EngineLog, FaultGuard                          |
+-------------------------------------------------------------+
|  .NET Framework 4.0 BCL + user32, gdi32, winmm, d3d9.dll     |
+-------------------------------------------------------------+
```

The dependency arrow only points down. `DPT.Engine` references `DPT.Engine.Shared`; nothing in
Shared knows about Direct3D, scenes or the project format. That split exists because the same
shared assembly is used by five programs (runtime, tests, self-test, master, and later the
editor host and launcher) and it is the only place the wire protocol and the log front end may
live, or the master and the runtime would drift apart.

`DPT.Engine.Shared` targets `net40;net9.0` so a modern server can host the directory without
installing the .NET Framework 4 runtime. `DPT.Engine` targets `net40` only, because the runtime
has to start on Windows XP.

`DPT.Engine.Master` does not reference the shared assembly at all: it compiles the protocol
sources in by link, so the master executable is a single file that cannot lose a DLL when
someone copies it to a server.

## Row-vector matrix convention

Everything is `v * M` with translation in the fourth row (`M41..M43`), the layout XNA used and
the layout the project format was authored against.

```csharp
// DPT.Engine.Mathematics.Matrix
public Vector3 Translation { get { return new Vector3(M41, M42, M43); } }
public static Matrix Multiply(Matrix a, Matrix b); // a, then b
```

Consequences that are easy to get wrong:

* `a * b` means "apply a, then b", so a world matrix is `Scale * Rotation * Translation`.
* Field order is part of the file format: transforms are read straight into `M11..M44`, so
  reordering the fields would silently transpose every object in every existing project.
* HLSL matrices default to `column_major`. Every shader in `ShaderLibrary` declares
  `row_major` and multiplies `mul(vector, matrix)`. Without the qualifier the GPU transposes
  the matrix and the whole scene collapses into the corner of the frame.
* `D3D9Device.SetVertexShaderConstant` copies `M11..M44` in row order into four constant
  registers. Nothing transposes on the way to the GPU.

## The two-pass renderer

`SceneRenderer.RenderInternal` does exactly this per 3D view:

1. Clear the opaque and transparent draw lists, prepare the camera and the environment.
2. `scene.UpdateTransforms()` walks the hierarchy and fills `SceneNode.World`.
3. `Collect` walks the roots, pushing every visible mesh into `opaque` or `transparent`
   according to `Material.Transparent`.
4. Draw opaque: depth write on, no blending. No front-to-back sort: early-z makes it worth less
   than the sort costs.
5. Sort transparent back to front by squared distance from the camera (insertion sort, the list
   is tens of items and nearly sorted already), then draw with depth write off.
6. Draw particles: CPU-billboarded quads, one `DrawPrimitiveUP` per emitter, depth tested but
   not depth writing.

Two things are deliberately not sorted or batched: opaque geometry (early-z) and particles
(they are additive light, order matters only between emitters). Per-item draws are wrapped in a
try/catch that logs the node name and records the failure, so one bad mesh costs one mesh, not
the frame.

If shaders cannot be compiled, `ShadersReady` stays false and `ApplyFixedFunction` binds the
texture stages by hand. Textured and untextured geometry still render; environment mapping and
the fog shader do not.

## The timeline and effect model

A project is a list of `Effect` objects sorted by start frame. Each frame the player walks the
list, and for every effect where `StartFrame <= frame <= EndFrame` it calls `ComputeRect` and
then `Render`.

```
Effect
  StartFrame, EndFrame
  StartRect, EndRect        in the 800x600 virtual space
  Progress(frame)           0..1 across the span
  ComputeRect(context)      interpolates the rectangle, scales to device pixels, clamps
  Render(context)           draws, or changes state
```

`EffectContext` is the only state an effect may touch. It is created once per player and reused:
device, renderer, quad renderer, project, the six off-screen slots, the current frame buffer,
the frame number, the effect rectangle in pixels, and the accumulated camera shake offsets.
Effects themselves are stateless data, so the same object renders correctly on any frame and the
editor can scrub the timeline without a reset.

Effects fall into three groups:

| Group | Types | Behaviour |
| --- | --- | --- |
| Draw into the frame | `clear`, `layer2d`, `display3d`, `blur`, `feedback`, `contrast` | Write pixels into the project's frame buffer |
| Capture | `rendertotexture` | `StretchRect` from the frame buffer into a slot |
| State only | `camerashake`, `particles` | Do not draw; leave an offset for the next 3D pass, or advance emitters |

Two rules make the post-processes work. First, they read the **project's** frame buffer, not the
window's back buffer: the project renders at its own resolution, so the back buffer holds a
previous frame at a different size. Second, the slots are shared between effects, so
`rendertotexture` writes slot N, `blur` reads it and writes back, and a later `layer2d` can show
the result. Each slot is half the project resolution: six full-size targets at 1024x768 would be
about 18 MB of video memory for buffers that are sampled at their own resolution anyway.

Camera shake is a state effect: it sets `context.EyeShake` and `context.TargetShake`, the next
`display3d` adds them to the camera and clears them unless `DontSaveCamera` is set. Offsets are
computed from a seeded xorshift at a fixed frequency and interpolated between two ticks, so the
shake is deterministic per frame and scrubbing does not change it.

## Off-screen project buffer and letterboxing

`TimelinePlayer` allocates one render target at the project's resolution and six at half that.
Every frame:

```
SetRenderTarget(frameTarget)
Clear(black)
for each active effect: ComputeRect, Render
SetRenderTarget(null)              -> back buffer
Clear(black)                        letterbox bars
draw frameTarget.Texture at the centred, aspect-correct rectangle
SetTexture(0, null)
```

The letterbox rectangle is computed from the project aspect against the window aspect; the
window is never asked to stretch. This is why a demo looks identical at 320x240 and 4K: all
effect rectangles, viewports and the 3D camera aspect come from the project buffer.

Two details that bite:

* Binding a render target unbinds sampler textures first, and the depth-stencil surface is
  recreated to match the target's size. A 1024x768 target with the window's 512x384 depth buffer
  still bound makes every pixel outside that corner fail the depth test.
* `display3d` sets the viewport and scissor to its own rectangle and restores them afterwards;
  `SceneRenderer` deliberately never sets the viewport itself.

## COM vtable approach

There is no import library for the Direct3D 9 interfaces, so every call goes through a vtable
slot:

```csharp
internal static IntPtr Method(IntPtr instance, int slot)
{
    var table = Marshal.ReadIntPtr(instance);
    return Marshal.ReadIntPtr(table, checked(slot * IntPtr.Size));
}
```

`D3D9Slots` holds the slot numbers from `d3d9.h`, counted from zero and including the three
`IUnknown` methods. The delegates are `[UnmanagedFunctionPointer(CallingConvention.StdCall)]`
with explicit argument lists; `D3D9Types.cs` holds the structs with their exact layouts.

Three rules the code follows, each of them a bug avoided:

1. **Resolve once, cache.** A vtable read plus a delegate allocation per `SetRenderState` is
   measurable in a frame that issues thousands of them. `D3D9Device.CacheDelegates` resolves
   every delegate once at creation.
2. **Release per object.** `Release` lives at the same slot index on every interface but is a
   different function per object. Calling the device's `Release` with a surface pointer crashes
   on some drivers; `ReleaseObject` resolves the slot from the instance being released.
3. **Never share a delegate between differently shaped methods.** `IDirect3DTexture9::LockRect`
   takes a mip level that `IDirect3DSurface9::LockRect` does not. Sharing the delegate corrupts
   the stack on x86, which is why both exist in `D3D9Interop.cs`.

The device caches render state, sampler state, texture-stage state and the bound textures,
shaders, FVF, stream and indices, and skips a call when the value is unchanged. The cache is
invalidated on `Reset`, because the driver forgets everything at that point.

## Frame timing and failure handling

`EngineApp.Loop` is fixed-step: a `QueryPerformanceCounter` delta is accumulated and consumed in
whole `1 / project.FramesPerSecond` steps, capped at 0.25 s so a breakpoint or a driver reset
does not fast-forward the demo. Rendering happens once per pass, not once per simulated frame.

Cross-cutting concerns sit at the boundaries:

* `FaultGuard.Guard` / `Attempt` wrap everything that touches outside data: project load,
  effect render, texture upload, shader compile, screenshot.
* `FaultGuard.Record` marks the subsystem degraded immediately and disables it after three
  consecutive failures, so a transient device loss is survivable and a permanently broken
  shader stops spamming the log.
* `EngineLog` is the only writer; see `docs/LOGGING.md`.
* A lost device is not an error. `D3D9Device` sets a flag and returns; `EngineApp.RecoverDevice`
  releases default-pool resources, resets, and rebuilds shaders, quads and targets within a five
  second deadline.
