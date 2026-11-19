# Architecture

HardwareOSD is a desktop process with three cooperating parts: a background sensor poller, a
lock-free snapshot cache, and a WPF overlay/settings UI. The default installation is a single
desktop process. 0.16.0 adds a built-in Windows Energy Meter/EMI provider for standard-user CPU
package, cores/IA, GT and DRAM power. 0.17.0 adds a versioned OSD configuration model, a WPF-free
layout/threshold engine, a profile store and the OSD Builder window. 0.18.0 redesigns the application
shell into horizontal top navigation and a three-zone Metrics workspace; Classic remains the default
legacy renderer path. 0.20.0 rebuilds the System page as a CapFrameX-Info-style dashboard and removes
Monitoring from navigation. The optional 0.15.0 LocalSystem fan
broker remains a separate service with a
fixed local pipe and is never installed automatically. HardwareOSD still writes no drivers and
never performs fan control or EC writes.

## Component map

| Component | File(s) | Responsibility |
| --- | --- | --- |
| Application core | `App.xaml(.cs)` | Lifetime, single instance, unhandled-exception policy |
| Runtime composition | `Services/AppRuntime.cs` | Wires config, cache, polling, overlay, tray, hotkey, timers |
| Sensor providers | `Core/Hardware/Providers/*` | Windows counters, PDH thermal zone, Windows Energy Meter/EMI, NVML, LibreHardwareMonitor (CPU/GPU/motherboard SuperIO), read-only MSI ACPI `Get_Fan` and broker client |
| MSI reader | `Core/Hardware/MsiAcpiFanReader.cs` | Shared serialized `Get_Fan` reader used by the elevated provider and the broker |
| Fan broker protocol | `Core/Hardware/FanBrokerProtocol.cs`, `MsiFanBrokerClient.cs` | Fixed versioned request/response, bounded sizes/timeouts, service detection |
| Fan broker service | `HardwareOSD.FanBroker/*` | Optional LocalSystem service, 1.5 s read-only poll, local pipe, structured log |
| Fan model | `Core/Hardware/FanSensors.cs` | Channel ids, CPU/GPU/system/pump classification, RPM/percentage pairing, audit channels |
| System inventory | `Core/SystemInfo/*` | Cached Windows WMI/registry snapshot, brand catalog, platform features, software probes, report builder |
| System page | `App/Views/SystemPageView.xaml(.cs)`, `SystemPageModel.cs`, `SystemVisuals.cs` | Reference layout, cards, tiles, bars, chips, brand wordmarks and live updates |
| System composition | `App/Services/SystemPageComposer.cs` | Maps probes/provider state/diagnostics onto the page model without creating providers |
| Merge/sanitise/derive | `Core/Hardware/SensorMergePolicy.cs`, `SensorPostProcessor.cs` | Priority merge, plausibility checks, derived metrics |
| Sensor audit | `Core/Hardware/SensorAudit.cs` | Metric/raw-sensor diagnostic table, fan-channel section and CSV export |
| Polling service | `Core/Hardware/SensorPollingService.cs` | Background thread, failure isolation, statistics |
| Cache | `Core/Hardware/SensorCache.cs` | Immutable snapshot publication |
| History | `Core/Hardware/SensorHistory.cs` | Ring buffers for sparklines |
| Metric model | `Core/Metrics/*` | Catalogue, formatting, severity thresholds |
| Layout model | `Core/Overlay/OverlayLayout.cs` | Snapshot + settings → logical rows/cells (WPF-free, unit tested) |
| Visual builder | `App/Overlay/OverlayVisualBuilder.cs` | Logical rows → measured text/geometry |
| Overlay surface | `App/Overlay/OverlaySurface.cs` | `OnRender`, sparkline geometry, background |
| Overlay window | `App/Overlay/OverlayWindow.xaml(.cs)` | Layered window, styles, monitor positioning, drag mode, topmost |
| Monitor enumeration | `App/Native/Monitors.cs` | `EnumDisplayMonitors` / `GetMonitorInfoW` / effective DPI |
| Configuration UI | `Views/MainWindow.xaml(.cs)` | Overlay, metric list, recording, live preview, positioning, settings and diagnostics |
| Application theming | `Theming/AppThemeManager.cs` + `Core/Theming/AppThemeCatalog.cs` | Five chrome themes in order Black, Dark, Light, White, Custom; full-shell custom-accent palette/contrast definitions; in-place brush updates |
| Styles | `Views/Styles.xaml` | Neutral utility palette, compact templates, dense table styling |
| Localization | `App/Localization/*` | Embedded en/uk string tables, `Loc` singleton, `LocText` markup extension |
| Native interop | `App/Native/NativeMethods.cs` | Window styles, `SetWindowPos`, hotkey, dark/light title bar |
| Colour picker | `Views/ColorPickerWindow.xaml(.cs)` | Modal RGB/HEX picker with palette, also used for the custom application accent |
| FPS capture | `Core/Fps/EtwFrameEventSource.cs` | Real-time ETW session (DXGI/D3D9 present events), elevation-aware |
| FPS timing | `Core/Fps/FrameRateTracker.cs` | Per-process ring buffers, trailing-window FPS, 1 % low, fresh-presenter snapshot |
| FPS service | `Core/Fps/FpsMonitorService.cs` | Capture callback → tracker → publisher → `FpsSnapshot`; Game Only and All Applications selection |
| FPS target selection | `Core/Fps/AllApplicationsTargetResolver.cs` | Foreground-anchored ladder: foreground, UWP child, window owner, sibling, helper, sticky |
| Process relationships | `Core/Fps/Win32ProcessRelationshipProbe.cs`, `AppProcessCatalog.cs` | Read-only image path/parent/window/UWP queries with a 30 s identity cache |
| ETW permission | `Core/Security/PerformanceLogUsers.cs` | Token/group detection, one-time UAC grant helper |
| Foreground probe | `Core/Fps/ForegroundAppProbe.cs` | `GetForegroundWindow` → pid, cached process names |
| Theme presets | `Core/Config/OverlayThemePresets.cs` | Neutral dark/light palettes and background presets |
| Hotkey gesture | `Core/Input/HotkeyGesture.cs` | Parse/format/normalise configurable shortcuts; five registered global chords (overlay, main window, start, stop, restart) |
| Hold gesture | `Core/Input/HoldGesture.cs` | Parse/format/check held modifier-only chords and modifier+key drag gestures; one-time legacy Ctrl/Shift import |
| Hotkey policies | `Core/Input/MainWindowTogglePolicy.cs`, `ModifierDragPolicy.cs` | WPF-free decisions for the main-window shortcut and held-gesture + left-button dragging |
| Recording | `Core/Recording/SensorRecorder.cs`, `RecordingTextFormatter.cs` | Bounded-queue CSV/TXT writer fed only by `SensorCache`; deduplicated sensors and source/acquisition age columns |
| OSD configuration | `Core/Config/OsdSettings.cs` | Versioned profiles, four styles, element tree, `RootLayout`, tile migration version, style settings and migration |
| OSD layout/threshold/value engines | `Core/Overlay/OsdLayoutEngine.cs`, `OsdThresholdEngine.cs`, `OsdValueFormatter.cs` | Profile elements → snapshot layout, one-row inline root, adaptive colours and per-element formatting |
| Metric bridge | `Core/Overlay/OsdMetricBridge.cs` | Materialises/toggles catalogue metrics in the active row profile and keeps Metrics and OSD Elements in sync |
| Stable widths | `Core/Metrics/MetricWidths.cs` | Worst-case value/unit samples consumed by the Classic and row layout engines |
| OSD profile transport | `Core/Overlay/OsdProfileSerializer.cs` | Versioned single-profile JSON export/import |
| Row-to-tile migration | `Core/TileOsd/RowProfileTileMigrator.cs` | Converts the five classic element trees (labels, visibility, colours, root/group layout) into editable tile documents; idempotent and preserves ids/names |
| OSD Builder | `App/Views/OverlayBuilderWindow.xaml(.cs)`, `ClassicRowsWindow.xaml(.cs)` | Tile editor for tile profiles; shared rows editor (tree, drag/drop between sections, root layout, properties, undo/redo, preview) for every row profile |
| Sensor access probe | `Core/Hardware/PawnIoAccessProbe.cs`, `SensorNotes.cs` | Read-only PawnIO/MSR explanation and stable unavailable reason codes |
| Tray | `Services/TrayIcon.cs` | `Shell_NotifyIcon`, popup menu |
| Persistence | `Core/Config/SettingsStore.cs` | JSON, atomic write, recovery |
| Logging | `Core/Diagnostics/FileLogger.cs` | Size-capped rolling log + in-memory ring |
| QA harness | `Diagnostics/VisualQa.cs`, `HardwareOSD.Cli` | Offscreen layout render, sensor and FPS diagnostics |

