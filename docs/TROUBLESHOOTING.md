# Troubleshooting

Every entry here is a real failure found while porting the engine, with the cause and the fix
that is now in the code. If you hit one of these again, the fix has regressed.

Where to look first:

| Thing | Location |
| --- | --- |
| Log file | `dptengine.log` in the data directory (exe folder, or `%APPDATA%\DPTEngine`) |
| Log archives | `dptengine.log.1` .. `.3`, 4 MB each |
| Crash reports | `Crashes\crash-yyyyMMdd-HHmmss.txt` in the data directory |
| Screenshots | `Screenshots\dptengine-yyyyMMdd-HHmmss.bmp` |
| Headless frames | `Screenshots\headless-<frame>.bmp` |
| Shader cache | `ShaderCache\<hash>.dptshader` |
| Subsystem state | The overlay, shown when anything is not `Running`; also in every crash report |

The data directory is the executable's folder when it is writable, otherwise
`%APPDATA%\DPTEngine`. The runtime logs the resolved path at start-up.

## Device lost on alt-tab

**Symptom.** Alt-tab out of a full-screen demo and back: the window is black or frozen, the
frame counter stops, sometimes nothing is logged at all.

**Cause.** Direct3D 9 loses the device whenever it cannot own the display: alt-tab from full
screen, a driver update, a resolution change, a remote session attach. Every call then returns
`D3DERR_DEVICELOST` until the device is reset, and the default-pool resources (render targets,
the depth-stencil, dynamic buffers) no longer exist.

**Fix.** A lost device is treated as normal, not as an error:

* `D3D9Device.Check` sets a `lost` flag and returns on `D3DERR_DEVICELOST` /
  `D3DERR_DEVICENOTRESET` instead of throwing, so the fault guard never sees it.
* `EngineApp.RenderFrame` calls `CheckDevice` first and goes to `RecoverDevice` when it fails.
* `RecoverDevice` polls `TestCooperativeLevel` for up to five seconds, releases the text cache,
  the player and the renderer's device resources, calls `Reset`, then rebuilds shaders, quads and
  render targets.
* `Reset` invalidates the render-state cache, because the driver forgets everything at that
  point.

If you add a new device-owned resource, it must be released in `ReleaseDeviceResources` and
recreated in `RestoreDeviceResources`, or the first alt-tab turns it into a stale handle and an
access violation.

## Readback wedges the driver (one shot per device)

**Symptom.** The first screenshot of a run works. The second hangs the process with no error, no
crash and no CPU use. The first headless run hung at frame nine, inside `LockRect`.

**Cause.** Two driver-level problems, both on WDDM (Vista and later):

1. `LockRect` on the back buffer blocks until the GPU is done presenting. Under load that vblank
   may never arrive, and the wait is in kernel mode, so it cannot be timed out.
2. `GetRenderTargetData` is documented to synchronise but does not when the device has a deep
   queue. Worse, the second `GetRenderTargetData` of a process on the driver this was developed
   against wedges inside the driver, holding the device's internal lock. A Direct3D 9 device is
   not thread-safe, so a watchdog thread cannot recover it: the next call on any thread blocks
   forever.

**Fix.** Readback is a one-shot diagnostic:

* the first read copies into a system-memory surface (`CreateOffscreenPlainSurface`,
  `Pool.SystemMem`), locks that with `D3DLOCK_DONOTWAIT` and retries against a deadline, and
  copies row by row honouring the pitch;
* the copy is preceded by an event-query flush (`Flush`), because a blocking
  `GetRenderTargetData` on a deep queue is the deadlock;
* any further read on the same device returns an empty array immediately;
* a failed or timed-out read disables readback for the session instead of retrying.

Consequences: F12 saves one screenshot per run, and the headless verification checks pixels on
one frame and every other frame structurally (effects ran, nothing threw). The render path itself
never reads back.

`DPT_FRESH_READBACK=1` in the environment makes the staging surface be recreated for each read
instead of reused. It is a debugging aid for investigating the driver bug; it does not bypass the
one-shot rule.

