# CapFrameX-inspired OSD design library (0.21.0)

HardwareOSD 0.21.0 adds a second, independently versioned OSD design library. It is additive:
the five original HardwareOSD profiles, their ids, their names, the Classic layout path and every
persisted key are unchanged. Existing `settings.json` files gain 48 new profiles on first load and
never lose or rewrite the old ones.

The library is a **design recreation**, not a runtime integration. HardwareOSD has no reference to
CapFrameX binaries, DLLs or source. The saved design documents were used as a specification and
converted into HardwareOSD's own scene model.

## Two engines, one profile list

| | Row profiles | Tile profiles |
| --- | --- | --- |
| Engine | `OsdLayoutEngine` (0.17.0) | `TileSceneBuilder` + `TileSceneRenderer` (0.21.0) |
| Geometry | flowing rows, sized by text | absolute canvas, every widget has a frame |
| Sensor binding | `OsdElement.SensorId` | tile node `key` mapped through `TileSensorMap` |
| Added in 0.21.0 | 3 CapFrameX row templates | 45 saved tile designs |
| Profile id | `cfx-row-basic`, `cfx-row-detailed`, `cfx-row-enthusiast` | `cfx-tile-<design id>` |
| Persistence | existing element tree | `designId` + sparse `tile` overrides |

`OsdProfile` gained three fields: `Library` (`HardwareOsd` / `CapFrameX`), `Layout`
(`Rows` / `Tile`) and `DesignId` for tile profiles. A tile profile stores only user changes in
`TileOverrideSettings`, never a copy of the design. The catalog stays immutable in the assembly.

## Tile model

`TileDesignDocument` (`src/HardwareOSD.Core/TileOsd/TileDesign.cs`) is a version-1 JSON document:

- `theme` - panel, accent, text and subtext as `#RRGGBBAA`, font family, base/title size, padding,
  gap and radius.
- `root` - a canvas panel with `width`/`height` and absolutely positioned children.
- Node kinds: `panel`, `text`, `metric`, `chart`, `bar`, `gauge`, `image`, `heatmap`,
  `classicRows`.
- `frameWidth`/`frameHeight` and `x`/`y` are copied from the saved editor frames. Flow panels fall
  back to the layout rule; explicit frames win. A single-child wrapper panel gives its child the
  padded content box, which is how the CapFrameX compiler wraps top-level tiles.

The renderer (`TileSceneRenderer`) draws the immutable `TileScene` produced by `TileSceneBuilder`.
It supports solid and gradient panels, rounded corners, text, metric values and units, secondary
values, charts with history, horizontal and vertical bars, ring/arc gauges, embedded PNG images and
heatmap grids. No WPF element is created per widget per frame.

## Sensor binding

`TileSensorMap` maps the reference keys used by the saved designs (`fps`, `fps1pctLow`, `gpuLoad`,
`gpuTemp`, `gpuPower`, `cpuLoad`, `cpuTemp`, `ramUsed`, `vramUsed`, `gpuClock`, `cpuClock`, ...)
to HardwareOSD sensor ids. It also handles:

- aliases and role-based keys (`cpu-loads`, thread/core bindings);
- base-unit conversion (MB to GB, MHz to GHz, milliseconds);
- value scaling, decimal places and unit overrides;
- explicit `N/A` when a sensor is missing. Missing readings are never replaced with zero, a guess
  or another sensor's value.

Demo values exist only in the **builder preview**; they carry `SensorOrigin.Demo` and a PREVIEW
badge. The live overlay always receives the real snapshot.

## Row templates

`CapFrameXRowTemplates` recreates the three classic templates on the existing element model. They
are ordinary `OsdProfile` objects with `Library=CapFrameX`, `Layout=Rows` and vendor roles:

- **Basic** - GPU clock/memory/load/temp; CPU max/total/package/temp; average and 1% low;
  framerate and frametime graphs.
- **Detailed** - adds GPU power/VRAM, CPU model and the RAM block.
- **Enthusiast** - adds VRAM hot spot, 16 core rows (clock, load, temperature), RAM and PC latency.

