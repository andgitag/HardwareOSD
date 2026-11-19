# Third-party notices

HardwareOSD 1.0.0.0 - Copyright (c) 2026 HardwareOSD contributors. MIT licensed (see LICENSE).

This product includes the following third-party components. They are used unmodified as binary
dependencies restored from NuGet. Source code for the MPL-2.0 components is available at the
project URLs below, as required by that license.

## Runtime dependencies

### LibreHardwareMonitorLib 0.9.6
- License: **MPL-2.0**
- Project: https://github.com/LibreHardwareMonitor/LibreHardwareMonitor
- License text: `licenses/MPL-2.0.txt`
- Used for: CPU/GPU sensor access (temperatures, power, clocks, loads, VRAM totals).
- Note: when running elevated, the library may load its own signed kernel driver to access CPU
  MSRs. That driver and behaviour belong to the LibreHardwareMonitor project, not to HardwareOSD.

### BlackSharp.Core 1.0.7
- License: **MPL-2.0**
- Project: https://github.com/Blacktempel/BlackSharp
- Transitive dependency of LibreHardwareMonitorLib.

### DiskInfoToolkit 1.1.2
- License: **MPL-2.0**
- Project: https://github.com/Blacktempel/DiskInfoToolkit
- Transitive dependency of LibreHardwareMonitorLib (storage support is disabled by HardwareOSD,
  but the assembly ships with the package).

### RAMSPDToolkit-NDD 1.4.2
- License: **MPL-2.0**
- Project: https://github.com/Blacktempel/RAMSPDToolkit
- Transitive dependency of LibreHardwareMonitorLib (memory SPD support is not used).

### HidSharp 2.6.4
- License: **Apache-2.0**
- Project: https://software.seekye.com/hidsharp
- License text: `licenses/HidSharp-LICENSE.txt`
- Used for HID device access inside LibreHardwareMonitor.

### Mono.Posix.NETStandard 1.0.0
- License: **MIT** (Microsoft .NET Foundation)
- Project: https://github.com/mono/mono

### Microsoft.Diagnostics.Tracing.TraceEvent 3.2.8
- License: **MIT**
- Project: https://github.com/microsoft/perfview
- Used for: real-time ETW sessions and frame-presentation event delivery (FPS monitoring).

### System.Management 10.0.2 (Microsoft)
- License: **MIT**
- Project: https://github.com/dotnet/runtime
- Used for: read-only WMI queries for the MSI ACPI fan interface and the Settings → System
  inventory. HardwareOSD does not invoke any WMI setter or write path.

### System.Diagnostics.PerformanceCounter 9.0.0 (Microsoft)
- License: **MIT**
- Project: https://github.com/dotnet/runtime
- Used for: reading the built-in Windows `Energy Meter` performance-counter category
  (`RAPL_Package0_PKG`, `PP0`, `PP1`, `DRAM`) for non-admin CPU power telemetry. The counters are
  provided by Windows; this package is only the managed access layer.

### System.ServiceProcess.ServiceController 9.0.0 (Microsoft)
- License: **MIT**
- Project: https://github.com/dotnet/runtime
- Used for: the optional `HardwareOSDFanBroker` Windows service host and read-only service-status
  detection from the standard-user client.

### System.IO.Pipes.AccessControl 5.0.0 (Microsoft)
- License: **MIT**
- Project: https://github.com/dotnet/runtime
- Used for: the explicit named-pipe DACL on the optional fan broker. The service is user-mode; no
  kernel driver is bundled.

### Microsoft.Diagnostics.NETCore.Client 0.2.510501
- License: **MIT**
- Transitive dependency of TraceEvent.

### System.Reflection.Metadata 9.0.8, System.Text.Json 9.0.8 (Microsoft)
- License: **MIT**
- Transitive dependencies of TraceEvent.

## Reference projects consulted (not redistributed)

The following repositories were inspected during the 0.9.3 FPS audit. No source code or binaries
from them are included in HardwareOSD.

- **1qlee/fps-overlay** — C#/WPF reference for direct TraceEvent ETW capture. The repository does
  not include a license file, so no code was copied; only the documented behaviour was reimplemented.
- **p1ccalo/fps-counter** — C++ reference that embeds Intel PresentMon 2.4.1 (MIT) as a child
  process. Its own source has no root license file; neither its source nor the PresentMon binary is
  redistributed by HardwareOSD.

The following projects were inspected during the 0.16.0 CPU-power research. No source code or
binaries from them are included in HardwareOSD.

- **CXWorld/CapFrameX** - the installed CapFrameX directory and the public repository were
  inspected to determine its CPU-power path. Its installed copy contains LibreHardwareMonitor and a
  signed WinRing0 kernel driver; HardwareOSD does not use, load or redistribute either. The
  repository does not grant a simple permissive licence for its whole source tree, so no code was
  copied.
- **seerge/g-helper** (MIT) - inspected as public evidence that the Windows `Energy Meter`
  performance-counter category exposes `RAPL_Package0_PKG` to standard users. Only the documented
  Windows counter names and units were used; no code was copied.
- **Microsoft Energy Meter Interface documentation** - the authoritative description of EMI IOCTLs,
  energy units and the RAPL domains used by the Windows provider.

## ETW providers consumed (not redistributed)

- **Microsoft-Windows-DXGI** `{CA11C036-0102-4A2D-A6AD-F03CFED5D3C9}` — `Present::Start` (42),
  `PresentMultiplaneOverlay::Start` (55).
- **Microsoft-Windows-D3D9** `{783ACA0A-790E-4D7F-8451-AA850511C6B9}` — `Present::Start` (1).

These are operating-system providers; no Microsoft binaries are redistributed with HardwareOSD.

### Microsoft .NET runtime and WPF (when published self-contained)
- License: **MIT**
- https://github.com/dotnet/runtime

### System.IO.Ports, System.CodeDom (Microsoft)
- License: **MIT**
- Transitive dependencies of LibreHardwareMonitorLib.

## Interfaces used (not bundled)

- **NVIDIA NVML** (`nvml.dll`) — supplied by the NVIDIA display driver. Documented public
  monitoring API; used through P/Invoke. No NVIDIA code is redistributed with HardwareOSD.
- **MSI ACPI WMI** (`root\wmi:MSI_ACPI`) — vendor ACPI interface exposed by the notebook firmware;
  HardwareOSD calls only the read-only `Get_Fan` method. No MSI service, DLL or MOF is redistributed
  or installed.
- **Windows PDH / NtQuerySystemInformation / CallNtPowerInformation / GlobalMemoryStatusEx** -
  documented Windows interfaces.
- **Windows Energy Meter Interface (EMI)** - the built-in Windows performance-counter/EMI provider
  exposes RAPL package and subdomain power. HardwareOSD reads it through the documented
  `Energy Meter` PDH category; no kernel driver or vendor component is bundled or installed.
- **Windows Power Meter / Energy Meter provider** - the `PowerMeterCounter` WMI/PDH provider is
  supplied by Windows under `%SystemRoot%\System32\wbem\powermeterprovider.mof`; no Microsoft
  binary is redistributed.
- **DWM / user32 window styles** — documented Windows APIs.

## Trademarks

NVIDIA, GeForce, Intel, Core, AMD and Counter-Strike are trademarks of their respective owners.
HardwareOSD is not affiliated with or endorsed by any of them, nor by MSI Afterburner, RivaTuner
Statistics Server, Valve Corporation, or the LibreHardwareMonitor project.