## Application themes (0.25.0)

`AppThemeCatalog` (Core) owns the hex palettes and the custom-accent logic; `AppThemeManager` (App)
only parses them into WPF brushes. The display order is Black, Dark, Light, White, Custom. White is
the former Light palette and keeps the persisted JSON value `light`; Black uses a pure `#000000`
background; Light is the separate cream palette. The custom theme compares the chosen accent with
the dark and light backgrounds, selects the base with better contrast, and tints every neutral
surface (background, chrome, panels, fields, borders, hover/pressed, tables, preview and System
cards/tiles) before deriving `AccentDim`, `Selection` and `AccentText` with a 4.5:1 target. The
brushes in `Styles.xaml` are mutated in place so both DynamicResource users and code-generated
controls that captured `FindResource` values update together.

## Threading rules

1. **Polling thread** (dedicated, below-normal priority) is the *only* thread that calls hardware
   interfaces. It owns the provider objects. It never touches WPF objects.
2. **UI thread** (WPF dispatcher) owns every window, the tray hook, the hotkey message window and
   all configuration state. It never calls a hardware interface.
3. Communication is one-way: polling thread → `SensorCache.Publish` (volatile write) →
   UI thread reads `SensorCache.Latest` (volatile read). No locks are shared between these two
   paths.
