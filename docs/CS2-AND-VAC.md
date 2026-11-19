# CS2 and anti-cheat notes

## What the overlay is

HardwareOSD draws a normal top-level window with these properties:

| Property | Implementation | Purpose |
| --- | --- | --- |
| `WS_EX_LAYERED` + `AllowsTransparency` | WPF layered window | Per-pixel transparency |
| `WS_EX_TRANSPARENT` | `SetWindowLongPtr(GWL_EXSTYLE)` | Click-through: mouse input passes to the window underneath |
| `WS_EX_NOACTIVATE` | `SetWindowLongPtr` | The overlay never takes focus |
| `WS_EX_TOOLWINDOW` + `ShowInTaskbar=false` | window style | No Alt+Tab/taskbar entry |
| `WS_EX_TOPMOST` / `SetWindowPos(HWND_TOPMOST)` | `Topmost=true`, re-asserted every 5 s | Stays above other windows |

The window is composed by DWM exactly like any other application window. There is no rendering hook,
no swap-chain interception, no overlay injection, no `SetWindowsHookEx`, no `ReadProcessMemory`, no
`WriteProcessMemory`, no `CreateRemoteThread`, no game file modification, no input interception and
no kernel driver authored by this project.

The application never opens a game process for reading or writing. Sensor data comes from the
display driver, CPU MSRs (when elevated, through LibreHardwareMonitor), Windows performance
counters and system APIs. FPS monitoring identifies the foreground window's process id and consumes
OS-level ETW present events; the game is not opened, read or injected.

## Display modes

| Mode | External overlay visible? | Verified here? |
| --- | --- | --- |
| Windowed | Yes | Overlay yes; FPS measurement requires *Measure windowed apps* |
| Borderless / windowed fullscreen | Yes - it is still a composed desktop window | Yes, with a borderless maximized probe window |
| Exclusive fullscreen (DirectX exclusive mode / flip-exclusive without DWM composition) | No — DWM composition is bypassed, so no external topmost window can appear | Not applicable (mode is not supported by design) |

**Recommendation for Counter-Strike 2:** use *Borderless* or *Windowed Fullscreen* video mode.
HardwareOSD does not attempt to force exclusive fullscreen compatibility, because every technique
that could do so safely would require injecting into the game's rendering pipeline or a kernel
component — both violate the security constraints of this project.

## Was CS2 itself tested?

**Yes, for the main-menu live measurement.** Counter-Strike 2 was installed and launched on the
validation machine. The following were verified with the real `cs2.exe`:

- HardwareOSD detected `cs2` as the foreground process (Metrics and Diagnostics pages).
- After UAC elevation, both ETW providers started (`DXGI=True, D3D9=True`) and real present events
  were received: **20,119 events / 1,551 presents** in the final CLI run.
- The measured rate was **118.5 FPS** with `src=Presented`; the overlay displayed **118 FPS·p**
  over the CS2 menu (screenshot: `docs/screenshot-cs2-elevated.png`).
- The elevated app produced **20/20 numeric Metrics samples, 0 `--`, 0 visibility transitions**
  during continuous 0.9.4 measurement.
- Closing CS2 changed the overlay to `--` over OpenCode/Steam (no false desktop-UI FPS); restarting
  CS2 restored **119 FPS·p**. This is the fullscreen foreground gate.
- Without active permission the module correctly reported `Administrator or Performance Log Users
  rights required` with the CS2 process named, rather than fabricating a value.
- The one-time **Grant FPS permission…** flow added the current account to Performance Log Users
  through UAC. After the required sign-out/sign-in it no longer needs elevation; that final
  new-token launch was not performed in this session.

What was *not* performed or is not claimed:

- A full gameplay frame-pacing impact measurement (the live measurement was in the main menu).
- A CS2 rendering/VAC validation session of any kind.

The technical foundation is unchanged: the overlay is a plain external layered window, it stays
above a maximized borderless window, is click-through and never activates, and the process contains
no game-specific code, signatures or memory access. The FPS backend's provider GUIDs, event IDs and
keywords were verified against the installed Windows manifests and then exercised live.

## VAC compatibility statement

An external overlay does not automatically guarantee VAC compatibility. There is no public
guarantee from Valve for arbitrary third-party overlays, and this project cannot test or promise
one. What can be stated factually:

- The application performs no injection and no game memory access.
- It does not hook graphics APIs or input.
- It does not attempt to hide from, bypass or interact with anti-cheat software.
- It uses documented Windows interfaces for its own window and for hardware telemetry.

Users should treat this as "a normal desktop window while playing", and decide for themselves
whether to run it. If a game or anti-cheat update ever restricts external overlays, HardwareOSD
cannot work around that without violating its design constraints.

## Alternatives investigated

| Approach | Verdict |
| --- | --- |
| External layered window (chosen) | Safe, no game interaction, works in borderless fullscreen |
| DLL injection + in-game Present hook | Rejected: injection, anti-cheat risk, violates project constraints |
| Kernel-mode compositor overlay | Rejected: driver requirement, security and maintenance cost |
| Windows.Graphics.Capture composite preview | Rejected: duplicates the game (performance), not a transparent overlay |
| Steam/Discord overlay integration | Not applicable: would require cooperation from those clients |

## FPS and frame time

FPS is collected externally from a real-time ETW session (`Microsoft-Windows-DXGI` /
`Microsoft-Windows-D3D9` present events). The foreground window's process id is identified with
`GetForegroundWindow`, then only that process's present event timestamps are measured. The game
process is never opened, read, injected or hooked; ETW is an operating-system tracing facility, not
game interaction.

Requires administrator rights or Performance Log Users membership (ETW session creation). CS2 renders through DXGI, so it is covered by
the backend, including the presented-vs-displayed distinction and explicit status handling. FPS is
enabled with the "FPS (foreground app)" checkbox on the Metrics page, and
the overlay toggle shortcut is configurable under Settings → Hotkeys. Live CS2 frame-rate
measurement was verified in 0.9.3/0.9.4 (118.5 FPS elevated, 20/20 samples, Presented, real ETW
present events); no VAC conclusions are claimed here. By default FPS is only measured when the game
window is fullscreen/borderless; enable *Measure windowed apps* for a windowed game.
