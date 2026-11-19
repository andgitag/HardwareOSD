using HardwareOSD.Core.Fps;

namespace HardwareOSD.Tests;

/// <summary>Configurable in-memory relationship probe used by the resolver and publisher tests.</summary>
internal sealed class FakeRelationshipProbe : IProcessRelationshipProbe
{
    public Dictionary<int, ProcessIdentity> Identities { get; } = new();

    public List<WindowDescriptor> Windows { get; } = new();

    public Dictionary<IntPtr, int> UwpChildren { get; } = new();

    public FakeRelationshipProbe With(
        int processId,
        string? imagePath,
        string? name,
        int parentProcessId = 0)
    {
        Identities[processId] = new ProcessIdentity(
            processId,
            imagePath,
            name ?? (imagePath is null ? null : Path.GetFileNameWithoutExtension(imagePath)),
            parentProcessId,
            processId * 1000L);
        return this;
    }

    public ProcessIdentity? GetIdentity(int processId)
        => Identities.TryGetValue(processId, out var identity) ? identity : null;

    public IReadOnlyList<WindowDescriptor> GetTopLevelWindows() => Windows;

    public int ResolveUwpChild(IntPtr frameHostWindow, int frameHostProcessId)
        => UwpChildren.TryGetValue(frameHostWindow, out var child) ? child : 0;
}

internal static class FpsTestData
{
    public static PresenterActivity Activity(int pid, int presents = 60, long lastWallMs = 1000)
        => new(pid, lastWallMs, presents);

    public static readonly IntPtr FrameHostWindow = new(0x4000);
}