## A texture upload wedges the process

**Symptom.** A project that has large procedural textures stops after
`renderer: Uploading texture N at WxH with M mip level(s).` in the verbose log, with no error, no
crash, no CPU use and no window. It happens on the same project every time and on no other.

**Cause.** The same class of driver defect as the readback one: `LockRect` on a texture mip level
never returns, inside the driver, holding the device's internal lock. The evidence that it is the
driver and not the upload code is that the identical code path - same pool, same mip count, same
row copy - succeeds for other projects in the same run, and that the exact call it stops in moves
between runs of the same file.

**What you can do.** Nothing in-process: a kernel wait cannot be timed out, and a Direct3D 9
device is not thread-safe, so a watchdog cannot recover it. Workarounds, in order of preference:

* run that project on a machine with a different driver, or with the reference rasteriser
  (`D3D9Device` falls back to `REF` when `HAL` fails);
* import the project with `--import` and edit the texture resolutions down in the editor before
  playing it;
* lower the mip count in `SceneRenderer.CountMipLevels` while investigating.

If you are porting this engine and hit it often, the structural fix is to build textures in a
`Pool.SystemMem` texture and hand them over with a single `UpdateTexture` call, which replaces N
`LockRect` calls with one driver call. That is not implemented here because the failure cannot be
reproduced reliably enough to prove the fix.

## The process dies with no log line and no exception

**Symptom.** The demo runs for a while - anything from a minute to several - and then
disappears. The log ends mid-sentence with no `[E]` line, no crash report, and often with the
Windows dialog *"Exception Processing Message 0xc0000005 - Unexpected parameters"*. Exit code is
`0xC0000005` or `0x80131506`.

**Why nothing is logged.** `0x80131506` is `COR_E_EXECUTIONENGINE`: the CLR itself caught an
access violation and terminated the process. That path does not run
`AppDomain.UnhandledException`, so the crash reporter never fires. `0xC0000005` is the raw fault,
and if it happens inside a window procedure Windows shows its own dialog instead. In both cases
the managed stack is already gone by the time anything could record it.

**What the engine now does about it.** Three layers, each for a different failure mode:

* **Native fault tracer** - a vectored exception handler that runs before everything else,
  including the CLR. It records the faulting instruction pointer resolved to `module+offset`, plus
  whether the access was a read or a write and to which address. Output goes to
  `Crashes\native-fault.txt` and to the log. A line like
  `ACCESS_VIOLATION at d3d9.dll+0x1A4C2 writing 0x0000000C` is the whole diagnosis.
* **First-chance exceptions** - logged the moment they are thrown, before anything handles them.
  The serious types (access violation, stack overflow, out of memory, type initialiser) are
  always logged; ordinary ones are capped so the log stays readable.
* **A one-second heartbeat** at verbose level with the frame number, live particle count, GC
  counts and heap size. If the process dies silently, the last heartbeat says how far it got.

### Capturing a fault

Run with the dialogs enabled so nothing is suppressed, and with verbose logging:

```bat
set DPT_ALLOW_CRASH_DIALOGS=1
dptengine.exe --log-level=verbose
```

When it dies, `Crashes\native-fault.txt` will contain the module and offset. Send that.

`DPT_ALLOW_CRASH_DIALOGS=1` matters because the engine calls `SetErrorMode` to suppress Windows
error dialogs, which also suppresses Windows Error Reporting and therefore any crash dump.

### Bisecting where it dies

Each variable removes one layer. If the fault disappears with a layer removed, it is in that
layer; if it survives all of them, it is in the window/device/present path.

