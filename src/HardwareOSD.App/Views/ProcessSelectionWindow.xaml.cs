using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using HardwareOSD.App.Localization;
using HardwareOSD.App.Native;
using HardwareOSD.App.Services;
using HardwareOSD.App.Theming;
using HardwareOSD.Core.Config;
using HardwareOSD.Core.Input;

namespace HardwareOSD.App.Views;

public partial class ProcessSelectionWindow : Window
{
    private readonly AppRuntime _runtime;
    private readonly AppSettings _draft;

    public ProcessSelectionWindow(AppRuntime runtime)
    {
        _runtime = runtime;
        _draft = runtime.Settings.Clone();
        InitializeComponent();
        SourceInitialized += (_, _) =>
            NativeMethods.SetTitleBarTheme(new WindowInteropHelper(this).Handle,
                AppThemeManager.IsDark);
        foreach (var name in _draft.Fps.IgnoredProcesses)
        {
            IgnoreList.Items.Add(new ListBoxItem { Content = name, Tag = name });
        }

        CaptureHotkeyBox.Text = _draft.Fps.CaptureHotkey;
        CaptureDurationBox.Text = _draft.Fps.CaptureDurationSeconds.ToString(CultureInfo.InvariantCulture);
        CaptureDelayBox.Text = _draft.Fps.CaptureDelaySeconds.ToString(CultureInfo.InvariantCulture);
        Loaded += (_, _) => RefreshProcesses();
    }

    private void RefreshProcesses_Click(object sender, RoutedEventArgs e) => RefreshProcesses();

    private void RefreshProcesses()
    {
        ProcessSearchBox.IsEnabled = false;
        ProcessList.Items.Clear();
        var filter = ProcessSearchBox.Text.Trim();
        try
        {
            var processes = Process.GetProcesses()
                .Where(process =>
                {
                    try
                    {
                        return process.Id > 0 &&
                               !string.IsNullOrWhiteSpace(process.ProcessName) &&
                               (filter.Length == 0 ||
                                process.ProcessName.Contains(filter, StringComparison.OrdinalIgnoreCase));
                    }
                    catch
                    {
                        return false;
                    }
                })
                .OrderBy(process => process.ProcessName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(process => process.Id)
                .Take(400)
                .ToArray();
            foreach (var process in processes)
            {
                try
                {
                    var name = process.ProcessName + ".exe";
                    ProcessList.Items.Add(new ListBoxItem
                    {
                        Content = $"{name}  ·  pid {process.Id}",
                        Tag = name,
                        Padding = new Thickness(4, 2, 4, 2),
                    });
                }
                catch
                {
                    // A process can exit between enumeration and access.
                }
                finally
                {
                    process.Dispose();
                }
            }

            if (ProcessList.Items.Count == 0)
            {
                ProcessList.Items.Add(new ListBoxItem
                {
                    Content = Loc.T("processes_none"),
                    IsEnabled = false,
                });
            }
        }
        finally
        {
            ProcessSearchBox.IsEnabled = true;
        }
    }

    private void ProcessSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded)
        {
            RefreshProcesses();
        }
    }

    private void ProcessList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
    }

    private void AddIgnore_Click(object sender, RoutedEventArgs e)
    {
        if (ProcessList.SelectedItem is not ListBoxItem item || item.Tag is not string name)
        {
            return;
        }

        if (IgnoreList.Items.OfType<ListBoxItem>().Any(existing =>
                string.Equals((string?)existing.Tag, name, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        IgnoreList.Items.Add(new ListBoxItem { Content = name, Tag = name });
    }

    private void RemoveIgnore_Click(object sender, RoutedEventArgs e)
    {
        if (IgnoreList.SelectedItem is ListBoxItem selected)
        {
            IgnoreList.Items.Remove(selected);
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var settings = _draft.Clone();
        settings.Fps.IgnoredProcesses = IgnoreList.Items.OfType<ListBoxItem>()
            .Select(item => item.Tag as string)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .ToList();
        if (HotkeyGesture.TryParse(CaptureHotkeyBox.Text, out var gesture))
        {
            settings.Fps.CaptureHotkey = gesture.DisplayText;
        }

        if (int.TryParse(CaptureDurationBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var duration))
        {
            settings.Fps.CaptureDurationSeconds = Math.Clamp(duration, 0, 3600);
        }

        if (int.TryParse(CaptureDelayBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var delay))
        {
            settings.Fps.CaptureDelaySeconds = Math.Clamp(delay, 0, 60);
        }

        _runtime.ApplySettings(settings, persist: true);
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
