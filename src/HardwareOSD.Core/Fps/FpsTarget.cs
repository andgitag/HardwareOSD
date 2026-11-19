namespace HardwareOSD.Core.Fps;

/// <summary>
/// One resolved FPS target: the single process whose presents are measured, plus why it was chosen.
/// A target is either eligible (<see cref="IsEligible"/> true, <see cref="ProcessId"/> non-zero) or a
/// reason describing why no honest value can be published. Present counts are never aggregated into
/// this type.
/// </summary>
public readonly record struct FpsTarget(
    int ProcessId,
    string? ProcessName,
    bool IsEligible,
    FpsStatusReason Reason)
{
    public static FpsTarget None(FpsStatusReason reason) => new(0, null, false, reason);

    public static FpsTarget Select(int processId, string? processName, FpsStatusReason reason)
        => new(processId, processName, processId > 0, reason);
}
