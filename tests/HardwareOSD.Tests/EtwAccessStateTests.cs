using HardwareOSD.Core.Security;

namespace HardwareOSD.Tests;

public class EtwAccessStateTests
{
    [Fact]
    public void MembershipStates_AreClassified()
    {
        var notGranted = new EtwAccessState(false, false, false, "Performance Log Users", "user", null);
        Assert.False(notGranted.HasAccess);
        Assert.True(notGranted.NeedsGrant);
        Assert.False(notGranted.NeedsRelogon);
        Assert.Equal(EtwAccessKind.NotGranted, notGranted.Kind);

        var granted = new EtwAccessState(false, false, true, "Performance Log Users", "user", null);
        Assert.False(granted.HasAccess);
        Assert.False(granted.NeedsGrant);
        Assert.True(granted.NeedsRelogon);
        Assert.Equal(EtwAccessKind.GrantedNeedsRelogon, granted.Kind);

        var activeToken = new EtwAccessState(false, true, true, "Performance Log Users", "user", null);
        Assert.True(activeToken.HasAccess);
        Assert.Equal(EtwAccessKind.Active, activeToken.Kind);

        var elevated = new EtwAccessState(true, false, false, "Performance Log Users", "user", null);
        Assert.True(elevated.HasAccess);
        Assert.Equal(EtwAccessKind.Active, elevated.Kind);
    }

    [Fact]
    public void GroupSid_IsTheBuiltInPerformanceLogUsersGroup()
    {
        Assert.Equal("S-1-5-32-559", PerformanceLogUsers.GroupSid);
    }
}
