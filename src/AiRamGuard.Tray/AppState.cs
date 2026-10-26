using AiRamGuard.Core;

namespace AiRamGuard.Tray;

/// <summary>Everything the window shows. The tray owns it; the window only reads it and asks for actions.</summary>
sealed class AppState
{
    public AppSettings Settings { get; } = AppSettings.Load();
    public ProfileSet Profiles { get; } = ProfileSet.Load();
    public History History { get; } = History.Load(Paths.HistoryFile);
    public Stats Stats { get; } = Stats.Load(Paths.StatsFile);

    public Report? Report { get; set; }
    public IReadOnlyList<Advice> Advice { get; set; } = [];
    /// <summary>Servers AI RAM Guard switched off and that are still off: the Fixes page offers Undo.</summary>
    public IReadOnlyList<FixRecord> Fixed { get; set; } = [];
    public CleanResult? Last { get; set; }
    public DateTime LastRun { get; set; }
    public bool GameActive { get; set; }
    public bool Busy { get; set; }

    public event Action? Changed;
    public void Notify() => Changed?.Invoke();

    /// <summary>Set by the tray; the window calls these.</summary>
    public Action<bool> RunNow { get; set; } = _ => { };
    public Action<TimeSpan?> Pause { get; set; } = _ => { };

    public bool Paused => Settings.Guard.PausedUntil is { } u && u > DateTime.Now;

    public string StatusText =>
        Paused ? $"Paused until {Settings.Guard.PausedUntil:HH:mm}"
        : !Settings.AutoGuard ? "Watching only"
        : GameActive && Settings.Guard.GameMode ? "Game mode"
        : "Guarding";
}