4. `SensorHistory` is the only shared mutable structure. Writers hold a short lock while shifting a
   small array; readers use `Monitor.TryEnter` and skip the frame instead of waiting. Therefore a
   render pass can never block on hardware polling.
5. **FPS capture thread** (ETW callback) owns only the `FrameRateTracker`; the **FPS publisher
   thread** reads the tracker and publishes immutable snapshots. UI code never touches either; it
   only reads `FpsCache.Latest`. The publisher gates measurement to a fullscreen foreground window
   (or a windowed app when opted in), holds the last real value for up to 2.5 s across ETW delivery
   gaps, then transitions to `NoGame`/`NoFrames`. The busiest-presenter fallback was removed after
   live tests showed it measuring Steam/OpenCode UI. The overlay's own process is excluded at the
   ETW callback.
6. Provider shutdown is coordinated by `SensorPollingService.Stop()`, which signals the thread,
   joins it (5 s timeout) and only then clears the cache.
7. **Recorder capture thread** (below-normal, long-running) reads `SensorCache.Latest` on a
   `PeriodicTimer` and writes samples through a bounded `Channel<RecordingSample>(240)`. A single
   **writer thread** owns the `FileStream`; the UI only calls `Start`/`StopAsync`/`Status`. No
   hardware interface is ever called from either recording thread.
8. **System inventory task** runs only on demand from the System page. `SystemInfoService` starts a
   single background collection, caches it for 60 s and returns the same immutable snapshot to
   concurrent callers. WMI/registry queries never run on the polling or rendering thread.

## UI structure (0.20)

- **Top navigation**: HardwareOSD name, System, Overlay, Metrics, Recording and a separate Settings
  action. Monitoring was removed in 0.19.0. The native Windows title bar supplies
  minimise/maximise/close. A two-row footer keeps the footer status message above, with the
  monitoring status/parameters and Save now/Reload/Reset defaults on the lower row; Pause, Hide to
  tray and Exit are anchored in its bottom-right corner. Save now reports success inline on the
  button, and the brand label shares the navigation hover tone.
- **System**: full-width OS banner, Processor | Graphics Card and Mainboard | Memory card rows,
  Storage, Software Components, Platform Security and Diagnostics. The Graphics Card name has a
  driver-reported VRAM subtitle. Flat cards, local brand wordmarks, monospaced technical text,
  progress bars and status dots; live values come from `SensorCache` and static inventory from
  `SystemInfoService`. See `docs/SYSTEM-PAGE.md`.
