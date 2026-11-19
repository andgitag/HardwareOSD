namespace HardwareOSD.Core.Fps;

/// <summary>
/// Foreground-anchored target selection for <see cref="FpsCaptureMode.AllApplications"/>.
///
/// <para>The resolver answers one question per publish tick: which single process belongs to the
/// foreground application and is presenting? It never sums presents, never picks the globally
/// busiest presenter and never falls back to an unrelated background application. When the
/// relationship is ambiguous it returns no target, and the publisher shows <c>NoPresenter</c>.</para>
///
/// <para>Ladder: exact foreground presenter → UWP child → window owner → same-image-path sibling →
/// bounded helper → previous target within the 750 ms focus grace → NoPresenter. A 400 ms minimum
/// dwell prevents two siblings from alternating while the foreground does not change.</para>
///
/// <para>Pure selection policy: no ETW, no WPF, no hardware. <see cref="IProcessRelationshipProbe"/>
/// supplies read-only process/window facts and can be faked in unit tests.</para>
/// </summary>
public sealed class AllApplicationsTargetResolver
{
    /// <summary>How long the previous target may survive a foreground transition.</summary>
    public const int FocusGraceMs = 750;

    /// <summary>Minimum time a selected group stays selected unless the foreground changes.</summary>
    public const int MinStableMs = 400;

    private const int MaxHelperAncestorDepth = 3;

