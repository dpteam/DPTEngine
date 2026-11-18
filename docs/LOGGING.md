# Logging

The engine logs through `System.Diagnostics.Trace`. Not a bespoke logger, not a package: Trace
already gives levels, listeners, rotation-friendly hooks and debugger integration, and a profiler
or an IDE attaches to it without any cooperation from the engine.

All of it is fronted by `DPT.Engine.Shared.Diagnostics.EngineLog`.

## Design rules

**No App.config.** Trace listeners are normally declared in configuration, which means shipping
an `.exe.config` beside every program. That is a second file that can go missing in a copy, and
it silently changes behaviour between a developer's machine and a user's. Listeners are attached
in code instead.

The SDK still emits a minimal `dptengine.exe.config` containing only
`<supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.0" />`. It declares which runtime
to load and contains no listeners. Nothing in the engine reads it.

**Initialize is idempotent.** The classic bug is calling `Initialize` from two places - the entry
point and a library that wants to log early - which attaches a second file listener, so every
line appears twice and the two listeners race on rotation. `Initialize` guards with a static flag
*and* scans `Trace.Listeners` for an instance of our listener types, because a flag alone does not
survive a test harness that clears the collection:

```csharp
if (initialized && fileListener != null) return;
```

**Logging never throws.** Every method is safe from any thread. A log call that can take the
engine down is worse than no log at all, so the writers, the rotation, the file open and the
subscriber callbacks are all wrapped.

**The default listener is removed.** `DefaultTraceListener` writes to `OutputDebugString` and can
raise an assertion dialog from a background thread. A full-screen demo must never block on a
message box because of a log line. `RemoveDefaultListeners` sets `AssertUiEnabled = false` and
detaches it.

## Log levels

| Level | Value | Letter | Written by |
| --- | --- | --- | --- |
| `Verbose` | 0 | `V` | `EngineLog.Verbose` |
| `Info` | 1 | `I` | `EngineLog.Info` (default minimum) |
| `Warning` | 2 | `W` | `EngineLog.Warn` |
| `Error` | 3 | `E` | `EngineLog.Error`, `EngineLog.Exception` |
| `Fatal` | 4 | `F` | `EngineLog.Fatal` |
| `Off` | 5 | - | Set as the minimum to silence everything |

Anything below `MinimumLevel` is dropped before formatting, so a verbose log costs nothing when
the level is `Info`. Change it at run time:

```csharp
EngineLog.MinimumLevel = LogLevel.Verbose;
```

or at start-up with `dptengine --log-level=verbose` (also `info`, `warning`, `error`).

`EngineLog.Exception(category, error)` walks up to eight levels of the inner-exception chain and
defaults the category to the exception type name, so grepping for `NullReferenceException` finds
every occurrence.

## Log line format

```
2026-11-19 14:03:21.417 [I] project: (t1) Loaded 'DPT.Engine Template' from C:\demo\demo.dptproj (1 scenes, 7 effects).
2026-11-19 14:03:21.905 [W] graphics: (t1) No depth-stencil for the off-screen target: 0x8876017C
2026-11-19 14:03:22.001 [E] renderer: (t1) Drawing 'cube3' failed: ...
```

| Part | Meaning |
| --- | --- |
| `yyyy-MM-dd HH:mm:ss.fff` | Local time, milliseconds included |
| `[I]` | Level letter |
| `project:` | Category, omitted entirely when empty |
| `(t1)` | Managed thread id, which is what makes a multi-threaded log readable |
| the rest | The message |

Messages longer than 8192 characters are truncated with ` ... (truncated)`. A stack trace logged
inside a per-frame loop is how a log file reaches gigabytes.

## Where the file goes

`EngineLog.Initialize(path)` with a null path uses `<entry assembly name>.log` in the data
directory, so `dptengine.log` for the runtime, `selftest.log` for the self-test.