- **Overlay**: a persistent OSD Items list with search and per-item visibility on the left; profile
  and style selection, placement, typography, colours, appearance and live exact renderer preview
  on the right. Editing opens the existing OSD Builder rather than duplicating its property model.
- **Metrics**: fixed OSD Builder structure on the left, FPS monitoring across the centre/right top,
  searchable/category-filtered metric list in the centre bottom and metric presentation settings on
  the right bottom. FPS technical details are hidden behind a compact "..." action and closed when
  leaving Metrics. Metric rows expose a settings action; applying writes a sensor element into the
  active profile and keeps the existing metric preference table.
- **Recording**: CSV/TXT format, interval, output folder/name, per-sensor selection, live counters,
  recording history, last-file preview and a CSV validator (rows, sensors, unavailable values,
  duplicate sample/sensor keys, column count). The on-disk format is unchanged.
- **FPS Processes**: running-process search/refresh, ignore list and capture hotkey/duration/delay
  preferences. Ignored names are enforced by FpsMonitorService, which publishes NoPresenter/Ignored
  instead of a stale FPS value.
- **Settings**: General, Appearance, Language, Hotkeys, System, Diagnostics and About. The System and
  Diagnostics tabs are detail/utility views; the primary machine dashboard is the System page.
## Recording pipeline (0.13.0, extended 0.16.0)

`SensorRecorder` is deliberately a consumer, not a provider:

1. `Start(RecordingSettings, IReadOnlyList<RecordingSensor>)` normalises the settings, collapses
   duplicate sensor ids, creates a unique file (`CreateNew`, `-1` suffix on collision) and snapshots
   the chosen descriptors so the columns cannot change mid-file.
2. A long-running capture task samples `SensorCache.Latest` immediately and then on a
   `PeriodicTimer`. Each value keeps `Available` and `Note`; a missing cache entry becomes
   `Available=false, Note="sensor-not-present"`, never a zero. If the recording interval is shorter
   than the poll interval the same snapshot is sampled repeatedly; the row timestamp is the sampling
   time and the reading is not interpolated.
3. Samples go through `Channel.CreateBounded(240, Wait)`. `TryWrite` failure increments
   `DroppedSamples`; it never blocks the capture thread or grows memory without bound.
4. A single writer task owns the `FileStream`, writes a UTF-8 CSV header (BOM for Excel) or a TXT
   header, flushes every second and on stop, and updates byte/row counters.
5. `StopAsync` cancels capture, completes the channel, waits up to 10 s (3 s during disposal) and
   leaves the file readable even after a writer error. `AppRuntime.Exit` stops the recorder before
   disposing the poller.

CSV is long-format:
`Timestamp, ElapsedSeconds, SensorId, SensorName, Value, Unit, Status, Note, Source, Origin,
SensorTimestamp, AgeMs`. `Timestamp` is when the recorder sampled the cache; `SensorTimestamp` is
when that cached sensor reading was acquired; `AgeMs` is the difference, which exposes a stale
provider instead of implying a fresh hardware sample. Unavailable rows leave `Value` empty and set
`Status=unavailable`; the reason stays in `Note`. TXT writes one timestamped block per sample with
the source and age appended per value, and a footer with total samples/rows/bytes.

## Localization

- All user-visible strings live in `Localization/strings-en.json` and `strings-uk.json`, embedded in
  the main assembly (`LogicalName` set explicitly; hyphenated file names avoid satellite-resource
  inference). 0.18.0 has **847 keys in each table**, including the broker toggle, diagnostics,
  CPU power/voltage labels, OSD style names, builder controls, redesigned page labels, process
  ignore UI and unavailable-reason labels.
- `Loc` loads both dictionaries, normalises `en`/`uk`, exposes an indexer, and raises an `Item[]`
  property-changed notification on `SetLanguage` so every `LocText` binding updates immediately.
- XAML uses `Text="{l:LocText key}"`. Dynamically generated controls (settings tabs, layout segments,
  anchors, metric rows, monitor/language/log-level choices) are rebuilt by `RefreshLocalizedUi` when
  the language changes. There are no per-control language checks.
