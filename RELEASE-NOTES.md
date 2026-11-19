# HardwareOSD 1.0.0.0 — Release Notes

HardwareOSD 1.0.0.0 is the first public release of the external hardware-monitoring overlay for
Windows 11 x64.

## Highlights

- Transparent, click-through, always-on-top OSD overlay with stacked, inline and detailed layouts.
- Five original OSD profiles, three CapFrameX-derived row templates and 45 tile designs.
- Full OSD Builder with layers, groups, metrics, charts, bars, gauges, templates, drag/resize,
  undo/redo and import/export.
- CPU, GPU, VRAM, RAM, system and FPS metrics with explicit `N/A` handling.
- RAM/VRAM Used, Total, Available and percentage formats, including `used / total`.
- Optional mini graphs, warning/critical metric colours and per-tile styling.
- English and Ukrainian interface with independent application and overlay themes.
- CSV/TXT sensor recording and a detailed System information page.
- Optional per-user Start with Windows; nothing is registered silently.
- No game injection, game-memory access, network access or telemetry.

## Important fixes included

- Stable footer geometry when values, units or language change.
- Removed the obsolete language/status footer message area.
- Retired Blackwell starter profiles are hidden from new-profile and template choices while old
  saved profiles remain compatible.
- Correct label/value alignment in Dual Line and IceLAN Vertical designs.
- OSD themes, backgrounds, warning/critical thresholds and mini graphs now reach the real overlay.
- Standard Set shrinks the overlay to the actual visible content.
- GPU Power Limit and RAM metrics are inserted into their logical groups.
- Duplicate OSD Builder Structure block removed from Metrics.
- Real fan RPM/percentage channels are used; percentage is never synthesized.
- RAM/VRAM now support Used/Total and real provider totals.
- Main-window close now exits cleanly without an unhandled UI exception.
- A portable or QA instance no longer stops the main instance's FPS ETW session.
- The About page reports the full 1.0.0.0 version.

## Installation

1. Download `HardwareOSD-1.0.0.0-win-x64.zip`.
2. Unpack it into a normal folder.
3. Run `HardwareOSD.exe`.

The self-contained package includes the .NET runtime. A framework-dependent build is also provided
for systems that already have the .NET 9 Desktop Runtime.

Settings are created at `%LOCALAPPDATA%\HardwareOSD\settings.json` on first launch. Logs are written
to `%LOCALAPPDATA%\HardwareOSD\logs`.

## Optional FanBroker

The FanBroker is an opt-in LocalSystem service for read-only MSI fan readings. It is never installed
or started by the application. See `README.md` and `tools\install-fan-broker.ps1`.

## Known limitations

- Fan percentage is available only when the hardware exposes a real PWM/control channel.
- Exclusive fullscreen games cannot be overlaid; use borderless or windowed fullscreen.
- Vulkan/OpenGL-only FPS titles are not covered by the present-event providers.
- FPS requires administrator rights or Performance Log Users membership.
- An ETW session stopped by another tool is not automatically restarted.
- Mixed-DPI and physical multi-monitor positioning were not fully verified on a multi-display setup.
- Capture duration/delay preferences do not currently provide an automatic capture scheduler.
- AMD/Intel GPU sensor coverage depends on LibreHardwareMonitor and was not validated on every GPU
  family.

## License

HardwareOSD is MIT licensed. Third-party notices and license texts are included with the release.