Group colours are resolved at render time from the installed GPU/CPU vendor: NVIDIA `#76B900`,
AMD `#ED1C24`, Intel `#0071C5`; RAM `#87CEEB`, metrics `#9CD200`, framerate `#AEEA00`, latency
`#0271F9`. Values are gold `#FFD700`. Unsupported reference rows stay explicit `N/A` rows.

## Editing the five original profiles (0.26.0)

Classic, Green HUD, Split HUD, Info HUD and Minimal Rings are tile documents in 0.26.0. Their
ids and names are unchanged; `RowProfileTileMigrator` reads the existing element tree (labels,
visibility, colours, group/root layout and metric order) and writes ordinary tile nodes. All five
open the tile editor from Overlay -> Open OSD Builder; the old rows editor is now reachable only for
the three preserved CapFrameX row templates. The tile editor exposes the layer tree, containers,
add/duplicate/delete, drag/drop, canvas X/Y/width/height, per-tile value/label size, font family,
alignment, colours, charts/gauges, undo/redo and the live preview.

The first migration writes `settings.json.pre-0.26.bak`, converts the five profiles and persists the
result immediately. A profile already stored as a tile is never rebuilt, so repeated launches cannot
overwrite builder edits. `RootLayout` values from 0.25 are honoured during conversion (stacked rows
or one horizontal line); after conversion the tile canvas itself is the layout source.

## Persistence and migration

- `OsdSettings.CapFrameXLibraryVersion` starts at `1`. On load, `SeedCapFrameXProfiles()` adds the
  48 new ids once. Re-normalising does not duplicate them; deleting a design does not resurrect it
  because the library version is already current.
- The original five profiles are still enforced by `CreateReferenceProfiles` exactly as before.
  A default installation therefore has 53 profiles: 5 original + 45 tiles + 3 rows.
- Tile overrides are keyed by the editor path (`"1"`, `"1/0"`, ...) and store only changed fields:
  key, secondary key, label, unit, visibility, digits, scale, colours, chart thickness, bar range,
  value size and order.
- The profile list is grouped in the Overlay page and in the OSD Builder under
  "HardwareOSD designs" and "New Design". The builder shows the tile family filter,
  design list with rendered thumbnails, canvas size, tile scale/opacity and a live preview.

## Catalog

45 designs in 8 families. Canvas sizes are design pixels at 100% zoom; the live OSD window equals
the canvas, and `--render-tiles` adds 8 px of transparent QA padding per side.