- `BehaviorSettings.Language` is persisted and initialised before the runtime/UI is constructed.
  Sensor names, technical units and source names stay in their standard form; Ukrainian descriptions
  are provided for every label, tooltip, error and diagnostic explanation.
- The tray menu and colour-picker window read the same table, so switching language updates them too.

## Overlay positioning

- `Monitors.All()` enumerates displays with `EnumDisplayMonitors`, `GetMonitorInfoW` and
  `GetDpiForMonitor`, returning physical monitor/work-area coordinates and effective scale.
- The overlay is placed with `SetWindowPos` in **physical pixels**, never with WPF's DIP window
  coordinates. Anchored mode uses the selected monitor's work area; offsets are in DIPs and are
  multiplied by the monitor scale.
- Dragged mode stores `UseCustomPosition`, `CustomX`, `CustomY` and the monitor device. If the saved
  monitor is no longer attached, the overlay falls back to the primary monitor's work area instead of
  an unreachable virtual-screen gap.
- Move mode temporarily clears `WS_EX_TRANSPARENT | WS_EX_NOACTIVATE` and enables surface hit
  testing; releasing the mouse persists the position and re-applies the click-through styles.
  Cancel restores the previous anchored position. The overlay remains independent of the settings
  window throughout.
- Hold-to-drag (0.25.0) keeps `WS_EX_NOACTIVATE` and `WS_EX_TOOLWINDOW` but toggles only
  `WS_EX_TRANSPARENT` while the assigned hold gesture is down and the cursor is over the overlay.
  The gesture is parsed by `HoldGesture` (modifier-only or modifier+key) and stored in
  `BehaviorSettings.HotkeyDragOverlay`. A 45 ms dispatcher timer runs
  `ModifierDragPolicy.ShouldArm`; the surface draws a panel border and hovered-cell highlight, and
  after a drag the policy stays suppressed until the gesture is released, so click-through cannot
  get stuck off. The overlay never activates or steals focus.

## Classic profile tile migration (0.26.0)

`RowProfileTileMigrator` runs from `OsdSettings.Normalize` after the legacy Classic metric-matrix
sync. It converts only the five original ids that still have `Layout=Rows`; a profile already stored
as a tile is never rebuilt. The first conversion is preceded by `settings.json.pre-0.26.bak` and the
result is persisted immediately. `ClassicRowsWindow` is no longer reachable for those ids; only the
three CapFrameX row templates keep the legacy element engine and rows editor. `TileNode.Visible`,
`OsdMetricBridge` tile methods and the extended `TileSourceCatalog` keep Metrics, OSD Elements and
the builder on one registry.

## System information

- `WindowsSystemInfoCollector` queries `Win32_OperatingSystem`, `Win32_Processor`,
  `Win32_PhysicalMemory`, `Win32_VideoController`, `Win32_BaseBoard`, `Win32_BIOS`,
  `Win32_DiskDrive`, `Win32_DiskPartition`, `Win32_LogicalDisk` and `MSFT_PhysicalDisk`.
  VRAM comes from the display-driver registry `HardwareInformation.qwMemorySize`, not the capped
  `Win32_VideoController.AdapterRAM`.
- Each group is isolated: a failed query produces rows with `N/A` and a reason such as
  `wmi-unavailable`, `wmi-access-denied`, `not-reported`, `shared-memory` or
  `storage-type-unavailable`. No fallback values are guessed from a model name.
- `SystemInfoService` caches one immutable snapshot for 60 s and coalesces concurrent requests. The
  System page starts it only when opened; Settings → System offers an explicit Refresh.
- `SystemPageView` renders the reference layout from `SystemPageModel`; fast-changing values and
  bars update in place on the runtime tick, while static inventory is rebuilt on page entry.
  `SystemPageComposer` maps live providers, ETW/recording state and software probes onto the model.
- `SystemReportBuilder` produces the **Copy System Info** clipboard report. It is intentionally
  English and stable, with `N/A`/`Unknown` for absent data.
- Long names are trimmed with a tooltip, so the 920×600 layout does not overflow. The offscreen QA
  renderer asserts zero horizontal overflow in EN and UK at both required viewports.

## OSD engine and profiles (0.17.0)

