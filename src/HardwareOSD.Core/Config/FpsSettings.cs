using HardwareOSD.Core.Fps;

namespace HardwareOSD.Core.Config;

/// <summary>FPS monitoring configuration. Captured values are never persisted, only preferences.</summary>
public sealed class FpsSettings
{
    public bool Enabled { get; set; } = true;

    public FpsSourceMode Mode { get; set; } = FpsSourceMode.Automatic;

    /// <summary>
    /// Which applications may be measured. Game Only (0) is the historical behaviour and the
    /// default for every settings file written before this option existed.
    /// </summary>
    public FpsCaptureMode CaptureMode { get; set; } = FpsCaptureMode.GameOnly;

    /// <summary>How often the measured value is republished to the overlay (100–250 ms).</summary>
    public int UpdateIntervalMs { get; set; } = 150;

    /// <summary>
    /// Measure any foreground process that presents through DXGI/D3D9, including windowed apps.
    /// Off by default: normal desktop windows (Steam, browsers, Electron shells) also present and
    /// would otherwise be mistaken for a game's FPS.
    /// </summary>
    public bool MeasureWindowedApps { get; set; }

    /// <summary>Process names that must never be measured. Names are compared case-insensitively.</summary>
    public List<string> IgnoredProcesses { get; set; } = new();

    /// <summary>Optional capture shortcut shown in the process-selection UI.</summary>
    public string CaptureHotkey { get; set; } = "Ctrl+Alt+F11";

    /// <summary>Capture duration hint in seconds; 0 means manual stop.</summary>
    public int CaptureDurationSeconds { get; set; }

    /// <summary>Capture start delay hint in seconds.</summary>
    public int CaptureDelaySeconds { get; set; }

    public bool IsIgnored(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        var name = processName.Trim();
        return IgnoredProcesses.Any(item =>
            string.Equals(item, name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFileNameWithoutExtension(item), Path.GetFileNameWithoutExtension(name),
                StringComparison.OrdinalIgnoreCase));
    }

    public FpsSettings Clone() => new()
    {
        Enabled = Enabled,
        Mode = Mode,
        CaptureMode = CaptureMode,
        UpdateIntervalMs = UpdateIntervalMs,
        MeasureWindowedApps = MeasureWindowedApps,
        IgnoredProcesses = (IgnoredProcesses ?? new List<string>()).ToList(),
        CaptureHotkey = CaptureHotkey,
        CaptureDurationSeconds = CaptureDurationSeconds,
        CaptureDelaySeconds = CaptureDelaySeconds,
    };

    public void Normalize()
    {
        UpdateIntervalMs = Math.Clamp(UpdateIntervalMs, 100, 250);
        IgnoredProcesses ??= new List<string>();
        var normalized = new List<string>(IgnoredProcesses.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in IgnoredProcesses)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var name = raw.Trim();
            var key = Path.GetFileNameWithoutExtension(name);
            if (seen.Add(key))
            {
                normalized.Add(name);
            }
        }

        IgnoredProcesses = normalized.Take(128).ToList();
        CaptureHotkey = string.IsNullOrWhiteSpace(CaptureHotkey) ? "Ctrl+Alt+F11" : CaptureHotkey.Trim();
        CaptureDurationSeconds = Math.Clamp(CaptureDurationSeconds, 0, 3600);
        CaptureDelaySeconds = Math.Clamp(CaptureDelaySeconds, 0, 60);
        if (!Enum.IsDefined(Mode))
        {
            Mode = FpsSourceMode.Automatic;
        }

        if (!Enum.IsDefined(CaptureMode))
        {
            CaptureMode = FpsCaptureMode.GameOnly;
        }
    }
}
