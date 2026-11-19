# HardwareOSD 1.0.0.0

HardwareOSD is a lightweight, external hardware-monitoring overlay for **Windows 11 x64**. It
draws real sensor readings in a transparent, click-through, always-on-top panel and never injects
into games or reads game memory.

The application is portable: unpack the release archive and run `HardwareOSD.exe`. No installer is
required.

Requirements: Windows 11 x64. The self-contained package needs no runtime; the framework-dependent
package needs the .NET 9 Desktop Runtime. The optional FanBroker installation requires administrator
approval.

## Download and install

1. Download the self-contained archive `HardwareOSD-1.0.0.0-win-x64.zip`.
2. Unpack it into a normal folder, for example `C:\Tools\HardwareOSD`.
3. Run `HardwareOSD.exe`.
4. The overlay appears in the top-left corner. Use the tray icon or the configuration window to
   change its layout, metrics, colours, position and polling interval.

The self-contained package includes the .NET runtime and needs nothing else. A framework-dependent
package (`-fdd`) is also produced for users who already have the **.NET 9 Desktop Runtime**
installed.

First launch creates:

- Settings: `%LOCALAPPDATA%\HardwareOSD\settings.json`
- Logs: `%LOCALAPPDATA%\HardwareOSD\logs\hardwareosd-YYYYMMDD.log`

Existing settings are preserved across updates. A backup of the previous settings generation is
kept as `settings.json.bak`.

An optional `HardwareOSD.Cli.exe` is included for diagnostics; run it without arguments to see the
available read-only sensor and system commands.

## Main functionality

- **Overlay** — stacked, inline and detailed layouts; transparent or opaque background; click-through
  mode; always-on-top positioning; monitor picker, anchors and X/Y offsets.
- **OSD profiles** — the five original HardwareOSD profiles, three CapFrameX-derived row templates
  and 45 tile designs. Four retired Blackwell starter designs remain available only to existing
  saved profiles and are not offered for new profiles. Existing profiles remain editable and are
  never discarded.
- **Metrics** — CPU, GPU, VRAM, RAM, system and FPS groups, with explicit `N/A` and source status for
  unavailable sensors. Warning and critical thresholds can colour individual metrics.
- **RAM / VRAM** — Used, Total, Available and percentage formats, including `used / total` in GB or
  MB where the profile requests it.
- **Mini Graph** — optional history graph drawn inside a metric using the existing sensor history.
- **OSD Builder** — tile editor with layers, groups, text, metrics, charts, bars, gauges, templates,
  drag/resize, undo/redo, import/export and per-profile designs.
- **Recording** — CSV/TXT recording from the live sensor cache, with a bounded background writer and
  explicit unavailable values.
- **System page** — OS, CPU, GPU, RAM, mainboard, storage, platform security and software component
  information collected from Windows.
- **FPS monitoring** — real ETW present events for supported games and applications. Requires
  administrator rights or the one-time *Grant FPS permission* action.
- **Themes and language** — independent application and overlay themes; English and Ukrainian UI.
- **Start with Windows** — optional per-user `HKCU\...\Run` entry, only written when the user enables
  the checkbox. It is never created silently, and portable `--data-dir` profiles cannot modify it.

## Metrics and sensor limitations

HardwareOSD reports what the machine and its drivers expose. It does not invent values.

- CPU temperature, per-core clocks and voltage may require elevation through
  LibreHardwareMonitor's signed driver. CPU package power can work without elevation through
  Windows Energy Meter counters where the platform supports it.
- GPU sensors depend on NVIDIA NVML/NVAPI, AMD ADL or LibreHardwareMonitor support.
- Missing sensors remain visible as `N/A` with a reason instead of a fabricated number.
- DRAM power is unavailable on systems that do not wire the relevant RAPL domain.
- RAM/VRAM totals come from the provider snapshot, not from an assumed maximum.

## Fan monitoring

CPU and GPU fan RPM and percentage are optional Metrics entries. Multiple physical fans are shown
as separate readings such as `1200/900`, never averaged or synthesized.