- `OsdSettings` owns the active profile id and a versioned list of `OsdProfile` objects. Each
  profile has a style (`Classic`, `HighContrast`, `BlueLabels`, `AdaptiveColors`), an independent
  `OsdStyleSettings`, and a recursive element tree.
- The four default profiles are created from the current metric preferences on migration. Classic
  keeps `FollowMetricPreferences=true`, so it uses the original layout path and remains visually
  identical after upgrading. Editing its structure in the builder sets the flag to false and moves
  it to the same element engine as every other style.
- Element kinds: Sensor, Group (nested), Group Header, Device Header, Custom Text, Empty Line,
  Divider, Graph, Progress Bar and Custom Value. Groups can use `Columns` (one Classic row with
  multiple values) or `Rows` (one value per line).
- `OsdLayoutEngine` builds an immutable `OverlayLayout` from a profile and a `SensorSnapshot`; it
  never accesses hardware. `OsdValueFormatter` resolves metric/raw sensor ids, display names, units,
  decimal places, custom formats and prefix/suffix. `OsdThresholdEngine` applies user thresholds,
  lower-is-worse FPS semantics and the real GPU power limit; unavailable values keep an explicit
  reason and are never replaced with estimates.
- The builder keeps an in-memory draft. Edit operations push deep-cloned profiles onto a bounded
  undo stack; Apply writes the draft through `AppRuntime.ApplySettings` and SaveNow, Revert reloads
  the last applied settings. The preview uses `SensorOrigin.Demo` values only for missing sensors
  and draws a localized PREVIEW badge; the real overlay never receives demo values.
- `OsdProfileSerializer` wraps one profile in a versioned JSON envelope for import/export. The full
  set of profiles lives in the existing `settings.json` under `osd`, alongside all prior settings.

## 0.22.0 audit fixes

- The in-app `HardwareOSD` title is a plain `TextBlock`: no hover fill, no pointer cursor and no
  click handler. It is intentionally non-interactive.
- The bottom action/status footer keeps the two-row structure but its content height is about 53 px
  (previously about 70 px): status text 14 px, 4 px gap, 26 px action buttons and 4 px vertical
  padding. Width, controls and handlers are unchanged.
- `MainWindow` appearance/typography/colour/Surface controls now resolve the active profile:
  Classic writes `OverlaySettings` and syncs its profile style block; every other profile writes
  its own `StyleSettings`. Switching profiles reloads the controls, so one profile cannot overwrite
  another. The tile engine continues to use the design document's theme; tile profiles keep their
  own sparse overrides.
- CapFrameX tile text and artwork are separated from hardware identity. Static vendor claims were
  replaced with real tokens; image nodes can be gated by `vendor`/`model` and are omitted when the
  installed CPU/GPU does not match. Real names come from `SystemInfoService`, never from a profile
  name.
- Contrast fixes were applied to BLUELINE, CHERRY BLOSSOM, REDLINE and OLED Minimal; the vendor and
  contrast regressions are covered by `TileOsdTests`.

## Tile OSD engine (0.21.0)

- The additive library is split by `OsdProfile.Library` (`HardwareOsd` / `CapFrameX`) and
  `OsdProfile.Layout` (`Rows` / `Tile`). The original five profiles keep `Library=HardwareOsd`
  and their existing row path; nothing in `OsdLayoutEngine` is used for tile profiles.
- `TileDesignCatalog` loads 45 embedded version-1 design documents plus `catalog.json`. The
  documents are immutable resources converted from the saved CapFrameX 1.9.2 library by
  `tools/build-tile-catalog.py`; they contain no CapFrameX code and no runtime dependency.
- `TileSceneBuilder` turns a design, a `SensorSnapshot`, an optional `FpsSnapshot`, a
  `SensorHistory` and `TileOverrideSettings` into an immutable `TileScene`. It resolves metric
  keys through `TileSensorMap`, applies unit conversion and overrides, and emits explicit `N/A`
  for missing readings. It never reads hardware.
- `TileSceneRenderer` draws the scene into a WPF `DrawingContext`; `OverlaySurface` hosts tile
  content directly, so the live overlay and the builder preview share one renderer. Thumbnails use
  the same path at catalog size.
