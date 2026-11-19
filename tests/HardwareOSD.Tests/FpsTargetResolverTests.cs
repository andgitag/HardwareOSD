using HardwareOSD.Core.Fps;

namespace HardwareOSD.Tests;

public class FpsTargetResolverTests
{
    private const int OwnPid = 1000;
    private const long Now = 10_000;

    private static AllApplicationsTargetResolver Resolver(FakeRelationshipProbe probe)
        => new(probe, OwnPid, identityTtlMs: 0);

    private static FpsTarget Resolve(
        AllApplicationsTargetResolver resolver,
        int foregroundPid,
        bool fullscreen,
        string? windowClass,
        params PresenterActivity[] activities)
        => resolver.Resolve(
            new ForegroundWindowInfo(foregroundPid, fullscreen, IntPtr.Zero, windowClass),
            activities,
            Now);

    [Fact]
    public void ForegroundPresenter_IsSelected()
    {
        var probe = new FakeRelationshipProbe().With(2000, @"C:\Games\game.exe", "game");
        var target = Resolve(Resolver(probe), 2000, true, "GameWindow", FpsTestData.Activity(2000));

        Assert.True(target.IsEligible);
        Assert.Equal(2000, target.ProcessId);
        Assert.Equal(FpsStatusReason.Foreground, target.Reason);
    }

    [Fact]
    public void WindowedForegroundPresenter_IsSelectedInAllApplications()
    {
        var probe = new FakeRelationshipProbe().With(2000, @"C:\Apps\video.exe", "video");
        var target = Resolve(Resolver(probe), 2000, fullscreen: false, "VideoWindow", FpsTestData.Activity(2000));

        Assert.True(target.IsEligible);
        Assert.Equal(2000, target.ProcessId);
    }

    [Fact]
    public void SiblingRenderer_IsSelectedWhenForegroundDoesNotPresent()
    {
        var probe = new FakeRelationshipProbe()
            .With(5000, @"C:\Program Files\Google\Chrome\Application\chrome.exe", "chrome")
            .With(5001, @"C:\Program Files\Google\Chrome\Application\chrome.exe", "chrome");

        var target = Resolve(Resolver(probe), 5000, false, "Chrome_WidgetWin_1",
            FpsTestData.Activity(5001));

        Assert.True(target.IsEligible);
        Assert.Equal(5001, target.ProcessId);
        Assert.Equal(FpsStatusReason.SiblingRenderer, target.Reason);
    }

    [Fact]
    public void MultipleSiblings_PicksDominantNotSum()
    {
        var probe = new FakeRelationshipProbe()
            .With(5000, @"C:\Apps\app.exe", "app")
            .With(5001, @"C:\Apps\app.exe", "app")
            .With(5002, @"C:\Apps\app.exe", "app");

        var target = Resolve(Resolver(probe), 5000, false, "AppWindow",
            FpsTestData.Activity(5001, presents: 60),
            FpsTestData.Activity(5002, presents: 144));

        Assert.Equal(5002, target.ProcessId);
        Assert.Equal(FpsStatusReason.SiblingRenderer, target.Reason);
    }

    [Fact]
    public void BackgroundPresenter_IsNotSelected()
    {
        var probe = new FakeRelationshipProbe()
            .With(3000, @"C:\Windows\System32\notepad.exe", "notepad")
            .With(2000, @"C:\Games\game.exe", "game");

        var target = Resolve(Resolver(probe), 3000, false, "Notepad",
            FpsTestData.Activity(2000, presents: 1440));

        Assert.False(target.IsEligible);
        Assert.Equal(0, target.ProcessId);
        Assert.Equal(FpsStatusReason.NotPresenting, target.Reason);
    }

    [Fact]
    public void DesktopForeground_ReturnsNoTarget()
    {
        var probe = new FakeRelationshipProbe().With(900, @"C:\Windows\explorer.exe", "explorer");
        var target = Resolve(Resolver(probe), 900, true, "Progman");

        Assert.False(target.IsEligible);
        Assert.Equal(FpsStatusReason.Desktop, target.Reason);
    }

    [Fact]
    public void OwnWindow_KeepsPreviousTarget()
    {
        var probe = new FakeRelationshipProbe().With(3000, @"C:\Games\game.exe", "game");
        var resolver = Resolver(probe);

        var first = Resolve(resolver, 3000, true, "GameWindow", FpsTestData.Activity(3000));
        Assert.Equal(3000, first.ProcessId);

        var after = resolver.Resolve(
            new ForegroundWindowInfo(OwnPid, false, IntPtr.Zero, "HwndWrapper"),
            new[] { FpsTestData.Activity(3000) },
            Now + 3000);

        Assert.True(after.IsEligible);
        Assert.Equal(3000, after.ProcessId);
        Assert.Equal(FpsStatusReason.Sticky, after.Reason);
    }

