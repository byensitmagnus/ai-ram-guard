namespace AiRamGuard.Core;

public sealed class GuardSettings
{
    /// <summary>Trim AI apps when free RAM drops below this.</summary>
    public int MinFreeMB { get; set; } = 6144;
    /// <summary>Stricter limit while a fullscreen game (or any non-AI fullscreen app) is in front.</summary>
    public int GameModeMinFreeMB { get; set; } = 10240;
    public bool GameMode { get; set; } = true;
    public bool KillOrphans { get; set; } = true;
    public bool Trim { get; set; } = true;
    /// <summary>Background AI apps get low memory priority, so Windows frees their pages first. While a game is in front, also less CPU.</summary>
    public bool LowerPriority { get; set; } = true;
    public int IntervalSeconds { get; set; } = 120;
    /// <summary>Per-app RAM cap by profile id, e.g. { "codex": 3072 }. Above it the app is trimmed even with plenty of free RAM.</summary>
    public Dictionary<string, int> AgentCapsMB { get; set; } = [];
    /// <summary>Guard does nothing until this time (manual "Free RAM now" still works).</summary>
    public DateTime? PausedUntil { get; set; }
}

public sealed record Plan(IReadOnlyList<ProcessInfo> Kill, IReadOnlyList<ProcessInfo> Trim, string Reason)
{
    /// <summary>Processes of AI apps in the background: low memory priority. Anything lowered earlier and not in here gets restored.</summary>
    public IReadOnlyList<ProcessInfo> Lower { get; init; } = [];
    /// <summary>A game is in front: <see cref="Lower"/> also gets below-normal CPU priority and EcoQoS.</summary>
    public bool LowerCpu { get; init; }

    public static Plan Nothing(string reason) => new([], [], reason);
}

/// <summary>Pure decision logic: what to do, given a report. Never touches the machine.</summary>
public static class Policy
{
    public static Plan Decide(Report report, GuardSettings s, int foregroundPid, bool fullscreenGame, bool force = false, DateTime? now = null)
    {
        if (!force && s.PausedUntil is { } until && until > (now ?? DateTime.Now))
            return Plan.Nothing($"paused until {until:HH:mm}");

        var kill = s.KillOrphans ? report.Orphans : [];
        var limitMB = s.GameMode && fullscreenGame ? s.GameModeMinFreeMB : s.MinFreeMB;
        var freeMB = report.Snapshot.FreeRamBytes / (1024 * 1024);
        var underPressure = force || freeMB < limitMB;

        // The app you are using is never trimmed, not just its window: its renderers and helpers would lag too.
        // A CLI agent counts as in front when its terminal is (WindowsTerminal is its ancestor). Explorer starts everything, so not that.
        var byPid = report.Snapshot.Processes.ToDictionary(p => p.Pid);
        var ancestorCounts = foregroundPid > 0 && !(byPid.TryGetValue(foregroundPid, out var fg)
            && string.Equals(fg.Name, "explorer.exe", StringComparison.OrdinalIgnoreCase));
        bool InFront(AgentUsage a) => a.Processes.Any(p => p.Pid == foregroundPid || (ancestorCounts && Analyzer.HasAncestor(p, foregroundPid, byPid)));
        var candidates = report.Agents.Where(a => !InFront(a)).ToList();
        var overCap = candidates.Where(a => s.AgentCapsMB.TryGetValue(a.Profile.Id, out var cap) && a.Bytes / (1024 * 1024) > cap).ToList();
        var trimAgents = !s.Trim ? [] : underPressure ? candidates : overCap;

        var reason = force ? "manual clean"
            : underPressure ? $"{freeMB / 1024.0:0.0} GB free, below the {limitMB / 1024.0:0.#} GB limit{(fullscreenGame && s.GameMode ? " (game mode)" : "")}"
            : overCap.Count > 0 ? $"over cap: {string.Join(", ", overCap.Select(a => a.Profile.Name))}"
            : $"{freeMB / 1024.0:0.0} GB free, limit {limitMB / 1024.0:0.#} GB";
        var paused = s.PausedUntil is { } u && u > (now ?? DateTime.Now); // reached only by a manual clean: it frees RAM, it doesn't resume lowering
        var lower = s.LowerPriority && !paused;
        return new Plan(kill, trimAgents.SelectMany(a => a.Processes).ToList(), reason)
        {
            Lower = lower ? candidates.SelectMany(a => a.Processes).ToList() : [],
            LowerCpu = lower && s.GameMode && fullscreenGame,
        };
    }
}
