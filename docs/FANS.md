# Fan monitoring — implementation and validation status

## Summary

HardwareOSD supports four optional fan metrics:

| Metric id | Label | Unit |
| --- | --- | --- |
| `cpu.fan.rpm` | CPU fan speed (RPM) | RPM |
| `cpu.fan.percent` | CPU fan speed (%) | % |
| `gpu.fan.rpm` | GPU fan speed (RPM) | RPM |
| `gpu.fan.percent` | GPU fan speed (%) | % |

All four are disabled by default, can be enabled independently in **Metrics** and are persisted in
`settings.json` with the other metric preferences. Existing settings that enabled the old
`gpu.fan` value are migrated to the new GPU RPM and percentage rows.

Multiple physical fans are displayed as separate values (`1200/900 RPM`), never summed, averaged or
interpolated. Pumps are classified separately and never contribute to CPU or GPU fan metrics.

## Root cause on the validation laptop (0.14.0)

The validation machine is an **MSI Vector GP68HX 12VH / MS-15M1** (i9-12900HX, RTX 4080 Laptop).
For 0.11–0.13 the fan metrics showed `N/A` because every standard source was checked and none exposed
this laptop's EC fan tachometers:

- **NVML:** `nvmlDeviceGetNumFans=0`; `nvmlDeviceGetFanSpeed` and the per-fan RPM query are not
  supported for this mobile GPU.
- **NVAPI:** `NvAPI_GPU_GetTachReading` returned `NVAPI_NOT_SUPPORTED` (`-104`).
- **LibreHardwareMonitor:** discovered the MSI MS-15M1 board and full CPU MSR sensors, but no
  SuperIO/embedded-controller fan channels. LHM 0.9.6 has no MS-15M1 EC fan profile.
- **Windows `Win32_Fan`:** generic cooling devices with null `DesiredSpeed`, not RPM readings.

The actual source is the **MSI ACPI WMI interface** `root\wmi:MSI_ACPI`, method `Get_Fan`. It is
present on this machine and is used read-only by HardwareOSD. The missing piece was not a sensor
ID, merge-policy or normalization bug: the app never queried the vendor interface, and the interface
instance is **administrator-only** by its WMI ACL. That is why standard user runs still show `N/A`
and why an elevated app needed the new provider.

## MSI ACPI WMI fan interface

The interface is documented by the Linux kernel's `msi-wmi-platform` driver:

- Call `MSI_ACPI.Get_Fan` with a fixed **32-byte** `Package_32`; the first byte selects the
  subfeature. Subfeature `0x00` reads fan speeds. Bytes 1–8 contain up to four **big-endian 16-bit
  period counters**. The remaining bytes are zero.
- The conversion is `RPM = 480000 / period`. A zero period is reported as zero RPM; it can mean a
  stopped fan or an unused channel.
- `Get_Fan` has no current PWM/duty output. Subfeatures `0x01`/`0x02` are fan curves, not current
  percentages. HardwareOSD therefore does not report a fan percentage from this source and never
  derives one from RPM.
- Only `Get_Fan` is called. `Set_Fan`, `Set_AP`, EC writes and fan-control changes are not
  implemented.

HardwareOSD uses `System.Management` to invoke the documented WMI method; no MSI service, HWiNFO,
driver or vendor DLL is required. The provider is enabled by default but does nothing when the
`MSI_ACPI` class is absent (non-MSI machines).

## Data sources

### MSI notebook CPU/GPU fans

- `MsiAcpiFanProvider` reads `Get_Fan` subfeature 0 every poll when the process can open the WMI
  instance.
- Fan 1 maps to CPU, fan 2 to GPU; fans 3/4 are included only when their period is non-zero.
- The provider records the raw period in the source string (`MSI_ACPI Get_Fan: CPU Fan (period
  205)`), the RPM value, and the absence of a percentage channel.
