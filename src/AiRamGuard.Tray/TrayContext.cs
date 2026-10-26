using System.Diagnostics;
using System.Drawing.Drawing2D;
using AiRamGuard.Core;

namespace AiRamGuard.Tray;

/// <summary>Lives in the notification area: runs the guard on a timer, keeps history, owns the window.</summary>
sealed class TrayContext : ApplicationContext
{
    readonly AppState _state = new();
    readonly NotifyIcon _icon = new() { Visible = true, Text = "AI RAM Guard" };
    readonly System.Windows.Forms.Timer _guardTimer = new(), _sampleTimer = new() { Interval = 60_000 };
    readonly ToolStripMenuItem _status = new() { Enabled = false }, _pause = new("Pause for 1 hour");
    readonly SynchronizationContext _ui;
    DateTime _lastToast = DateTime.MinValue;
    MainForm? _form;
    int _samplesSinceSave;

    public TrayContext(bool showWindow)
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _state.RunNow = force => RunRound(force);
        _state.Pause = Pause;

        _icon.ContextMenuStrip = BuildMenu();
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };
        _guardTimer.Interval = Math.Max(30, _state.Settings.Guard.IntervalSeconds) * 1000;
        _guardTimer.Tick += (_, _) => RunRound(force: false);
        _sampleTimer.Tick += (_, _) => Sample();
        _guardTimer.Start();
        _sampleTimer.Start();

        Signals.Listen(Signals.Show, () => _ui.Post(_ => ShowWindow(), null));
        Signals.Listen(Signals.Exit, () => _ui.Post(_ => ExitThread(), null));
        _onForeground = (_, _, _, _, _, _, _) => Guard.ForegroundChanged();
        _foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _onForeground, 0, 0, 0);

        RunRound(force: false);
        if (showWindow) ShowWindow();
    }

    ContextMenuStrip BuildMenu()
    {
        var m = new ContextMenuStrip { Renderer = new MenuRenderer(Theme.Current), ShowImageMargin = true };
        var open = new ToolStripMenuItem("Open AI RAM Guard", null, (_, _) => ShowWindow()) { Font = new Font(m.Font, FontStyle.Bold) };
        m.Items.Add(open);
        m.Items.Add(_status);
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Free RAM now", null, (_, _) => RunRound(force: true));
        _pause.Click += (_, _) => Pause(_state.Paused ? null : TimeSpan.FromHours(1));
        m.Items.Add(_pause);
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Exit", null, (_, _) => ExitThread());
        m.Opening += (_, _) => _pause.Text = _state.Paused ? "Resume guarding" : "Pause for 1 hour";
        return m;
    }

    void Pause(TimeSpan? span)
    {
        _state.Settings.Guard.PausedUntil = span is null ? null : DateTime.Now + span;
        _state.Settings.Save();
        if (span is not null) Guard.RestorePriorities(); // paused means hands off now, not at the next round
        Log.Write(span is null ? "resumed" : $"paused until {_state.Settings.Guard.PausedUntil:HH:mm}");
        _state.Notify();
        Render();
    }

    async void RunRound(bool force)
    {
        if (_state.Busy) return;
        _state.Busy = true;
        _state.Notify();
        var s = _state.Settings;
        _guardTimer.Interval = Math.Max(30, s.Guard.IntervalSeconds) * 1000;
        try
        {
            var dryRun = !force && !s.AutoGuard; // auto guard off: only measure
            var (result, report, advice, applied, game) = await Task.Run(() =>
            {
                var inspected = Guard.Inspect(s.Guard, _state.Profiles, force);
                var game = Desktop.ForegroundIsFullscreen();
                var r = Guard.Execute(inspected, dryRun, force, prioritize: true);
                var after = r.Killed + r.Trimmed > 0 && !dryRun ? Guard.Scan(_state.Profiles) : inspected.Report;
                var configs = KnownConfigs.Read();
                var applied = Fixer.Applied(configs);
                return (r, after, Advisor.Advise(after, configs, applied, s.Guard.AgentCapsMB), applied, game);
            });
            if (_state.Paused) Guard.RestorePriorities(); // you paused while this round ran: it lowered after the pause restored

            _state.Report = report;
            _state.Advice = advice;
            _state.Fixed = applied;
            _state.Last = result;
            _state.LastRun = DateTime.Now;
            _state.GameActive = game;
            if (!dryRun && result.Killed + result.Trimmed > 0)
            {
                _state.Stats.Record(result);
                _state.Stats.Save(Paths.StatsFile);
            }
            if (_state.History.Samples.Count == 0) Sample();
            Toast(result, force, dryRun);
        }
        catch (Exception e) // async void: anything unhandled here would crash the app, and it restarts at every login
        {
            Log.Write($"round failed: {e.Message}");
        }
        finally
        {
            _state.Busy = false;
            _state.Notify();
            Render();
            if (_form is null) Guard.TrimSelf();
        }
    }

    void Toast(CleanResult r, bool force, bool dryRun)
    {
        var freed = r.FreedBytes;
        if (force)
            _icon.ShowBalloonTip(3000, "AI RAM Guard", $"Freed {Theme.Size(Math.Max(0, freed))}. Closed {r.Killed} leftovers, freed memory in {r.Trimmed} AI processes.", ToolTipIcon.Info);
        else if (_state.Settings.Notify && !dryRun && freed > 1L << 30 && DateTime.Now - _lastToast > TimeSpan.FromMinutes(30))
        {
            _lastToast = DateTime.Now;
            _icon.ShowBalloonTip(3000, "AI RAM Guard", $"Freed {Theme.Size(freed)} from AI apps ({r.Reason}).", ToolTipIcon.Info);
        }
    }

    /// <summary>Cheap once-a-minute reading for the graph; the AI share comes from the latest full scan.</summary>
    void Sample()
    {
        var (total, free) = Guard.Memory();
        _state.History.Add(new Sample(DateTime.Now, (int)(free >> 20), (int)((_state.Report?.AiBytes ?? 0) >> 20), (int)(total >> 20)));
        if (++_samplesSinceSave >= 10)
        {
            _samplesSinceSave = 0;
            _state.History.Save(Paths.HistoryFile);
        }
        _state.Notify();
    }

    void Render()
    {
        if (_state.Report is not { } r) return;
        var s = r.Snapshot;
        var text = $"{Theme.Size(s.FreeRamBytes)} free · AI apps {Theme.Size(r.AiBytes)}";
        _status.Text = $"{_state.StatusText} · {text}";
        var tip = $"AI RAM Guard · {_state.StatusText}\n{text}";
        _icon.Text = tip.Length > 127 ? tip[..tip.LastIndexOf(' ', 126)] : tip;
        var old = _icon.Icon;
        _icon.Icon = DrawIcon(1 - (double)s.FreeRamBytes / s.TotalRamBytes, (double)r.AiBytes / s.TotalRamBytes, _state.Paused);
        old?.Dispose();
    }

    /// <summary>
    /// Ring: brand = the AI share, light = the rest in use, faint track = free. Orange when RAM is nearly full, grey while paused.
    /// Drawn at 32 px with thick strokes so it still reads when the shell shrinks it to 16.
    /// </summary>
    static Icon DrawIcon(double usedPct, double aiPct, bool paused)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new RectangleF(4.5f, 4.5f, 23, 23);
            // The taskbar follows the system theme, which can differ from the apps theme.
            var dark = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")?.GetValue("SystemUsesLightTheme") is not 1;
            var ink = dark ? Color.White : Color.FromArgb(40, 36, 48);
            var accent = paused ? Color.Gray : usedPct > 0.85 ? Color.FromArgb(240, 138, 62) : Theme.Brand;
            using var track = new Pen(Color.FromArgb(dark ? 70 : 55, ink), 7);
            using var used = new Pen(Color.FromArgb(paused ? 110 : 200, ink), 7);
            using var ai = new Pen(accent, 7);
            g.DrawEllipse(track, rect);
            g.DrawArc(used, rect, -90, (float)(360 * usedPct));
            g.DrawArc(ai, rect, -90, (float)(360 * Math.Max(0.04, Math.Min(aiPct, usedPct))));
        }
        var h = bmp.GetHicon();
        using var tmp = Icon.FromHandle(h);
        var icon = (Icon)tmp.Clone();
        DestroyIcon(h);
        return icon;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

    // Out-of-context hook: called on this (UI) thread's message loop when the foreground window changes.
    delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time);
    const uint EVENT_SYSTEM_FOREGROUND = 3;
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
    readonly WinEventProc _onForeground; // kept in a field: the hook outlives any local, and a collected delegate crashes the callback
    readonly IntPtr _foregroundHook;

    void ShowWindow()
    {
        if (_form is null || _form.IsDisposed)
        {
            _form = new MainForm(_state);
            _form.FormClosed += (_, _) => { _form = null; Guard.TrimSelf(); };
        }
        _form.Show();
        if (_form.WindowState == FormWindowState.Minimized) _form.WindowState = FormWindowState.Normal;
        _form.Activate();
    }

    protected override void ExitThreadCore()
    {
        _state.History.Save(Paths.HistoryFile);
        UnhookWinEvent(_foregroundHook);
        Guard.RestorePriorities(); // nobody would put them back after we are gone
        _guardTimer.Dispose();
        _sampleTimer.Dispose();
        _form?.Close();
        _icon.Visible = false;
        _icon.Dispose();
        base.ExitThreadCore();
    }
}

/// <summary>Named events so a second launch can show the window, and the installer can stop a running copy.</summary>
static class Signals
{
    public const string Show = @"Local\AiRamGuard.Show", Exit = @"Local\AiRamGuard.Exit";

    public static void Listen(string name, Action onSignal)
    {
        var ev = new EventWaitHandle(false, EventResetMode.AutoReset, name);
        ThreadPool.RegisterWaitForSingleObject(ev, (_, _) => onSignal(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public static bool Send(string name)
    {
        if (!EventWaitHandle.TryOpenExisting(name, out var ev)) return false;
        using (ev) ev.Set();
        return true;
    }
}
