using HardwareOSD.Core.Fps;

namespace HardwareOSD.Tests;

public sealed class EtwSessionPolicyTests
{
    [Fact]
    public void NameFor_UsesTheProcessId()
        => Assert.Equal("HardwareOSD-FPS-4242", EtwSessionPolicy.NameFor(4242));

    [Theory]
    [InlineData("HardwareOSD-FPS-42", 7, true, false)]
    [InlineData("HardwareOSD-FPS-42", 7, false, true)]
    [InlineData("HardwareOSD-FPS-7", 7, true, false)]
    [InlineData("HardwareOSD-FPS-7", 7, false, false)]
    [InlineData("Other-FPS-42", 7, false, false)]
    [InlineData("HardwareOSD-FPS-abc", 7, false, false)]
    [InlineData("HardwareOSD-FPS-0", 7, false, false)]
    [InlineData(null, 7, false, false)]
    public void ShouldStop_OnlyStopsDeadForeignSessions(string? name, int currentPid,
        bool ownerAlive, bool expected)
        => Assert.Equal(expected,
            EtwSessionPolicy.ShouldStop(name, currentPid, _ => ownerAlive));
}