- Without elevation the provider publishes unavailable CPU/GPU RPM and percentage entries with
  `msi-acpi-requires-elevation`, so the UI/audit show the real reason instead of "no fan sensor".
- 0.15.0 adds an optional LocalSystem broker for standard-user sessions. It calls the same
  `Get_Fan` method and returns only a fixed snapshot; the direct provider remains the elevated path.
  Without the broker installed, the standard-user behavior above is unchanged.

### NVIDIA GPU

- **NVAPI through LibreHardwareMonitor** supplies paired per-fan cooler status (RPM and current
  level) on hardware that supports it.
- **NVML** is the user-mode fallback: per-fan percentage, legacy single-fan percentage and, on newer
  drivers, per-fan intended RPM. NVML's RPM is documented as the intended speed; the UI/audit names
  NVML as the source.
- The merge policy prefers LHM's paired channel over MSI/NVML when both expose the same physical
  channel, then MSI ACPI, then NVML. This avoids mixing RPM and percentage from different fan
  orderings.

### AMD GPU

LibreHardwareMonitor's ADL path supplies fan RPM/percentage where the driver exposes it. No AMD
hardware was available for physical validation here.

### Desktop motherboard / SuperIO

LibreHardwareMonitor's motherboard/SuperIO `SensorType.Fan` tachometers and paired
`SensorType.Control` PWM/percentage channels are mapped when present. Generic `Fan #N` channels
remain Other and are not guessed to be CPU fans.

## Classification and percentage rules

- **CPU:** names containing `CPU` or `Processor`, including `CPU Fan` and `CPU Optional Fan`.
- **System:** `System`, `Chassis`, `Case`, `AUX`, `MOS`, `VRM`, `Chipset`, `PCH`, and board-side
  `GPU` headers.
- **Pump:** `Pump`, `Water Block`, `Liquid Cooling`; excluded from CPU/GPU fan metrics.
- **Other:** generic names such as `Fan #1`; recorded in diagnostics, not promoted to CPU.
- RPM is shown only when a real tachometer/period is returned. `0 RPM` is valid for a stopped fan;
  `N/A` is used when no channel exists or access is denied.
- Percentage is shown only when the hardware reports a real PWM/control percentage. It is never
  derived from observed maximum RPM.
- Fan values are range-checked; negative/implausible RPM and percentages above 150 % are rejected.
  NVML's documented allowance for slightly above 100 % is preserved.

## Diagnostics

The regular **Save sensor audit** CSV contains:

```text
fan_channel_id,component,fan_index,fan_name,hardware,source,rpm,percent,available,timestamp_utc,age_ms,note
```

Every fan/pump/unknown channel records its component, name, hardware/source API, RPM, percentage,
availability, timestamp, age and note. MSI raw periods are carried in the source string. The CLI
`audit` command prints a readable fan table and the provider inventory. The Diagnostics page shows:

- elevation and PawnIO state;
- fan-channel counts with the reason when unavailable;
- the MSI ACPI state (`available`, `requires administrator`, or `not present`).

Example from the elevated validation run:

```text
Component  Fan                  Hardware/source                              RPM    %    Available
CPU        CPU Fan              MSI_ACPI Get_Fan                             2341    -   yes
GPU        GPU Fan              MSI_ACPI Get_Fan                             2892    -   yes
```

## Actual hardware test — 2026-11-17

Validation machine: **MSI Vector GP68HX 12VH / MS-15M1**, i9-12900HX, RTX 4080 Laptop GPU.

**OBSERVED — elevated CLI audit (0.14.0):**

- `cpu.fan.rpm` = **2341–2400 RPM**, source `MSI_ACPI Get_Fan: CPU Fan` (period 205/200).
- `gpu.fan.rpm` = **2892 RPM**, source `MSI_ACPI Get_Fan: GPU Fan` (period 166).
- Both percentage metrics = `N/A`, note `msi-fan-no-percentage`.
- CPU package temperature 46–47 °C, CPU package power 5.2–6.1 W, all 24 logical core clocks
  present.