- `OverlayWindow` sizes a tile overlay to the design canvas (520x504 for Benchmark) and applies the
  per-profile tile scale, opacity and node overrides. `AppRuntime` builds `TileRenderContext` from
  cached system info once per render, so GPU/CPU names and the session clock never trigger a live
  hardware call.
- Persistence is additive: `OsdSettings.CapFrameXLibraryVersion` seeds the 48 new profiles once,
  and `TileOverrideSettings` stores sparse per-node edits keyed by the editor path. The original
  five profiles and the Classic migration path are untouched.
- Row parity lives in `CapFrameXRowTemplates`, which creates ordinary `OsdProfile` objects on the
  existing element model with `OsdVendorRole` colours resolved at render time. See
  `docs/OSD-DESIGNS.md` for the model, the 45-design table and the verification commands.

## CPU power sources (0.16.0)

- `WindowsEnergyMeterProvider` reads the built-in Windows `Energy Meter` performance-counter
  category. This is the Windows Energy Meter Interface (EMI): a read-only OS provider that exposes
  Intel RAPL energy to standard users on Windows 11. No CapFrameX, HWiNFO, MSI Center, WinRing0 or
  PawnIO driver is involved.
- Instance mapping: `RAPL_Package0_PKG` → `cpu.power`, `RAPL_Package0_PP0` →
  `cpu.power.cores` (IA/cores), `RAPL_Package0_PP1` → `cpu.power.gt`, `RAPL_Package0_DRAM` →
  `cpu.power.memory`. The EMI `Power` counter is in **milliwatts**; it is divided by 1000 for watts.
- There is no separate IA metric: on this platform PP0 *is* the cores/IA domain. DRAM is published
  only when its energy counter advances; this laptop reports zero energy for DRAM, so the metric is
  `N/A` with `energy-meter-channel-unavailable` rather than a fabricated 0 W.
- When elevated, LibreHardwareMonitor's MSR-backed package/cores/memory power and CPU Core voltage
  have higher per-sensor rank. Windows EMI remains the standard-user fallback. The selected value
  and source are visible in Metrics, the diagnostics audit and the recording CSV.
- CPU package/core temperature, per-core clocks and CPU voltage still require the LHM MSR path;
  EMI does not expose them.

## Fan monitoring

- Fan channels have stable per-physical-channel ids (`fan.cpu.0.rpm`, `fan.gpu.0.percent`, etc.).
  User-facing metrics `cpu.fan.rpm`, `cpu.fan.percent`, `gpu.fan.rpm` and `gpu.fan.percent` aggregate
  the matching channels. Multiple values are joined, never averaged.
- GPU channels come from LibreHardwareMonitor's NVAPI (`CurrentRpm`/current level) and ADL paths,
  with NVML per-fan percentage and driver-reported RPM as a fallback. The merge policy prefers LHM's
  paired channel when both APIs expose the same fan, then MSI ACPI, then NVML.
- MSI notebooks use a shared `MsiAcpiFanReader`: read-only `root\wmi:MSI_ACPI.Get_Fan` with a fixed
  32-byte package. The four big-endian period words decode as `RPM = 480000 / period`. The vendor
  class ACL requires an elevated process; non-elevated entries carry
  `msi-acpi-requires-elevation`. Fan 1 is CPU, fan 2 is GPU; 3/4 are included only when reported.
- 0.15.0 adds an optional LocalSystem broker and `MsiFanBrokerProvider`. It is selected only when
  the app is not elevated, broker integration is enabled, and the service is installed. The service
  owns the WMI reader and publishes an immutable fixed-size snapshot; the client sends only
  `GetFanSnapshot` over a fixed named pipe. The elevated path uses `MsiAcpiFanProvider` directly.
  Exactly one MSI source is registered, so the same fan id is never published twice.
- CPU/system channels also come from LibreHardwareMonitor's Motherboard/SuperIO fan and control
  sensors. Names classify CPU Fan/CPU Opt, System/Chassis/AUX/MOS/VRM/Chipset, Pump and unknown
  channels. Pumps and unknown generic channels never contribute to CPU/GPU metrics.
- Percentage is only mapped when the hardware exposes a control/PWM channel; it is never derived
  from the session maximum RPM. MSI `Get_Fan` has no percentage channel, so its percentage metrics
  stay `N/A` with `msi-fan-no-percentage`.