| Family | Design | Canvas | Nodes | Metrics | Charts | Bars | Gauges |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Anime | CHERRY BLOSSOM - Anime | 304 x 435.2 | 27 | 3 | 1 | 0 | 4 |
| Anime | LUNAR - Anime | 304 x 467.2 | 27 | 7 | 1 | 0 | 0 |
| Anime | NEON NIGHT - Anime | 304 x 441.6 | 27 | 3 | 1 | 4 | 0 |
| Core Types | Core Types Compact | 480 x 280 | 23 | 4 | 0 | 8 | 0 |
| Core Types | Core Types Overview | 600 x 448 | 35 | 4 | 0 | 16 | 0 |
| Core Types | Core Types Thermal | 560 x 456 | 35 | 4 | 0 | 16 | 0 |
| Everyday | Benchmark | 520 x 504 | 17 | 4 | 1 | 1 | 1 |
| Everyday | Compact | 384 x 320 | 9 | 2 | 0 | 2 | 0 |
| Everyday | Frame Pacing | 664 x 376 | 11 | 2 | 1 | 1 | 0 |
| Everyday | GPU Dashboard | 512 x 432 | 11 | 2 | 0 | 1 | 1 |
| Everyday | Memory Watch | 392 x 424 | 11 | 1 | 1 | 2 | 0 |
| Everyday | Minimal | 240 x 320 | 5 | 2 | 0 | 0 | 0 |
| Everyday | OLED Minimal | 232 x 68 | 9 | 3 | 1 | 0 | 0 |
| Everyday | Performance Strip | 1920 x 330 | 11 | 3 | 2 | 0 | 0 |
| Hardware | APEX 3D - RTX 50 Series + Ryzen 3D | 338 x 491.4 | 69 | 16 | 1 | 0 | 0 |
| Hardware | BLACKWELL THERMALS - RTX 5060 & 5060 Ti 8 GB | 440 x 520 | 41 | 11 | 0 | 0 | 0 |
| Hardware | BLACKWELL THERMALS - RTX 5070 | 440 x 520 | 45 | 13 | 0 | 0 | 0 |
| Hardware | BLACKWELL THERMALS - RTX 5080 & 5070 Ti | 440 x 520 | 49 | 15 | 0 | 0 | 0 |
| Hardware | BLACKWELL THERMALS - RTX 5090 | 440 x 520 | 65 | 23 | 0 | 0 | 0 |
| Hardware | BLUELINE - Nova Lake | 312 x 504.4 | 129 | 14 | 1 | 56 | 0 |
| Hardware | DUOLINE - Arc + Intel Core | 240 x 492 | 37 | 14 | 1 | 0 | 0 |
| Hardware | DUOLINE - GeForce RTX + Intel Core | 240 x 492 | 37 | 14 | 1 | 0 | 0 |
| Hardware | DUOLINE - GeForce RTX + Ryzen | 240 x 492 | 37 | 14 | 1 | 0 | 0 |
| Hardware | DUOLINE - Radeon + Intel Core | 240 x 492 | 37 | 14 | 1 | 0 | 0 |
| Hardware | DUOLINE - Radeon + Ryzen | 240 x 492 | 37 | 14 | 1 | 0 | 0 |
| Hardware | FUSION ARL - RTX 50 Series + Intel Core Ultra | 338 x 725.4 | 98 | 16 | 1 | 24 | 0 |
| Hardware | FUSION RPL - RTX 50 Series + Intel Core | 338 x 725.4 | 106 | 16 | 1 | 32 | 0 |
| Hardware | FUSION Ryzen - RTX 50 Series + Ryzen | 338 x 696.8 | 84 | 15 | 1 | 16 | 0 |
| Hardware | GREENLINE - GeForce RTX | 520 x 344 | 47 | 15 | 1 | 0 | 0 |
| Hardware | ICELINE - Vertical | 260 x 620 | 51 | 17 | 1 | 0 | 0 |
| Hardware | Panther Lake - Arc Aurora | 312 x 395.2 | 69 | 13 | 1 | 16 | 0 |
| Hardware | REDLINE - Ryzen + Radeon | 338 x 668.2 | 78 | 15 | 1 | 16 | 0 |
| Hybrid | Hybrid Benchmark | 640 x 432 | 9 | 2 | 1 | 0 | 0 |
| Hybrid | Hybrid Compact | 480 x 320 | 5 | 1 | 0 | 0 | 0 |
| Hybrid | Hybrid Thread Deck | 840 x 400 | 13 | 2 | 0 | 2 | 0 |
| Slick | Slick Overlay - Compact 2x2 | 483 x 175.5 | 83 | 19 | 1 | 0 | 0 |
| Slick | Slick Overlay - Single Line | 1877.5 x 80 | 83 | 19 | 1 | 0 | 0 |
| Slick | Slick Overlay - Vertical Panel | 247.5 x 352.5 | 83 | 19 | 1 | 0 | 0 |
| Slick | Slick Overlay - Wide 3 Rows | 910.5 x 91.5 | 83 | 19 | 1 | 0 | 0 |
| Turquoise | Turquoise Compact | 400 x 520 | 41 | 13 | 1 | 0 | 0 |
| Turquoise | Turquoise Reference | 400 x 720 | 91 | 18 | 1 | 25 | 2 |
| Turquoise | Turquoise Wide | 808 x 460 | 81 | 18 | 1 | 16 | 2 |
| Vertical | Vertical Clock Deck | 640 x 320 | 15 | 2 | 1 | 2 | 0 |
| Vertical | Vertical Thermal Panel | 640 x 328 | 19 | 2 | 0 | 4 | 0 |
| Vertical | Vertical Thread Strip | 560 x 344 | 19 | 4 | 1 | 2 | 0 |

