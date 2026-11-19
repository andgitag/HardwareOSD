namespace HardwareOSD.Core.Fps;

public enum FpsStatus
{
    /// <summary>FPS monitoring disabled by the user.</summary>
    Disabled = 0,

    /// <summary>Capture is starting up.</summary>
    Starting = 1,

    /// <summary>ETW capture requires administrator rights.</summary>
    RequiresElevation = 2,

    /// <summary>Frames are being measured for the foreground application.</summary>
    Capturing = 3,

    /// <summary>No foreground window (desktop, secure desktop, lock screen).</summary>
    NoForegroundApp = 4,

    /// <summary>A foreground application exists but is not presenting frames.</summary>
    NoFrames = 5,

    /// <summary>Displayed FPS was requested but the backend only provides presented timing.</summary>
    DisplayedUnavailable = 6,

    /// <summary>Capture failed (ETW session error, provider failure, ...).</summary>
    Error = 7,

    /// <summary>No fresh present events arrived, but the last valid rate is still recent enough to keep
    /// displaying while the ETW delivery gap resolves. The value is real, not interpolated.</summary>
    Holding = 8,

    /// <summary>The foreground window is not a fullscreen game (desktop UI, browser, Steam, ...).</summary>
    NoGame = 9,

    /// <summary>All Applications found no presenting process related to the foreground application.
    /// No value is published for unrelated background presenters.</summary>
    NoPresenter = 10,
}

/// <summary>
/// Why the publisher chose a target, held a value or declined to publish one. Used for localized
/// UI detail text and diagnostics; it is deliberately independent of the human-readable Message.
/// </summary>
public enum FpsStatusReason
{
    None = 0,

    /// <summary>Exact foreground process presents (or is the selected target).</summary>
    Foreground = 1,

    /// <summary>Presenting child of an ApplicationFrameHost window (UWP).</summary>
    UwpChild = 2,

    /// <summary>Process owning the foreground window's owner/root-owner window.</summary>
    WindowOwner = 3,

    /// <summary>Different process with the same image path (Chromium/Electron GPU or compositor).</summary>
    SiblingRenderer = 4,

    /// <summary>Bounded helper process related to the foreground install (Steam-style CEF).</summary>
    HelperRenderer = 5,

    /// <summary>Previous valid target kept during a short foreground transition.</summary>
    Sticky = 6,

    /// <summary>Last value held because the foreground changed (Game Only focus grace).</summary>
    FocusChange = 7,

    /// <summary>Last value held because ETW delivery paused.</summary>
    EtwGap = 8,

    /// <summary>The foreground application does not present through DXGI/D3D9.</summary>
    NotPresenting = 9,

    /// <summary>Desktop/shell foreground: composed by DWM, not an application FPS.</summary>
    Desktop = 10,

    /// <summary>No foreground window at all.</summary>
    NoForeground = 11,

    /// <summary>Several unrelated helpers matched; no value is safer than a wrong one.</summary>
    Ambiguous = 12,

    /// <summary>The process is on the user's FPS ignore list.</summary>
    Ignored = 13,
}

/// <summary>Which frame-presentation timing the value represents.</summary>
public enum FpsSource
{
    None = 0,
    Presented = 1,
    Displayed = 2,
}

public enum FpsSourceMode
{
    /// <summary>Prefer displayed timing; fall back to presented timing and mark the fallback.</summary>
    Automatic = 0,

    /// <summary>Use presented timing explicitly.</summary>
    Presented = 1,

    /// <summary>Use displayed timing only; unavailable with the current capture backend.</summary>
    Displayed = 2,
}

/// <summary>
/// Immutable FPS state published by <see cref="FpsMonitorService"/> and read by the overlay.
/// Contains no fabricated values: when no measurement exists, <see cref="Fps"/> is 0 and the
/// formatter renders "--".
/// </summary>
public sealed record FpsSnapshot(
    long Sequence,
    long TimestampMs,
    FpsStatus Status,
    FpsSource Source,
    bool IsFallback,
    int ProcessId,
    string? ProcessName,
    double Fps,
    double FrameTimeMs,
    double OnePercentLowFps,
    string? Message,
    bool IsProcessFallback = false,
    FpsStatusReason Reason = FpsStatusReason.None)
{
    public static readonly FpsSnapshot Empty = new(
        0, 0, FpsStatus.Disabled, FpsSource.None, false, 0, null, 0, 0, 0, null);

    public bool HasValue => (Status == FpsStatus.Capturing || Status == FpsStatus.Holding) && Fps > 0.1;

    /// <summary>True while the last valid value is retained during a short ETW delivery gap.</summary>
    public bool IsValueHeld => Status == FpsStatus.Holding;

    public string ProcessLabel => string.IsNullOrEmpty(ProcessName) ? "—" : ProcessName!;

    /// <summary>Short status text for the configuration UI.</summary>
    public string StatusText => Status switch
    {
        FpsStatus.Disabled => "Disabled",
        FpsStatus.Starting => "Starting…",
        FpsStatus.RequiresElevation => "Administrator or Performance Log Users rights required",
        FpsStatus.Capturing => $"Measuring {ProcessLabel}",
        FpsStatus.Holding => $"Holding last FPS ({ProcessLabel})",
        FpsStatus.NoGame => "Waiting for a fullscreen game",
        FpsStatus.NoPresenter => "No presenting application",
        FpsStatus.NoForegroundApp => "No foreground application",
        FpsStatus.NoFrames => $"Waiting for frames from {ProcessLabel}",
        FpsStatus.DisplayedUnavailable => "Displayed FPS unavailable",
        FpsStatus.Error => "Capture error",
        _ => "Unknown",
    };
}

/// <summary>Single-writer/many-reader cache for the latest FPS snapshot.</summary>
public sealed class FpsCache
{
    private FpsSnapshot _latest = FpsSnapshot.Empty;

    public FpsSnapshot Latest => Volatile.Read(ref _latest);

    public void Publish(FpsSnapshot snapshot) => Volatile.Write(ref _latest, snapshot);

    public void Reset() => Volatile.Write(ref _latest, FpsSnapshot.Empty);
}