| Variable | Effect |
| --- | --- |
| `DPT_MINIMAL=1` | Clear and present only - no project, no blit, no overlay |
| `DPT_MINIMAL=2` | Adds the off-screen project buffer |
| `DPT_MINIMAL=3` | Adds the letterboxed blit to the window |
| `DPT_SKIP_EFFECTS=particles,display3d` | Leaves the named effect types out of the timeline |
| `DPT_DEVICE=ref` | Forces the software rasteriser: if the fault goes away, it is the GPU driver |
| `DPT_DEVICE=hal` | Forces the hardware device, skipping the fallbacks |
| `DPT_CRASH_TEST=1` | Faults deliberately, to prove the tracer is working |

`DPT_DEVICE=ref` is the important one for deciding whether to keep debugging. A fault that only
happens on one driver is not something the engine can fix; a fault that happens on the reference
rasteriser too is in this code.

## A render target bound as a texture crashes

**Symptom.** The first frame that composites a post-process crashes inside the driver, or the
draw silently produces nothing. The blur and feedback effects bound their own slot as a shader
input and then copied the frame into it.

**Cause.** Direct3D 9 forbids a surface from being a render target and a shader input at the same
time. Several drivers treat the combination as undefined behaviour rather than returning an error.

**Fix.** `D3D9Device.SetRenderTarget` and both `StretchRect` wrappers unbind samplers 0 and 1
before touching the target, and `TimelinePlayer.PresentFrameToWindow` unbinds the frame buffer
again after the final blit. `ReadSurface` unbinds as well, because `GetRenderTargetData` refuses a
source that is still bound to the device.

If you add an effect, unbind before you rebind.

## Depth-stencil size mismatch with an off-screen target

**Symptom.** The project renders into a 1024x768 buffer but only the top-left 512x384 corner
shows content; everything else is the clear colour. Or the picture is correct in a 512x384 window
and clipped at any other size.

**Cause.** The auto depth-stencil created with the device belongs to the swap chain and is the
window's size. Rendering into a larger off-screen target while that depth buffer stays bound means
every pixel outside the window's rectangle fails the depth test and is discarded.

**Fix.** `SetRenderTarget` binds a depth-stencil that matches the target: the swap chain's own
surface for the back buffer, a cached `D24S8` surface created per size for off-screen targets.
If the driver refuses the depth surface, the call logs a warning and continues without depth: the
2D passes do not need it and a machine that cannot provide one still renders.

Related: `SceneRenderer` deliberately does not set the viewport. The caller owns it, because a 3D
effect renders into its own rectangle of the project buffer. Setting it to the window size made
every 3D effect draw into the top-left corner.

## Shader compilation failures

**Symptom.** `Shader 'posnormaltex' (main, vs_2_0) failed to compile: ...` in the log, followed by
`Falling back to fixed-function rendering`.

### `row_major`

HLSL matrices default to `column_major`. The CPU side is row-vector, `v * M`, so a matrix uploaded
unchanged arrives transposed and the whole scene collapses into a corner or disappears. Every
matrix declaration in `ShaderLibrary` carries the qualifier:

```hlsl
row_major float4x4 Projection : register(c0);
```

If you add a shader, add the qualifier. The symptom of forgetting it is not an error, just wrong
geometry.

### Reserved words

The blur shader originally named its offset `step`, which shadows the HLSL intrinsic of the same
name and fails to compile with a message that points at the wrong line. It is now `offset`. Other
names to avoid in these shaders: `saturate`, `lerp`, `frac`, `clip`, `sample`, `linear`, `point`,
`matrix`, `vector`, `texture`, `sampler`.

### General checklist

* Target is `vs_2_0` / `ps_2_0` on purpose. `ps_3_0` would drop the low-end hardware this engine
  targets for no visual gain here.
* Constant registers are fixed and shared with `D3D9.Registers`: vertex `c0..c3` projection,
  `c4..c7` view, `c8..c11` world, `c12..c15` texture transform, `c16` fog range; pixel `c0` global
  colour, `c1` and `c2` effect parameters.
