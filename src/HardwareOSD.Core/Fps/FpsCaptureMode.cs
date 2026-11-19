namespace HardwareOSD.Core.Fps;

/// <summary>
/// Which applications the FPS publisher is allowed to measure.
///
/// <para><see cref="GameOnly"/> is the historical behaviour: a fullscreen/borderless foreground
/// process (or any windowed process when <c>MeasureWindowedApps</c> is on).</para>
///
/// <para><see cref="AllApplications"/> anchors on the foreground application group and may measure
/// a related windowed presenter (browser GPU process, UWP child, launcher helper). It never sums
/// processes and never falls back to an unrelated presenter.</para>
///
/// The numeric order is part of the settings file: <c>GameOnly</c> must stay 0 so configurations
/// written before this option existed deserialize as Game Only.
/// </summary>
public enum FpsCaptureMode
{
    GameOnly = 0,
    AllApplications = 1,
}