The data directory is `PortablePaths.DataDirectory`: the executable's own folder when that folder
is writable, otherwise `%APPDATA%\DPTEngine`. This is the portable-first rule: a USB stick, a demo
party machine or a Windows XP install keeps everything together; a read-only `Program Files`
falls back to the user profile.

`EngineLog.FilePath` returns the resolved path, and the runtime logs it at start-up.

## Rotation

| Setting | Default | Meaning |
| --- | --- | --- |
| `maxFileBytes` | 4 MB (minimum 64 KB) | Size that triggers a rotation |
| `maxArchives` | 3 | Number of numbered archives kept |

Rotation runs under the writer's lock: close, rename `log` to `log.1`, `log.1` to `log.2` and so
on, dropping the oldest, then reopen. With `maxArchives` zero the file is simply deleted. A crash
inside that window costs at most one line, which is better than a lock held forever by a process
that has gone away.

The file is opened with `FileShare.ReadWrite | FileShare.Delete`, so a launcher can tail it while
the game writes and two processes can share a folder. It is UTF-8 without a BOM, and
`Trace.AutoFlush` is on, because without it a crash loses the last few lines - the only ones
anybody reads.

If the file cannot be opened at all (read-only folder, full disk), logging degrades to the memory
ring and the debugger. It does not stop the game.

## The in-memory ring

A `MemoryLogListener` keeps the last 2048 formatted lines in a ring buffer. The overlay and the
editor's log pane read it instead of tailing the file:

```csharp
string[] last = EngineLog.Recent(50);   // oldest first
string text = EngineLog.RecentText(50); // same, joined with newlines
```

Crash reports use it too: `FaultGuard.WriteCrashReport` appends the last 200 lines, which is the
only context that explains a crash when the log file itself is the thing that broke.

## Adding a sink

A sink is an extra destination besides the file and the ring - a network feed for the editor, a
test collector, a syslog bridge.

```csharp
internal sealed class NetworkSink : EngineLog.ILogSink
{
    public void Write(LogLevel level, string category, string message)
    {
        // Called synchronously on the logging thread. Must not throw and
        // must not block for long: it runs inside the frame.
    }
}

EngineLog.AddSink(new NetworkSink());
EngineLog.RemoveSink(sink);
```

Rules that apply to sinks:

* they are called synchronously on the writing thread, after the file write;
* an exception from a sink is swallowed, so a broken sink cannot break logging;
* `AddSink` ignores duplicates and nulls;
* the sink list is snapshotted before dispatch, so a sink may add or remove sinks from inside
  `Write` without corrupting the loop.

For UI that wants raw lines rather than formatted ones, subscribe to `EngineLog.LineWritten`
instead:

```csharp
EngineLog.LineWritten += delegate(LogLevel level, string category, string message)
{
    // message here is the unformatted text, without timestamp or thread id.
};
```

A throwing handler is ignored for the same reason.

## Lifecycle

```csharp
EngineLog.Initialize(null);                                   // Info, 4 MB, 3 archives
EngineLog.Initialize(path, LogLevel.Verbose, 1 << 20, 1);     // custom
EngineLog.Shutdown();                                         // detach everything
```

`Shutdown` exists for tests that need to move the log file, and for a host that wants to
re-initialise with a different path. Note that a listener whose file failed to open is still
attached - it degrades to the memory ring - so a second `Initialize` call does nothing. To retry
with a different path, call `Shutdown()` first.

## What gets logged

Categories are short and stable, so `grep` works: `startup`, `project`, `player`, `timeline`,
`renderer`, `graphics`, `shader`, `headless`, `runtime`, `window`, `crash`, plus
`<subsystem>.<operation>` for anything the fault guard recorded.

`FaultGuard` logs every recorded failure through `EngineLog.Exception` and adds one line when a
subsystem disables itself after three failures. The subsystem table is also printed by
`FaultGuard.Describe()` in crash reports and shown in the overlay when something is not `Running`.
