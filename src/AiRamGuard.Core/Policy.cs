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
    public int IntervalSeconds { get; set; } = 300;
}

public sealed record Plan(IReadOnlyList<ProcessInfo> Kill, IReadOnlyList<ProcessInfo> Trim, string Reason);

/// <summary>Pure decision logic: what to do, given a report. Never touches the machine.</summary>
public static class Policy
{
    public static Plan Decide(Report report, GuardSettings s, int foregroundPid, bool fullscreenGame, bool force = false)
    {
        var kill = s.KillOrphans ? report.Orphans : [];
        var limitMB = s.GameMode && fullscreenGame ? s.GameModeMinFreeMB : s.MinFreeMB;
        var freeMB = report.Snapshot.FreeRamBytes / (1024 * 1024);
        var underPressure = force || freeMB < limitMB;

        var trim = s.Trim && underPressure
            ? report.Agents.SelectMany(a => a.Processes).Where(p => p.Pid != foregroundPid).ToList()
            : [];
        var reason = underPressure
            ? $"{freeMB} MB free < {limitMB} MB{(fullscreenGame ? " (game mode)" : "")}"
            : $"{freeMB} MB free, limit {limitMB} MB";
        return new Plan(kill, trim, force ? "manual clean" : reason);
    }
}