    [Fact]
    public void UwpFrameHost_ResolvesChildPresenter()
    {
        var probe = new FakeRelationshipProbe()
            .With(4000, @"C:\Windows\System32\ApplicationFrameHost.exe", "ApplicationFrameHost")
            .With(4100, @"C:\Program Files\WindowsApps\app.exe", "app");
        probe.UwpChildren[FpsTestData.FrameHostWindow] = 4100;
        var resolver = Resolver(probe);

        var target = resolver.Resolve(
            new ForegroundWindowInfo(4000, false, FpsTestData.FrameHostWindow, "ApplicationFrameWindow"),
            new[] { FpsTestData.Activity(4100) },
            Now);

        Assert.True(target.IsEligible);
        Assert.Equal(4100, target.ProcessId);
        Assert.Equal(FpsStatusReason.UwpChild, target.Reason);
    }

    [Fact]
    public void WindowOwner_IsSelectedBeforeSiblingRules()
    {
        var probe = new FakeRelationshipProbe()
            .With(7000, @"C:\Apps\launcher.exe", "launcher")
            .With(7001, @"C:\Apps\render.exe", "render");
        var foregroundWindow = new IntPtr(0x100);
        probe.Windows.Add(new WindowDescriptor(foregroundWindow, 7000, "Launcher", true,
            Owner: new IntPtr(0x200), RootOwner: new IntPtr(0x200), Root: foregroundWindow));
        probe.Windows.Add(new WindowDescriptor(new IntPtr(0x200), 7001, "Render", true,
            IntPtr.Zero, IntPtr.Zero, new IntPtr(0x200)));

        var target = Resolver(probe).Resolve(
            new ForegroundWindowInfo(7000, false, foregroundWindow, "Launcher"),
            new[] { FpsTestData.Activity(7001) },
            Now);

        Assert.True(target.IsEligible);
        Assert.Equal(7001, target.ProcessId);
        Assert.Equal(FpsStatusReason.WindowOwner, target.Reason);
    }

    [Fact]
    public void AmbiguousHelpers_ReturnNoTarget()
    {
        var probe = new FakeRelationshipProbe()
            .With(6000, @"C:\Program Files (x86)\Steam\steam.exe", "steam")
            .With(6001, @"C:\Program Files (x86)\Steam\bin\cef\one\steamwebhelper.exe", "steamwebhelper", 6000)
            .With(6002, @"C:\Program Files (x86)\Steam\bin\cef\two\steamwebhelper.exe", "steamwebhelper", 6000);

        var target = Resolve(Resolver(probe), 6000, false, "Steam",
            FpsTestData.Activity(6001, presents: 60),
            FpsTestData.Activity(6002, presents: 55));

        Assert.False(target.IsEligible);
        Assert.Equal(FpsStatusReason.Ambiguous, target.Reason);
    }

    [Fact]
    public void SingleBoundedHelper_IsSelected()
    {
        var probe = new FakeRelationshipProbe()
            .With(6000, @"C:\Program Files (x86)\Steam\steam.exe", "steam")
            .With(6001, @"C:\Program Files (x86)\Steam\bin\cef\steamwebhelper.exe", "steamwebhelper", 6000);

        var target = Resolve(Resolver(probe), 6000, false, "Steam", FpsTestData.Activity(6001));

        Assert.True(target.IsEligible);
        Assert.Equal(6001, target.ProcessId);
        Assert.Equal(FpsStatusReason.HelperRenderer, target.Reason);
    }

    [Fact]
    public void StickyTarget_SurvivesBriefFocusLoss()
    {
        var probe = new FakeRelationshipProbe().With(3000, @"C:\Games\game.exe", "game");
        var resolver = Resolver(probe);
        var activity = FpsTestData.Activity(3000);

        var first = Resolve(resolver, 3000, true, "GameWindow", activity);
        Assert.Equal(3000, first.ProcessId);

        var duringGrace = resolver.Resolve(
            new ForegroundWindowInfo(0, false),
            new[] { activity },
            Now + 300);
        Assert.True(duringGrace.IsEligible);
        Assert.Equal(3000, duringGrace.ProcessId);
        Assert.Equal(FpsStatusReason.Sticky, duringGrace.Reason);

        var afterGrace = resolver.Resolve(
            new ForegroundWindowInfo(0, false),
            new[] { activity },
            Now + 300 + 900);
        Assert.False(afterGrace.IsEligible);
        Assert.Equal(FpsStatusReason.NoForeground, afterGrace.Reason);
    }

    [Fact]
    public void StaleTarget_IsNotSticky()
    {
        var probe = new FakeRelationshipProbe().With(3000, @"C:\Games\game.exe", "game");
        var resolver = Resolver(probe);
        Resolve(resolver, 3000, true, "GameWindow", FpsTestData.Activity(3000));

        var target = resolver.Resolve(new ForegroundWindowInfo(0, false), Array.Empty<PresenterActivity>(), Now + 100);
        Assert.False(target.IsEligible);
        Assert.Equal(FpsStatusReason.NoForeground, target.Reason);
    }
}
