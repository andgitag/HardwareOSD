using System.Globalization;

namespace HardwareOSD.Core.Fps;

/// <summary>
/// Ownership rule for the per-process ETW session. A session may only be stopped as stale when
/// its PID belongs to a dead process. Another live HardwareOSD instance (for example an isolated
/// QA profile) must keep its capture session.
/// </summary>
public static class EtwSessionPolicy
{
    public const string Prefix = "HardwareOSD-FPS-";

    public static string NameFor(int processId) => Prefix + processId.ToString(CultureInfo.InvariantCulture);

    public static bool ShouldStop(string? sessionName, int currentProcessId,
        Func<int, bool> isProcessAlive)
    {
        if (string.IsNullOrWhiteSpace(sessionName) ||
            !sessionName.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var suffix = sessionName[Prefix.Length..];
        if (!int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out var ownerPid) ||
            ownerPid <= 0)
        {
            return false;
        }

        if (ownerPid == currentProcessId)
        {
            return false;
        }

        return !isProcessAlive(ownerPid);
    }
}