    private static readonly HashSet<string> DesktopShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
    };

    private static readonly string[] HelperNameTokens =
    {
        "webhelper",
        "gpu",
        "renderer",
        "utility",
        "cef",
    };

    private readonly IProcessRelationshipProbe _probe;
    private readonly AppProcessCatalog _catalog;
    private readonly int _ownProcessId;

    private int _selectedPid;
    private string? _selectedName;
    private string? _selectedGroupKey;
    private long _selectedSinceMs;
    private long _selectedForegroundVersion;
    private int _lastForegroundPid;
    private long _foregroundChangedAtMs;
    private long _foregroundVersion;
    private FpsStatusReason _lastReason = FpsStatusReason.None;

    public AllApplicationsTargetResolver(
        IProcessRelationshipProbe probe,
        int ownProcessId,
        long identityTtlMs = AppProcessCatalog.DefaultTtlMs)
    {
        _probe = probe;
        _ownProcessId = ownProcessId;
        _catalog = new AppProcessCatalog(probe, identityTtlMs);
    }

    public int SelectedProcessId => _selectedPid;

    public bool HasSelection => _selectedPid != 0;

    public FpsStatusReason LastReason => _lastReason;

    public bool IsDesktopShell(string? windowClass)
        => !string.IsNullOrEmpty(windowClass) && DesktopShellClasses.Contains(windowClass);

    /// <summary>
    /// Resolves the target for one publish tick. <paramref name="fresh"/> must contain only processes
    /// with frames newer than the tracker's stale threshold.
    /// </summary>
    public FpsTarget Resolve(
        ForegroundWindowInfo foreground,
        IReadOnlyList<PresenterActivity> fresh,
        long nowWallMs)
    {
        var target = ResolveCore(foreground, fresh, nowWallMs);
        _lastReason = target.Reason;
        return target;
    }

    public void Reset()
    {
        _selectedPid = 0;
        _selectedName = null;
        _selectedGroupKey = null;
        _selectedSinceMs = 0;
        _selectedForegroundVersion = 0;
        _lastForegroundPid = 0;
        _foregroundChangedAtMs = 0;
        _foregroundVersion = 0;
        _lastReason = FpsStatusReason.None;
        _catalog.Clear();
    }

    public void Prune(long nowWallMs) => _catalog.Prune(nowWallMs);

    private FpsTarget ResolveCore(
        ForegroundWindowInfo foreground,
        IReadOnlyList<PresenterActivity> fresh,
        long nowWallMs)
    {
        ObserveForeground(foreground.ProcessId, nowWallMs);

        var activities = new Dictionary<int, PresenterActivity>(fresh.Count);
        foreach (var activity in fresh)
        {
            if (activity.ProcessId > 0)
            {
                activities[activity.ProcessId] = activity;
            }
        }

        // 0. No foreground / own settings window / desktop shell.
        if (foreground.ProcessId == 0)
        {
            return Sticky(activities, nowWallMs, ignoreGrace: false)
                   ?? FpsTarget.None(FpsStatusReason.NoForeground);
        }

        if (foreground.ProcessId == _ownProcessId)
        {
            // Opening the settings window must not blank a game that is still presenting. This is
            // the existing Game Only behaviour and is intentionally not time-capped while fresh.
            return Sticky(activities, nowWallMs, ignoreGrace: true)
                   ?? FpsTarget.None(FpsStatusReason.NotPresenting);
        }

        if (IsDesktopShell(foreground.ClassName))
        {
            return Sticky(activities, nowWallMs, ignoreGrace: false)
                   ?? FpsTarget.None(FpsStatusReason.Desktop);
        }

        // 1. Exact foreground presenter.
        if (activities.ContainsKey(foreground.ProcessId))
        {
            return Choose(foreground.ProcessId, FpsStatusReason.Foreground, activities, nowWallMs);
        }

        // 2. UWP: ApplicationFrameHost owns the window, the app runs in a child CoreWindow.
        if (IsUwpFrameHost(foreground))
        {
            var child = SafeResolveUwpChild(foreground);
            if (child > 0 && activities.ContainsKey(child))
            {
                return Choose(child, FpsStatusReason.UwpChild, activities, nowWallMs);
            }
        }

        IReadOnlyList<WindowDescriptor>? windows = null;
        IReadOnlyList<WindowDescriptor> Windows() => windows ??= SafeGetWindows();

        // 3. A fresh process owns the foreground window's owner/root-owner window.
        var owner = FindWindowOwner(foreground, activities, Windows());
        if (owner > 0)
        {
            return Choose(owner, FpsStatusReason.WindowOwner, activities, nowWallMs);
        }

        // 4. Same image path (Chromium/Electron GPU or compositor process). One pid only.
        var foregroundIdentity = _catalog.Get(foreground.ProcessId, nowWallMs);
        var foregroundGroup = foregroundIdentity?.GroupKey;
        var sibling = FindDominantSibling(foreground.ProcessId, foregroundGroup, activities, nowWallMs);
        if (sibling > 0)
        {
            return Choose(sibling, FpsStatusReason.SiblingRenderer, activities, nowWallMs);
        }

        // 5. Bounded helper (Steam-style multi-executable CEF). Ambiguity is not resolved by
        // guessing: two candidates mean NoPresenter.
        var helpers = FindHelpers(foreground.ProcessId, activities, Windows, nowWallMs);
        if (helpers.Count == 1)
        {
            return Choose(helpers[0], FpsStatusReason.HelperRenderer, activities, nowWallMs);
        }

        if (helpers.Count > 1)
        {
            return FpsTarget.None(FpsStatusReason.Ambiguous);
        }

        // 6. Previous target within the focus grace.
        return Sticky(activities, nowWallMs, ignoreGrace: false)
               ?? FpsTarget.None(FpsStatusReason.NotPresenting);
    }

    private void ObserveForeground(int processId, long nowWallMs)
    {
        if (processId != _lastForegroundPid)
        {
            _lastForegroundPid = processId;
            _foregroundChangedAtMs = nowWallMs;
            _foregroundVersion++;
        }
    }

    private FpsTarget? Sticky(
        Dictionary<int, PresenterActivity> activities,
        long nowWallMs,
        bool ignoreGrace)
    {
        if (_selectedPid == 0 || !activities.ContainsKey(_selectedPid))
        {
            return null;
        }

        if (!ignoreGrace && nowWallMs - _foregroundChangedAtMs > FocusGraceMs)
        {
            return null;
        }

        var identity = _catalog.Get(_selectedPid, nowWallMs);
        return FpsTarget.Select(_selectedPid, identity?.ProcessName ?? _selectedName, FpsStatusReason.Sticky);
    }

    private FpsTarget Choose(
        int processId,
        FpsStatusReason reason,
        Dictionary<int, PresenterActivity> activities,
        long nowWallMs)
    {
        var identity = _catalog.Get(processId, nowWallMs);
        var group = identity?.GroupKey ?? "pid:" + processId;
        var sameGroup = _selectedPid == processId ||
                        string.Equals(group, _selectedGroupKey, StringComparison.Ordinal);

        // Dwell: if the foreground did not change, a different group may not replace the current
        // target for MinStableMs. This stops two sibling renderers alternating as "most recent".
        if (!sameGroup &&
            _selectedPid != 0 &&
            _foregroundVersion == _selectedForegroundVersion &&
            nowWallMs - _selectedSinceMs < MinStableMs &&
            activities.ContainsKey(_selectedPid))
        {
            var previous = _catalog.Get(_selectedPid, nowWallMs);
            return FpsTarget.Select(_selectedPid, previous?.ProcessName ?? _selectedName, FpsStatusReason.Sticky);
        }

        if (!sameGroup)
        {
            _selectedSinceMs = nowWallMs;
            _selectedPid = processId;
            _selectedName = identity?.ProcessName;
            _selectedGroupKey = group;
            _selectedForegroundVersion = _foregroundVersion;
        }
        else
        {
            _selectedPid = processId;
            _selectedName = identity?.ProcessName;
        }

        return FpsTarget.Select(processId, identity?.ProcessName, reason);
    }

    private bool IsUwpFrameHost(ForegroundWindowInfo foreground)
    {
        if (string.Equals(foreground.ClassName, "ApplicationFrameWindow", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var identity = _catalog.Get(foreground.ProcessId);
        return string.Equals(identity?.ProcessName, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase);
    }

    private int SafeResolveUwpChild(ForegroundWindowInfo foreground)
    {
        try
        {
            return _probe.ResolveUwpChild(foreground.WindowHandle, foreground.ProcessId);
        }
        catch
        {
            return 0;
        }
    }

    private IReadOnlyList<WindowDescriptor> SafeGetWindows()
    {
        try
        {
            return _probe.GetTopLevelWindows();
        }
        catch
        {
            return Array.Empty<WindowDescriptor>();
        }
    }

    private int FindWindowOwner(
        ForegroundWindowInfo foreground,
        Dictionary<int, PresenterActivity> activities,
        IReadOnlyList<WindowDescriptor> windows)
    {
        if (windows.Count == 0 || foreground.WindowHandle == IntPtr.Zero)
        {
            return 0;
        }

        WindowDescriptor? foregroundWindow = null;
        foreach (var window in windows)
        {
            if (window.Handle == foreground.WindowHandle)
            {
                foregroundWindow = window;
                break;
            }
        }

        if (foregroundWindow is not { } fgWindow)
        {
            return 0;
        }

        var anchors = new HashSet<IntPtr>();
        if (fgWindow.Owner != IntPtr.Zero)
        {
            anchors.Add(fgWindow.Owner);
        }

        if (fgWindow.RootOwner != IntPtr.Zero)
        {
            anchors.Add(fgWindow.RootOwner);
        }

        if (anchors.Count == 0)
        {
            return 0;
        }

        foreach (var pid in RankedProcessIds(activities))
        {
            if (pid == foreground.ProcessId)
            {
                continue;
            }

            foreach (var window in windows)
            {
                if (window.ProcessId == pid && window.IsVisible && anchors.Contains(window.Handle))
                {
                    return pid;
                }
            }
        }

        return 0;
    }

    private int FindDominantSibling(
        int foregroundPid,
        string? foregroundGroup,
        Dictionary<int, PresenterActivity> activities,
        long nowWallMs)
    {
        if (string.IsNullOrEmpty(foregroundGroup))
        {
            return 0;
        }

        var bestPid = 0;
        var bestPresents = -1;
        var bestLast = long.MinValue;

        foreach (var pair in activities)
        {
            if (pair.Key == foregroundPid)
            {
                continue;
            }

            var identity = _catalog.Get(pair.Key, nowWallMs);
            if (identity is null ||
                !string.Equals(identity.GroupKey, foregroundGroup, StringComparison.Ordinal))
            {
                continue;
            }

            if (IsBetter(pair.Value, bestPid, bestPresents, bestLast))
            {
                bestPid = pair.Key;
                bestPresents = pair.Value.RecentPresents;
                bestLast = pair.Value.LastPresentWallMs;
            }
        }

        return bestPid;
    }

    private List<int> FindHelpers(
        int foregroundPid,
        Dictionary<int, PresenterActivity> activities,
        Func<IReadOnlyList<WindowDescriptor>> windows,
        long nowWallMs)
    {
        var candidates = new List<int>(2);
        var foregroundIdentity = _catalog.Get(foregroundPid, nowWallMs);
        var foregroundRoot = foregroundIdentity?.InstallRoot;

        foreach (var pid in RankedProcessIds(activities))
        {
            if (pid == foregroundPid)
            {
                continue;
            }

            var identity = _catalog.Get(pid, nowWallMs);
            if (identity is null || !IsHelperName(identity.ProcessName))
            {
                continue;
            }

            if (!IsDescendantOf(pid, foregroundPid, nowWallMs))
            {
                continue;
            }

            if (foregroundRoot is not null &&
                identity.InstallRoot is not null &&
                !string.Equals(identity.InstallRoot, foregroundRoot, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (HasVisibleWindow(pid, windows()))
            {
                continue;
            }

            candidates.Add(pid);
        }

        return candidates;
    }

    private bool IsDescendantOf(int processId, int ancestorProcessId, long nowWallMs)
    {
        var current = processId;
        for (var depth = 0; depth < MaxHelperAncestorDepth; depth++)
        {
            var identity = _catalog.Get(current, nowWallMs);
            var parent = identity?.ParentProcessId ?? 0;
            if (parent == ancestorProcessId)
            {
                return true;
            }

            if (parent <= 0)
            {
                return false;
            }

            current = parent;
        }

        return false;
    }

    private static bool HasVisibleWindow(int processId, IReadOnlyList<WindowDescriptor> windows)
    {
        foreach (var window in windows)
        {
            if (window.ProcessId == processId && window.IsVisible)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsHelperName(string? processName)
    {
        if (string.IsNullOrEmpty(processName))
        {
            return false;
        }

        foreach (var token in HelperNameTokens)
        {
            if (processName.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<int> RankedProcessIds(Dictionary<int, PresenterActivity> activities)
    {
        var ranked = new List<PresenterActivity>(activities.Count);
        ranked.AddRange(activities.Values);
        ranked.Sort(static (left, right) =>
        {
            var byPresents = right.RecentPresents.CompareTo(left.RecentPresents);
            if (byPresents != 0)
            {
                return byPresents;
            }

            var byLast = right.LastPresentWallMs.CompareTo(left.LastPresentWallMs);
            return byLast != 0 ? byLast : left.ProcessId.CompareTo(right.ProcessId);
        });

        foreach (var activity in ranked)
        {
            yield return activity.ProcessId;
        }
    }

    private static bool IsBetter(
        PresenterActivity candidate,
        int bestPid,
        int bestPresents,
        long bestLast)
        => candidate.RecentPresents > bestPresents ||
           (candidate.RecentPresents == bestPresents &&
            (candidate.LastPresentWallMs > bestLast ||
             (candidate.LastPresentWallMs == bestLast &&
              (bestPid == 0 || candidate.ProcessId < bestPid))));
}
