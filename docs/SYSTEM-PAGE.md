# System page (0.25.0)

The first navigation page is a visual reproduction of the CapFrameX Info screen, driven only by
real Windows inventory, the existing sensor cache and local software probes. The design source was
the supplied screenshot and its pixel specification; no CapFrameX source or XAML was consulted.

The Settings -> System and Settings -> Diagnostics tabs, which were present in XAML but unreachable
through 0.24.0, are restored in 0.25.0. They host the system inventory, polling/refresh, provider
toggles, live cache, ETW/fan diagnostics and the sensor-audit export.

## Layout

| Region | Height | Contents |
| --- | --- | --- |
| OS banner | 56 | Windows glyph, `OPERATING SYSTEM`, caption + build, `COPY SYSTEM INFO` |
| Hardware row 1 | 211 | Processor (left) / Graphics Card (right) |
| Hardware row 2 | 211 | Mainboard (left) / Memory (right) |
| Storage | auto | directly above Software Components |
| Software Components | auto | four equal cards in a 4-column grid |
| Platform Security | auto | four status chips in the same 4-column grid |
| Diagnostics | auto | three-column status chips |

Content margins are 17 left / 19 right / 17 top / 22 bottom, the column gutter is 11 and the
vertical gap between sections is 11. The two hardware columns are `*`-sized and therefore stay
equal at every window width.

## Design tokens

| Role | Value |
| --- | --- |
| Page background | `#252526` |
| Card / section surface | `#2D2D30` |
| Tile, row, progress track | `#474749` |
| Accent, progress fill, Copy action | `#0271F9` |
| Heading text | `#EAEAEB` |
| Tile value | `#EDEDED` |
| Body / technical text | `#C5C5C5` |
| Eyebrow, section header, subtitles | `#BDBDBE` |
| Icons | `#AAAAAB` |
| Success dot | `#4CAF50` |
| Warning dot | `#FF9800` |
| Brand tiles | `#0068B5` Intel, `#76B900` NVIDIA, `#E4002B` MSI, `#2E7BBF` Crucial, plus local mappings for other vendors |
| Wordmarks | `#FFFFFF` |

Radii: card 6, brand tile 5, inset tile/row 4, progress 3. There are no borders, shadows or
dividers; edges come only from fill contrast.

## Typography

- Titles and eyebrows: application `UiFont` (Segoe UI), semibold/bold.
- Technical labels, card subtitles, rows, chip text and values: application `MonoFont`
  (Cascadia Mono → Consolas).
- Large percentages: 26 px light; tile values 16 px bold; eyebrow 9.5 px bold uppercase; technical
  text 11.5–12 px.

## Data mapping

| Card | Source |
| --- | --- |
| OS | `SystemInfoSnapshot.Windows` (`os.name`, `os.build`) |
| Processor | `cpu.name` cleaned of (R)/(TM)/clock suffix, `cpu.cores`, `cpu.threads` |
| Graphics Card | `SystemInfoSnapshot.Gpus.PrimaryGpu()` (discrete adapter wins), its reported `Vram` subtitle, live NVML/Windows sensors |
| Mainboard | `board.vendor`, `board.model`, `bios.version`; ReBAR/HAGS/Game Mode from `PlatformFeatureInfo` |
| Memory | `ram.total`, `ram.module_config`, `ram.speed`, `ram.manufacturer`; live RAM sensors |
| Storage | `SystemInfoSnapshot.Storage` |
| Software Components | `SoftwareComponentProbe` + live provider state (PawnIO, broker, NVML, LHM) |
| Platform Security | `PlatformSecurityInfo.Read()` |
| Diagnostics | `SystemPageComposer.BuildDiagnostics` from live providers, ETW, recording and FPS state |

Values are never synthesized. A missing sensor or registry value renders `N/A` or `Unknown` and a
grey dot. Power is shown as whole watts, temperatures as whole °C, clocks as whole MHz, VRAM and RAM
with one decimal GB. The Graphics Card subtitle is the driver-reported VRAM (`12 GB VRAM` on the
validation laptop); when the driver does not report it, the explicit
`VRAM size not reported by the driver` note is shown instead. The subtitle uses the same style and
position as the processor's core/thread line.

## Verification

```powershell
# Offscreen EN/UK renders at both required viewports plus an overflow report.
HardwareOSD.exe --render-system C:\Temp\hwosd-system-qa
```

The 0.20.2 render reports the same page size as 0.20.0 (1047 × 1167 dp at a 1080 window and
887 × 1167 dp at 920) with **0 horizontal overflow elements** in EN and UK at both viewports. The
full page shows Storage, Software Components, Platform Security and Diagnostics in that order. The
QA renderer does not crop the page to the window: the viewport crop and the full page are both
written so the order and below-the-fold extras can be inspected.

## Known confines

- Storage is directly above Software Components; Platform Security and Diagnostics follow it. The
  lower sections are outside the visible 1080×720 viewport and were checked in the full-page capture
  and by scrolling the live window.
- The reference defines only the dark palette. The light theme maps the same roles to neutral
  surfaces for readability; it is not a reference reproduction.
- Wordmark tiles are text, not vendor artwork. This keeps every asset local and avoids trademark
  image files.
- Mixed-DPI and multi-monitor rendering was not re-measured for this page.