* Compiled bytecode is cached in memory and in `ShaderCache\`, keyed by a hash of name, entry
  point, target and source. Editing a shader invalidates exactly one entry. If you change a
  shader and see the old result, delete `ShaderCache\`.
* `ShaderCompiler.ClearCache()` drops both caches.

## Missing d3dcompiler DLLs

**Symptom.** In the log: `No d3dcompiler_*.dll found; shaders cannot be compiled and the engine
will use the fixed-function fallback.` The subsystem table shows `shader: Failed`. Geometry still
draws, textures still draw, but environment mapping, the blur, feedback and contrast shaders and
the fog shader are gone.

**Cause.** Shaders are HLSL source compiled at start-up. `ShaderCompiler` tries
`d3dcompiler_47.dll` down to `33.dll` via `LoadLibrary`, then `GetProcAddress("D3DCompile")`. On
Vista and later one ships with Windows; on XP one is present as soon as any Direct3D 9 game has
been installed, which is the same precondition Direct3D 9 itself has. A bare XP install has none.

**Fix.** Install the DirectX 9.0c runtime (the June 2010 redistributable), or drop the matching
`d3dcompiler_43.dll` beside the executable. 43 is the last version that supports the D3D9-era
shader models without a separate redistributable.

The engine is designed to keep running without a compiler, so this is a degradation, not a
failure to start. The renderer marks itself `Degraded`, not `Failed`.

## The engine starts, then exits immediately

**Symptom.** A window flashes and the process ends with exit code 1.

**Cause and fix.** Read the log and the newest crash report. The common cases:

* The project file does not exist: an error line on stderr and a log line name the path, then the
  built-in template plays instead.
* The project is not valid JSON: `InvalidDataException: The project file is not valid JSON`, then
  the template plays.
* No Direct3D 9 device at all: `Direct3DCreate9 returned null` or
  `IDirect3D9::CreateDevice failed for every device type`. Nothing can render; check the display
  driver and whether the session has 3D acceleration (RDP without acceleration has none).

`SetErrorMode` is set at start-up so a native fault does not block on the Windows "stopped
working" dialog. Without it the process appears to hang, the crash handler never runs and no
report is written.

## Crash reports

`FaultGuard.InstallCrashHandler(null)` is called first thing in `EngineApp.Run` and installs
`AppDomain.UnhandledException` plus `ProcessExit`. On an unhandled exception it writes:

```
Crashes\crash-yyyyMMdd-HHmmss.txt
```

in the data directory. The report contains the reason, time, engine version, OS, CLR version and
bitness, the command line, the working directory, the **subsystem table**, the full exception
chain with stack traces, and the last 200 log lines.

It is written separately from the log on purpose: the log may be the thing that is broken (disk
full, file locked), and the subsystem table is what actually explains a partial failure. A normal
exit writes no report: `BeginShutdown` and `ProcessExit` set a flag that suppresses it.

If even the report cannot be written, `WriteCrashReport` returns null and nothing else happens.
There is no minidump; `*.dmp` is gitignored but nothing produces one yet.

## Other bugs that are already fixed

These are worth knowing because the symptom does not point at the cause.

| Symptom | Cause | Fix |
| --- | --- | --- |
| Every mesh silently fails to draw, nothing in the log | `Buffer.BlockCopy` rejects arrays of structs, so the upload threw and the fault guard swallowed the draw | `SceneRenderer.StructsToBytes` pins the array and uses `Marshal.Copy` |
| Textured surfaces draw as flat colour | The player never uploaded the project's textures, so material indices pointed at textures the renderer had never seen | `TimelinePlayer.UploadTextures`, once per project |
| Post-process effects composite the previous frame | The timeline captured from the window back buffer instead of the project's own frame buffer | Capture from `context.FrameTarget` |
| A capture right after a clear grabs the previous frame | A back-buffer copy is not ordered against a preceding clear in the same batch | Flush before copying; the timeline captures its own buffer instead |
| A 3D effect draws into a corner | The renderer set the viewport to the window size | The caller owns the viewport; `display3d` sets and restores its own rectangle |
| A draw failure vanishes without a trace | The fault guard recorded it but nothing logged it | `DrawPass` logs the node name and records the failure |