- Unavailable channels are published explicitly with reason codes, not dropped. The sensor audit CSV
  contains a `fan_channel_id,...,rpm,percent,...` section with component, name, hardware, source API,
  timestamp and availability. See `docs/FANS.md`.

## Data flow

```
providers ──► merge (per-sensor rank) ──► sanitise ──► derive ──► snapshot (seq, timestamp)
                                                                       │
                                       ┌───────────────────────────────┤
                                       ▼                               ▼
                                 SensorCache.Latest            SensorHistory.Append

ETW present events ──► FrameRateTracker ──► FpsMonitorService (100–250 ms) ──► FpsCache.Latest
                                                                                  │
                                       ┌──────────────────────────────────────────┤
                                       ▼                                          ▼
                      settings ──► OverlayLayoutBuilder ──► OverlayVisualBuilder ──► OverlaySurface
```

Rendering is sequence-gated on **both** caches: if neither the sensor snapshot nor the FPS snapshot
sequence has changed and no settings change is pending, the frame is skipped entirely. Sparklines
advance when their series advance.

## Snapshot design

```csharp
sealed class SensorSnapshot {
    long Sequence;                 // monotonically increasing publication counter
    long CapturedAtMs;
    IReadOnlyDictionary<string, SensorValue> Values;
}

readonly struct SensorValue {
    string Id;                     // stable id, e.g. "gpu.temp.hotspot"
    double Value; string Unit;
    SensorKind Kind;               // Temperature/Load/Clock/Power/Voltage/Data/Fan
    SensorOrigin Origin;           // HardwareSensor/WindowsApi/Derived/Demo
    string Source;                 // human readable provenance
    long TimestampMs;
    bool Available;                // explicit "known unknown" instead of a missing key
}
```

`Available=false` keeps a stable layout: the formatter renders `N/A`, the UI can explain the
source, and a later poll can replace the same key without changing the layout.

## Rendering details

- The overlay is a WPF `Window` with `AllowsTransparency=true`, `WindowStyle=None`,
  `SizeToContent=WidthAndHeight`, `ShowActivated=false`, `Topmost=true`.
- Themes and colours come from `OverlaySettings` (theme preset + per-role colours + background
  colour/opacity). `OverlayThemePresets` supplies neutral dark (`#000000` background) and light
  (`#F2F1EE`) palettes, plus four background presets. Changing a colour or theme rebuilds the frozen
  theme and repaints immediately; no restart is involved.
- Extended styles are applied on `SourceInitialized`: `WS_EX_LAYERED | WS_EX_TOOLWINDOW
  | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE` (the last two only while click-through is enabled).
- The content is one `OverlaySurface : FrameworkElement`. `MeasureOverride` returns the size
  computed by the visual builder, so the window auto-sizes exactly.
- All brushes, pens and typefaces are frozen and rebuilt only when settings change. Text is
  `FormattedText` (Grayscale antialiasing, Display formatting mode). Numeric cells reserve width
  from the metric's sample string, so numbers changing from `9` to `100` never move the layout.
- Reserved-width columns are why the overlay does not "breath" at 4 Hz.
- Layered windows are software-composited by WPF; this keeps GPU usage at ~0 % by design, at the
  cost of a higher memory baseline (tracked as a known limitation).

## Error policy

- Provider read failures are counted per provider. First failure logs a warning; the poll continues
  with the remaining providers. After three consecutive failures the provider is disposed and
  re-initialised on a 15 s backoff, so a driver reset can recover without a restart.
- Sensor-level implausibility is converted to `Available=false` rather than throwing.
- Config parse failures quarantine the file and fall back to defaults.
- UI exceptions are logged and surfaced once; the overlay keeps running for non-fatal errors.
- All logging is best-effort and can never throw into the application.

## Extension points

- New sensor source: implement `ISensorProvider`, register it in `SensorProviderFactory`, add ranks
  in `SensorMergePolicy` if it competes with an existing source.
- New metric: add a `MetricDefinition` to `MetricCatalog`, a formatting case in `MetricFormatter`,
  and (optionally) a source mapping in `MainWindow.DescribeSource`.
- New layout: extend `OverlayLayoutMode` and `OverlayVisualBuilder`; the settings UI derives its
  segmented control from the enum.