- GPU edge 47 °C (NVML), hotspot 51.4 °C, memory junction 54 °C, GPU power 2.2 W, fan 2892 RPM.

**OBSERVED — non-elevated audit:**

- `MSI_ACPI` class present; WMI instance denied by ACL.
- CPU/GPU fan RPM and percentage = `N/A` with `msi-acpi-requires-elevation`; no fabricated values.

**OBSERVED — load response (raw `Get_Fan` sampling):**

- CPU period changed from 200–205 (2400–2341 RPM) to 173 (2775 RPM) during a CPU load and returned
  afterwards; GPU period stayed 166 (2892 RPM) while the GPU was idle. This confirms live updates,
  not a static value.

Raw probe and audit output is not part of the public source tree.

**UNVERIFIED:** real fan RPM/percentage on a desktop SuperIO motherboard, a multi-fan discrete
NVIDIA GPU, an AMD Radeon GPU, a laptop with a non-MSI EC, and MSI fan percentage telemetry (no
percentage channel exists in `Get_Fan`).

## Optional LocalSystem broker — 0.15.0 live test

The optional broker was installed from the hash-verified self-contained staging build as
`HardwareOSDFanBroker`, Automatic, LocalSystem. The service was confirmed with `sc.exe qc` to use
`SERVICE_START_NAME : LocalSystem`, and the Program Files directory ACL was restricted to
SYSTEM/Administrators.

**OBSERVED on the validation machine:**

- A non-elevated pipe probe received a valid version-1 `GetFanSnapshot`: CPU period 200 / 2400 RPM,
  GPU period 0 / 0 RPM at idle.
- A non-elevated `HardwareOSD.Cli.exe sensors` selected the broker provider and reported the same
  CPU fan channel; the app's Settings -> System row said `standard user` and Diagnostics showed
  `installed yes; running yes; pipe yes`.
- A controlled CPU load produced CPU 2462 -> 3556 RPM and GPU 0 -> 2892 RPM, proving live updates.
  The broker and elevated direct-WMI path were not active at the same time.
- Stopping the service produced `installed=yes; running=no`, `N/A` RPM values and
  `msi-broker-unavailable`; starting it again restored live values.
- The source contains only `GetMethodParameters("Get_Fan")` / `InvokeMethod("Get_Fan", ...)` for
  WMI. No `Set_Fan`, `Set_EC`, `Set_Data`, PawnIO or raw EC path exists in the broker.
- `HardwareOSD` UI smoke: **87/87 checks passed** against the non-elevated app, including the new
  broker toggle and broker diagnostics row.
- English -> Ukrainian -> English switching passed.

**UNVERIFIED:** service startup after a reboot, remote-pipe rejection on a second machine, WMI
provider failure injection, a non-MSI/non-MS-15M1 model, and GPU fan values under a controlled GPU-only
load (the load test used CPU load and the shared cooling path).

## 0.16.0 regression check

The CPU-power/recording work did not change the fan source contract. On the non-elevated 0.16.0
app the log recorded `Provider: MSI fan broker initialised` and no direct MSI ACPI provider; the
0.15.0 broker remained installed/running and returned CPU 2400 RPM / GPU 2892 RPM. The elevated
0.16.0 app initialised the direct ACPI provider only. Fan ids and percentage semantics are
unchanged, and the new P/E CPU core ids do not affect fan channels.

## Validation still required

- Desktop motherboard exposing `CPU Fan`, `CPU Optional Fan` and `System Fan` tachometers.
- Multi-fan discrete NVIDIA GPU with NVAPI cooler status.
- AMD Radeon with ADL fan RPM/percentage.
- A laptop whose EC exposes a fan percentage or a documented percentage channel.
- Confirm pumps are excluded on a board with a pump header.
- Test the MSI ACPI provider on a second MSI model and on a machine where the class is absent.
