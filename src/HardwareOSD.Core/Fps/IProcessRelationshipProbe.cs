namespace HardwareOSD.Core.Fps;

/// <summary>
/// Read-only description of one process. Used to decide which processes belong to the same
/// application (image path) and to walk the parent chain for bounded helper resolution.
/// No process memory is opened, injected or modified.
/// </summary>
public sealed record ProcessIdentity(
    int ProcessId,
    string? ImagePath,
    string? ProcessName,
    int ParentProcessId,
    long StartTimeUtcTicks)
{
    /// <summary>
    /// Identity used for sibling grouping. The full image path is the primary key (all Chromium
    /// renderer/GPU processes share <c>chrome.exe</c>), the executable name is the fallback when the
    /// path cannot be read, and the pid is the last resort so two unknown processes never group.
    /// </summary>
    public string GroupKey => !string.IsNullOrEmpty(ImagePath)
        ? "path:" + ImagePath.ToLowerInvariant()
        : !string.IsNullOrEmpty(ProcessName)
            ? "name:" + ProcessName.ToLowerInvariant()
            : "pid:" + ProcessId;

    /// <summary>
    /// Install root used by the bounded helper rule: volume root plus the first two directory
    /// segments. For example <c>C:\Program Files (x86)\Steam\...</c> -> <c>C:\Program Files (x86)\Steam</c>.
    /// </summary>
    public string? InstallRoot => GetInstallRoot(ImagePath);

    public static string? GetInstallRoot(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return null;
        }

        try
        {
            var root = Path.GetPathRoot(imagePath);
            if (string.IsNullOrEmpty(root))
            {
                return null;
            }

            var remainder = imagePath[root.Length..];
            var segments = remainder.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);

            // root + two directory segments keeps Steam/Chrome/Electron installs separate from
            // unrelated applications that happen to share C:\Program Files.
            var keep = Math.Min(2, Math.Max(0, segments.Length - 1));
            var parts = new List<string>(keep + 1) { root.TrimEnd(Path.DirectorySeparatorChar) };
            for (var i = 0; i < keep; i++)
            {
                parts.Add(segments[i]);
            }

            return string.Join(Path.DirectorySeparatorChar, parts).ToLowerInvariant();
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>One enumerated top-level window. Handles are only compared, never used to read content.</summary>
public readonly record struct WindowDescriptor(
    IntPtr Handle,
    int ProcessId,
    string? ClassName,
    bool IsVisible,
    IntPtr Owner,
    IntPtr RootOwner,
    IntPtr Root);

/// <summary>
/// Window and process relationship queries used by <see cref="AllApplicationsTargetResolver"/>.
/// Implementations are read-only; <see cref="Win32ProcessRelationshipProbe"/> uses user32/kernel32
/// enumeration APIs and never opens the target for memory access.
/// </summary>
public interface IProcessRelationshipProbe
{
    /// <summary>Identity of a process, or null when it exited or cannot be queried.</summary>
    ProcessIdentity? GetIdentity(int processId);

    /// <summary>Visible and hidden top-level windows in z-order, as a snapshot.</summary>
    IReadOnlyList<WindowDescriptor> GetTopLevelWindows();

    /// <summary>
    /// Resolves the presenting child of an <c>ApplicationFrameHost</c> window: the first visible
    /// <c>Windows.UI.Core.CoreWindow</c> child whose process is not the frame host itself.
    /// Returns 0 when the window is not a UWP frame or no child can be resolved.
    /// </summary>
    int ResolveUwpChild(IntPtr frameHostWindow, int frameHostProcessId);
}
