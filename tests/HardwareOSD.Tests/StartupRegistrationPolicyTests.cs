using HardwareOSD.Core.Config;

namespace HardwareOSD.Tests;

public sealed class StartupRegistrationPolicyTests
{
    [Theory]
    [InlineData(null, false, true)]
    [InlineData("", false, true)]
    [InlineData("   ", false, true)]
    [InlineData(null, true, false)]
    [InlineData(@"C:\Temp\HardwareOSD-QA", false, false)]
    [InlineData(@"C:\Temp\HardwareOSD-QA", true, false)]
    public void PortableAndDemoLaunchesNeverManageTheRealRunKey(
        string? dataDirectory, bool demo, bool expected)
    {
        Assert.Equal(expected, StartupRegistrationPolicy.CanManage(dataDirectory, demo));
    }

    [Theory]
    [InlineData(true, true, true, false)]   // no change: never write
    [InlineData(true, false, false, false)] // no change: never write
    [InlineData(true, false, true, true)]   // explicit disable
    [InlineData(true, true, false, true)]   // explicit enable
    [InlineData(false, true, false, false)] // portable profile: never write
    [InlineData(false, false, true, false)] // demo/portable profile: never write
    public void OnlyAnExplicitStateChangeMayWriteTheRunKey(
        bool canManage, bool requested, bool currentlyActive, bool expected)
    {
        Assert.Equal(expected,
            StartupRegistrationPolicy.ShouldWrite(canManage, requested, currentlyActive));
    }

    [Fact]
    public void ExplicitEnableOrDisableWritesOnlyWhenTheStateActuallyChanges()
    {
        // Inactive -> enabled by the user: one write.
        Assert.True(StartupRegistrationPolicy.ShouldWrite(
            canManage: true, requested: true, currentlyActive: false));

        // Already active and the user enables again: no duplicate write.
        Assert.False(StartupRegistrationPolicy.ShouldWrite(
            canManage: true, requested: true, currentlyActive: true));

        // Active -> disabled by the user: one write.
        Assert.True(StartupRegistrationPolicy.ShouldWrite(
            canManage: true, requested: false, currentlyActive: true));

        // Already inactive: no write, so a launch or unrelated save cannot restore the entry.
        Assert.False(StartupRegistrationPolicy.ShouldWrite(
            canManage: true, requested: false, currentlyActive: false));
    }
}