- Fan percentage is shown only when the hardware exposes a real PWM/control channel.
- If the hardware exposes RPM but not percentage, the percentage row remains `N/A`.
- No fan control, EC writes or fan curves are implemented.
- On supported MSI notebooks, the vendor ACPI `Get_Fan` interface is restricted to administrators.
  HardwareOSD can read it directly when elevated, or through an optional local read-only broker.

### Optional FanBroker

The broker is **not installed or started automatically** and is not required for normal
HardwareOSD operation. It is an opt-in LocalSystem service for read-only MSI fan readings.

From the unpacked release root, run as administrator:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\install-fan-broker.ps1
```

The installer verifies the packaged `FanBroker\fan-broker-manifest.json` hash manifest, copies the
service into `%ProgramFiles%\HardwareOSD\FanBroker`, and requires an explicit typed confirmation.
Removal is equally explicit:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\uninstall-fan-broker.ps1
```

The broker only implements the fixed read-only `GetFanSnapshot` request over a local named pipe. It
does not control fans, write EC registers, install a kernel driver or modify WMI/device ACLs.

## Configuration and startup

- Settings: `%LOCALAPPDATA%\HardwareOSD\settings.json`
- Logs: `%LOCALAPPDATA%\HardwareOSD\logs\`
- Settings writes are atomic and corrupted files are preserved for recovery.
- **Settings → General → Start with Windows** is the only normal way to create the per-user Run
  entry. Removing the entry outside the app is respected; it is not silently restored.
- `--startup` / `--minimized` starts hidden in the tray and is used by the Run entry.
- `--data-dir <path>` creates a portable profile with separate settings, logs and single-instance
  identity. Portable and demo launches never touch the real startup registration.

The configuration window and the overlay are independent: closing or minimising the configuration
window does not stop monitoring.

## Known limitations

- Exclusive fullscreen games cannot be overlaid by an external topmost window. Use borderless or
  windowed fullscreen.
- Vulkan/OpenGL-only FPS titles are not covered by the DXGI/D3D9 ETW present providers.
- FPS requires administrator rights or Performance Log Users membership. The one-time permission
  grant requires a sign-out/sign-in to become active.
- If another tool stops the HardwareOSD ETW session, FPS capture is not automatically restarted.
- Fan percentage is hardware-dependent and is never derived from RPM.
- Mixed-DPI and physical multi-monitor positioning are implemented, but the validation environment
  used a single display; a full physical mixed-DPI matrix was not exercised.
- The capture duration/delay fields in Process Selection are stored preferences; automatic
  scheduling is not implemented.
- CS2 is tested with borderless/windowed fullscreen. Full gameplay/VAC coverage is not claimed.
- AMD/Intel GPU sensor coverage depends on LibreHardwareMonitor and was not validated on every GPU
  family.

## Troubleshooting and bug reports

- Check `%LOCALAPPDATA%\HardwareOSD\logs\hardwareosd-YYYYMMDD.log` for startup and sensor errors.
- Unavailable values are expected when a provider or driver does not expose the sensor.
- For reproducible bugs, include the HardwareOSD version, Windows version, GPU/CPU model, the
  relevant log excerpt and the metrics/profile in use. Remove personal paths or other sensitive
  data before posting.
- Open an issue on the project's GitHub Issues page.

## Security and privacy

HardwareOSD performs **no network access, telemetry, update checks or analytics**. It does not
inject into games, read game memory, install hooks or modify game files. The overlay is an ordinary
layered Windows window.

Administrator access is optional and is used only for hardware sensors and ETW FPS capture. The
optional FPS helper grants the current account membership in **Performance Log Users** through a
normal UAC prompt. Settings and logs remain under `%LOCALAPPDATA%\HardwareOSD`.

## License

HardwareOSD is released under the MIT License (see `LICENSE`). Third-party components and their
licenses are listed in `THIRD-PARTY-NOTICES.md`; the MPL-2.0 text is included in
`licenses/MPL-2.0.txt`.

HardwareOSD is an independent project. It is not affiliated with MSI Afterburner, RivaTuner,
NVIDIA, Valve, CapFrameX or LibreHardwareMonitor.