The source of truth is `src/HardwareOSD.Core/TileOsd/catalog.json`; each row also carries font
family/size and palette tokens. The design documents are embedded resources (about 2.75 MB total,
mostly the three anime images and the six Blackwell/Fusion composites).

## Verification

- `dotnet build HardwareOSD.sln -c Release -p:Platform=x64` - 0 warnings, 0 errors.
- `dotnet test HardwareOSD.sln -c Release` - 318 tests, 0 failed. The tile suite alone covers the
  45-design catalog, embedded image decoding, every scene building without non-finite geometry,
  real-value/N/A behaviour, unit conversion, overrides, chart history, row composition, one-time
  seeding, old-profile preservation and JSON round-trip.
- `HardwareOSD.exe --render-tiles <dir>` - 45 PNGs + `tile-render-manifest.csv`; every PNG was
  checked as canvas + 16 px (8 px per side).
- Live UI Automation checks verified tile selection and persistence, the 53-profile library, the
  520x504 live overlay, and restoration of the selection and overlay after exit and restart.
- The OSD Builder preview exposed the tile library and design list with the
  `CapFrameX tile preview · 520 × 504` label.
- Offscreen rendering produced the Benchmark and Turquoise Reference tile captures.

## Adding a design

1. Put a version-1 design JSON into `src/HardwareOSD.Core/TileOsd/Designs/` and add its entry to
   `catalog.json` (id, name, family, size, font, palette, counts).
2. Keep the id stable. A new id is seeded once when `CurrentCapFrameXLibraryVersion` is bumped;
   changing an existing design changes every user who has not overridden it.
3. Use only keys supported by `TileSensorMap`; unknown keys render `N/A`.
4. Run the tile tests and `--render-tiles`, then compare the PNG with the reference render.

## 0.22.0 audit and identity policy

HardwareOSD 0.22.0 keeps the same 45 tile designs and 3 row templates. It does not add new ids or
reset existing profiles. The library remains 5 original HardwareOSD profiles + 3 CapFrameX row
templates + 45 tile designs = 53 profiles.

- **Template names are not hardware facts.** A name such as `APEX 3D - RTX 50 Series + Ryzen 3D` is
  a profile/template name. Rendered CPU/GPU names come from `SystemInfoService` through
  `TileRenderContext` and `OsdRenderContext`; they are never taken from the profile name.
- **Vendor-aware static text.** Vendor/marketing text in designs was replaced with neutral labels or
  real tokens (`{cpuVendor}`, `{gpuVendor}`, `{cpuShort}`, `{gpuShort}`). On an Intel + NVIDIA host,
  APEX renders `INTEL`, `i9-12900HX`, `NVIDIA` and `RTX 4080 Laptop`; it no longer renders AMD
  Ryzen/X3D/Blackwell/RTX 50 claims.
- **Vendor-aware artwork.** Image nodes can carry `vendor` and `model` gates. AMD Ryzen and Intel
  Core Ultra logos are hidden when the installed CPU does not match; NVIDIA logos are hidden on
  non-NVIDIA GPUs. The layout and metrics remain.
- **Contrast.** BLUELINE, CHERRY BLOSSOM, REDLINE and OLED Minimal label colours were raised to
  improve readability. No built-in text uses black on a dark panel.
- **Regression tests.** `TileOsdTests` covers vendor text, vendor artwork gating and real CPU/GPU
  values for APEX/FUSION/REDLINE; the full catalog still builds finite geometry for every design.

## Known limits

- The renderer is a faithful recreation of the saved frames and theme tokens, not a byte-identical
  clone of CapFrameX's native compositor. Chart-panel shading and some anti-aliasing differ.
- Heatmap and image widgets are supported by the scene model; only designs that use them exercise
  them. No editor for creating free-form tile geometry was added - users edit sensors, labels,
  units, visibility and presentation of existing widgets.
- Row templates use the existing row renderer, so graph height and font metrics follow HardwareOSD's
  row layout rather than CapFrameX's RTSS renderer.
- Mixed-DPI and multi-monitor tile placement were not re-tested in 0.21.0.
