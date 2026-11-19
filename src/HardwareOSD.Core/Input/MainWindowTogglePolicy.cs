namespace HardwareOSD.Core.Input;

public enum MainWindowToggleAction
{
    ShowAndActivate = 0,
    Hide = 1,
}

/// <summary>
/// Decides what Ctrl+Alt+F9 does. The first press restores/activates the window; a press while it is
/// already visible and active hides it back to the tray. A window that is visible but not focused is
/// activated instead of hidden, so the shortcut never makes the window disappear while the user is
/// switching to it.
/// </summary>
public static class MainWindowTogglePolicy
{
    public static MainWindowToggleAction Next(bool exists, bool visible, bool active, bool minimized)
        => exists && visible && !minimized && active
            ? MainWindowToggleAction.Hide
            : MainWindowToggleAction.ShowAndActivate;
}
