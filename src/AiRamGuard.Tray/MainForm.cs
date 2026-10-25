using System.Diagnostics;
using AiRamGuard.Core;

namespace AiRamGuard.Tray;

/// <summary>The app window: sidebar navigation and five pages.</summary>
sealed class MainForm : Form
{
    public const string RepoUrl = "https://github.com/byensitmagnus/ai-ram-guard";
    public const string SiteUrl = "https://byens-it.dk";
    public static readonly string Version = typeof(MainForm).Assembly.GetName().Version?.ToString(3) ?? "";

    readonly AppState _state;
    readonly Panel _content = new() { Dock = DockStyle.Fill };
    readonly Sidebar _sidebar;
    readonly Dictionary<string, Page> _pages;
    Page _current;

    public MainForm(AppState state)
    {
        _state = state;
        var t = Theme.Detect();
        Text = "AI RAM Guard";
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        Size = new Size(1120, 780);
        MinimumSize = new Size(900, 620);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = t.Bg;
        ForeColor = t.Text;
        Font = t.Body;
        DoubleBuffered = true;

        _pages = new()
        {
            ["Overview"] = new OverviewPage(state, Navigate),
            ["Recommendations"] = new AdvicePage(state, Navigate),
            ["Disk"] = new DiskPage(),
            ["Activity"] = new ActivityPage(state),
            ["Settings"] = new SettingsPage(state),
        };
        _sidebar = new Sidebar(state, [("Overview", ""), ("Recommendations", ""), ("Disk", ""), ("Activity", ""), ("Settings", "")], Navigate);
        foreach (var p in _pages.Values) { p.Visible = false; _content.Controls.Add(p); }
        Controls.Add(_content);
        Controls.Add(_sidebar);
        _current = _pages["Overview"];
        Navigate("Overview");

        _state.Changed += OnChanged;
        FormClosed += (_, _) => _state.Changed -= OnChanged;
        HandleCreated += (_, _) => t.ApplyTitleBar(this);
        Shown += (_, _) => ActiveControl = null; // no focus ring on the first button at open
    }

    void OnChanged()
    {
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke(() =>
        {
            _sidebar.Invalidate(true);
            _sidebar.UpdateBadges();
            _current.Reload(_state);
        });
    }

    public void ShowPage(string name) => Navigate(name);

    public bool DiskBusy => ((DiskPage)_pages["Disk"]).Busy;

    void Navigate(string name)
    {
        _current.Visible = false;
        _current = _pages[name];
        _current.Visible = true;
        _current.Reload(_state);
        _sidebar.Select(name);
    }

    public static void Open(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });

    /// <summary>The app icon at a real size: ExtractAssociatedIcon only gives 32 px, which blurs when zoomed.</summary>
    public static Bitmap? Logo(int size) => Icon.ExtractIcon(Environment.ProcessPath!, 0, size)?.ToBitmap();

    /// <summary>Ctrl+1..5 pages, F5 check again, Esc close.</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keys)
    {
        string[] order = ["Overview", "Recommendations", "Disk", "Activity", "Settings"];
        if (keys >= (Keys.Control | Keys.D1) && keys <= (Keys.Control | Keys.D5)) { Navigate(order[(int)(keys & Keys.KeyCode) - (int)Keys.D1]); return true; }
        if (keys == Keys.F5) { _state.RunNow(false); return true; }
        if (keys == Keys.Escape) { Close(); return true; }
        return base.ProcessCmdKey(ref msg, keys);
    }
}

/// <summary>A scrollable page with a header and a vertical stack of cards.</summary>
abstract class Page : Panel
{
    protected readonly FlowLayoutPanel Stack = new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Location = new Point(28, 92) };
    protected readonly Label Heading = new() { AutoSize = true, Location = new Point(28, 26) };
    protected readonly Label Sub = new() { AutoSize = true, Location = new Point(30, 60) };
    protected readonly FlowLayoutPanel Actions = new() { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };

    protected Page(string title, string subtitle)
    {
        var t = Theme.Current;
        Dock = DockStyle.Fill;
        AutoScroll = true;
        BackColor = t.Bg;
        Heading.Text = title;
        Heading.Font = t.Heading;
        Heading.ForeColor = t.Text;
        Sub.Text = subtitle;
        Sub.ForeColor = t.Muted;
        Controls.AddRange([Heading, Sub, Actions, Stack]);
    }

    protected int ContentWidth => Math.Max(560, ClientSize.Width - 56);

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Actions.Location = new Point(ClientSize.Width - Actions.Width - 28, 30);
        foreach (Control c in Stack.Controls) c.Width = ContentWidth;
        Layout2();
    }

    /// <summary>Re-flow children that depend on the width.</summary>
    protected virtual void Layout2() { }

    public abstract void Reload(AppState s);

    protected static Card NewCard(int height) => new() { Height = height, Margin = new Padding(0, 0, 0, 16) };

    protected static Label CardTitle(string text) => new()
    {
        Text = text, AutoSize = true, Font = Theme.Current.Section, ForeColor = Theme.Current.Text,
        BackColor = Theme.Current.Surface, Location = new Point(18, 14),
    };

    protected static Label Wrapping(string text, Font font, Color color, int width) => new()
    {
        Text = text, Font = font, ForeColor = color, BackColor = Theme.Current.Surface, AutoSize = true, MaximumSize = new Size(width, 0),
    };

    /// <summary>Stack rows under the title and size the card to them.</summary>
    protected static void Fit(Card c)
    {
        var y = 52;
        foreach (Control row in c.Controls)
        {
            if (row.Location == new Point(18, 14) && row is Label) continue; // the title
            row.Location = new Point(20, y);
            row.Width = c.Width - 40;
            y += row.Height + 8;
        }
        c.Height = y + 12;
    }

    /// <summary>A row: text, a hint under it, and a button on the right (none when <paramref name="buttonText"/> is null).</summary>
    protected static Control Button(string text, string hint, string? buttonText, Action onClick)
    {
        var t = Theme.Current;
        var row = new Panel { Height = 52, BackColor = t.Surface };
        var label = new Label { Text = text, AutoSize = true, Location = new Point(0, 6), ForeColor = t.Text, BackColor = t.Surface };
        var sub = new Label { Text = hint, AutoSize = true, Location = new Point(0, 28), ForeColor = t.Muted, BackColor = t.Surface, Font = t.Small };
        row.Controls.AddRange([label, sub]);
        if (buttonText is null) return row;
        var b = new FlatButton(buttonText) { BackColor = t.Surface };
        b.Click += (_, _) => onClick();
        row.Controls.Add(b);
        row.Resize += (_, _) => b.Location = new Point(row.Width - b.Width, 7);
        return row;
    }
}

sealed class OverviewPage : Page
{
    readonly RingGauge _ring = new() { Size = new Size(230, 230) };
    readonly StatTile _ai = new(), _other = new(), _freed = new(), _orphans = new();
    readonly Label _legend = new() { AutoSize = true };
    readonly HistoryChart _chart = new();
    readonly AppList _apps = new();
    readonly Card _top = NewCard(272), _hist = NewCard(300), _list = NewCard(120);
    readonly FlatButton _clean = new("Free RAM now", primary: true, glyph: ""), _pause = new("Pause 1 h", glyph: "");
    readonly Label _status = new() { AutoSize = true };
    readonly Label _listTitle = CardTitle("AI apps");
    readonly ToolTip _tips = new();

    public OverviewPage(AppState s, Action<string> navigate) : base("Overview", "How much RAM your AI tools use right now.")
    {
        var t = Theme.Current;
        _clean.Click += (_, _) => s.RunNow(true);
        _pause.Click += (_, _) => s.Pause(s.Paused ? null : TimeSpan.FromHours(1));
        _pause.Margin = new Padding(0, 0, 10, 0);
        Actions.Controls.AddRange([_pause, _clean]);

        _ring.BackColor = t.Surface;
        _ring.Location = new Point(22, 20);
        foreach (var tile in new[] { _ai, _other, _freed, _orphans }) { tile.BackColor = t.Surface; _top.Controls.Add(tile); }
        _legend.BackColor = t.Surface;
        _legend.ForeColor = t.Muted;
        _legend.Font = t.Small;
        _status.BackColor = t.Surface;
        _status.Font = t.Bold;
        _top.Controls.AddRange([_ring, _legend, _status]);

        _chart.BackColor = t.Surface;
        _chart.Location = new Point(14, 18);
        _hist.Controls.Add(_chart);
        _hist.Resize += (_, _) => _chart.Size = new Size(_hist.Width - 28, _hist.Height - 32);

        _list.Controls.Add(_listTitle);
        _tips.SetToolTip(_clean, "Close leftovers and free idle AI memory now, whatever the limit. Also in the tray menu.");
        _tips.SetToolTip(_pause, "Stop automatic rounds for an hour. \"Free RAM now\" still works.");
        _tips.SetToolTip(_ring, "Brand colour: AI apps. Grey: everything else in use. Empty: free RAM.");
        var more = new LinkLabel { Text = "See how to fix the waste →", AutoSize = true, BackColor = t.Surface, LinkColor = t.BrandText, ActiveLinkColor = t.BrandText, LinkBehavior = LinkBehavior.HoverUnderline };
        more.LinkClicked += (_, _) => navigate("Recommendations");
        _list.Controls.Add(more);
        _apps.BackColor = t.Surface;
        _apps.Location = new Point(18, 54);
        _list.Controls.Add(_apps);
        _list.Resize += (_, _) => { _apps.Width = _list.Width - 36; more.Location = new Point(_list.Width - more.Width - 20, 18); };

        Stack.Controls.AddRange([_top, _hist, _list]);
    }

    protected override void Layout2()
    {
        var x = 280;
        var w = (_top.Width - x - 24) / 2;
        _ai.SetBounds(x, 26, w, 86);
        _other.SetBounds(x + w, 26, w, 86);
        _freed.SetBounds(x, 128, w, 86);
        _orphans.SetBounds(x + w, 128, w, 86);
        _status.Location = new Point(x, 226);
        _legend.Location = new Point(x + 140, 229);
    }

    public override void Reload(AppState s)
    {
        var t = Theme.Current;
        _pause.Text = s.Paused ? "Resume" : "Pause 1 h";
        _pause.Glyph = s.Paused ? "" : "";
        _clean.Text = s.Busy ? "Working…" : "Free RAM now";
        _status.Text = $"● {s.StatusText}";
        _status.ForeColor = s.Paused ? t.Warn : t.Good;
        _legend.Text = s.LastRun == default ? "" : $"Last check {s.LastRun:HH:mm:ss} · every {s.Settings.Guard.IntervalSeconds / 60.0:0.#} min";
        _chart.Set(s.History.Samples);

        if (s.Report is not { } r) return;
        var snap = r.Snapshot;
        var used = snap.TotalRamBytes - snap.FreeRamBytes;
        _ring.Set(snap.TotalRamBytes, r.AiBytes, snap.FreeRamBytes);
        var lowMB = s.GameActive && s.Settings.Guard.GameMode ? s.Settings.Guard.GameModeMinFreeMB : s.Settings.Guard.MinFreeMB;
        _ai.Set("Free RAM", Theme.Size(snap.FreeRamBytes), $"of {Theme.Size(snap.TotalRamBytes)} installed · limit {lowMB / 1024} GB",
            snap.FreeRamBytes >> 20 < lowMB ? t.Warn : t.Good);
        _other.Set("Everything else", Theme.Size(Math.Max(0, used - r.AiBytes)), "Windows, games, browsers …");
        _listTitle.Text = $"AI apps · {r.Agents.Sum(a => a.Processes.Count)} processes";
        _freed.Set("Freed by AI RAM Guard", Theme.Size(s.Stats.FreedMB << 20), $"since {s.Stats.Since:d MMM yyyy}");
        _orphans.Set("Leftovers right now", r.Orphans.Count == 0 ? "None" : $"{r.Orphans.Count} · {Theme.Size(r.Orphans.Sum(o => o.WorkingSetBytes))}",
            $"{s.Stats.OrphansClosed} closed so far", r.Orphans.Count > 0 ? t.Warn : t.Good);
        _apps.Set(r.Agents, s.Settings.Guard.AgentCapsMB, snap.TotalRamBytes);
        _list.Height = _apps.Height + 76;
        Layout2();
    }
}

sealed class AdvicePage : Page
{
    readonly Action<string> _navigate;
    string _shown = "";

    public AdvicePage(AppState s, Action<string> navigate) : base("Recommendations", "Fix the cause, so the RAM never gets used up in the first place.")
    {
        _navigate = navigate;
        var refresh = new FlatButton("Check again", glyph: "");
        refresh.Click += (_, _) => s.RunNow(false);
        Actions.Controls.Add(refresh);
    }

    public override void Reload(AppState s)
    {
        var key = string.Join("|", s.Advice.Select(a => a.Title + a.SavingsBytes / (50 << 20)));
        if (key == _shown && Stack.Controls.Count > 0) return; // avoid flicker when nothing meaningful changed
        _shown = key;
        Stack.SuspendLayout();
        foreach (Control c in Stack.Controls) c.Dispose();
        Stack.Controls.Clear();

        if (s.Advice.Count == 0)
        {
            var ok = NewCard(110);
            ok.Width = ContentWidth;
            ok.Controls.Add(CardTitle("✓  Nothing to fix"));
            ok.Controls.Add(new Label { Text = "No duplicated MCP servers, no leftovers. Your AI tools are behaving.", AutoSize = true, Location = new Point(20, 52), ForeColor = Theme.Current.Muted, BackColor = Theme.Current.Surface });
            Stack.Controls.Add(ok);
        }
        foreach (var a in s.Advice) Stack.Controls.Add(Build(a, s));
        Stack.ResumeLayout();
    }

    Card Build(Advice a, AppState s)
    {
        var t = Theme.Current;
        var card = NewCard(100);
        card.Width = ContentWidth;
        var (glyph, color) = a.Kind switch
        {
            AdviceKind.Orphans => ("", t.Warn),
            AdviceKind.Duplicates => ("", Theme.Brand),
            AdviceKind.NpxWrapper => ("", Theme.Brand),
            _ => ("", t.Muted),
        };
        var icon = new Label { Text = glyph, Font = new Font(Theme.IconFont, 16f), ForeColor = color, BackColor = t.Surface, AutoSize = true, Location = new Point(20, 20) };
        var title = new Label { Text = a.Title, Font = t.Section, ForeColor = t.Text, BackColor = t.Surface, AutoSize = true, Location = new Point(60, 18) };
        var detail = Wrapping(a.Detail, t.Body, t.Muted, Math.Min(640, ContentWidth - 260));
        detail.Location = new Point(60, 48);
        card.Controls.AddRange([icon, title, detail]);

        if (a.SavingsBytes > 0)
        {
            var save = new Pill($"saves ~{Theme.Size(a.SavingsBytes)}", t.Good, Color.FromArgb(t.Dark ? 40 : 30, t.Good)) { BackColor = t.Surface };
            card.Controls.Add(save);
            card.Resize += (_, _) => save.Location = new Point(card.Width - save.Width - 20, 16);
        }

        FlatButton? action = a.Kind switch
        {
            AdviceKind.Orphans => new FlatButton("Close them now", primary: true),
            AdviceKind.BigApp => new FlatButton("Set a cap"),
            _ when a.ConfigPath is not null => new FlatButton("Open config"),
            _ => null,
        };
        if (action is not null)
        {
            action.Click += (_, _) =>
            {
                if (a.Kind == AdviceKind.Orphans) s.RunNow(true);
                else if (a.Kind == AdviceKind.BigApp) _navigate("Settings");
                else MainForm.Open(a.ConfigPath!);
            };
            card.Controls.Add(action);
            card.Resize += (_, _) => action.Location = new Point(card.Width - action.Width - 20, card.Height - action.Height - 16);
        }
        card.Height = Math.Max(110, detail.Bottom + 22);
        return card;
    }
}

sealed class ActivityPage : Page
{
    readonly Card _card = NewCard(200);
    readonly ActivityList _list = new();
    string _shown = "";

    public ActivityPage(AppState s) : base("Activity", "Everything AI RAM Guard did, newest first.")
    {
        _list.BackColor = Theme.Current.Surface;
        _list.Location = new Point(22, 14);
        _card.Controls.Add(_list);
        _card.Resize += (_, _) => _list.Width = _card.Width - 44;
        var open = new FlatButton("Open log file", glyph: "\uE8E5");
        open.Click += (_, _) => { if (File.Exists(Paths.LogFile)) MainForm.Open(Paths.LogFile); };
        Actions.Controls.Add(open);
        Stack.Controls.Add(_card);
    }

    public override void Reload(AppState s)
    {
        string[] lines;
        try { lines = File.Exists(Paths.LogFile) ? File.ReadAllLines(Paths.LogFile) : []; }
        catch (IOException) { return; } // the guard is writing right now; next refresh picks it up
        var key = lines.Length == 0 ? "" : lines[^1] + lines.Length;
        if (key == _shown && _shown.Length > 0) return;
        _shown = key;
        _list.Set(Enumerable.Reverse(lines).Select(Parse).OfType<ActivityEntry>().Take(150).ToList());
        _card.Height = _list.Height + 28;
    }

    static readonly System.Text.RegularExpressions.Regex Round = new(
        @"^(?<dry>\[dry-run\] )?(?<reason>.+): closed (?<k>\d+) orphans, trimmed (?<t>\d+) AI processes(?:, freed (?<f>\d+) MB)?, free (?<a>[\d.]+) -> (?<b>[\d.]+) GB$");
    static readonly System.Text.RegularExpressions.Regex Orphan = new(@"^(?<verb>closed|could not close|would close) orphan (?<name>\S+) (?<pid>\d+) (?<mb>\d+) MB (?<key>.+)$");

    /// <summary>"2026-10-25 12:20:21 installed 1.0.0 to …" → a timeline entry in plain words.</summary>
    internal static ActivityEntry? Parse(string line)
    {
        if (line.Length < 20 || !DateTime.TryParseExact(line[..19], "yyyy-MM-dd HH:mm:ss", null, System.Globalization.DateTimeStyles.None, out var time)) return null;
        var msg = line[20..];
        var t = Theme.Current;
        if (Round.Match(msg) is { Success: true } m)
        {
            var freed = m.Groups["f"].Success ? $"Freed {Theme.Size(long.Parse(m.Groups["f"].Value) << 20)}" : "Freed memory";
            var parts = new List<string>();
            if (m.Groups["t"].Value != "0") parts.Add($"{m.Groups["t"].Value} AI processes trimmed");
            if (m.Groups["k"].Value != "0") parts.Add($"{m.Groups["k"].Value} leftovers closed");
            parts.Add(m.Groups["reason"].Value);
            return m.Groups["dry"].Success
                ? new(time, "\uE945", t.Muted, "Would have freed memory (guard is off)", string.Join(" · ", parts))
                : new(time, "\uE945", Theme.Brand, freed, string.Join(" · ", parts) + $" · free {m.Groups["a"].Value} → {m.Groups["b"].Value} GB");
        }
        if (Orphan.Match(msg) is { Success: true } o)
        {
            var detail = $"{o.Groups["name"].Value} · {o.Groups["mb"].Value} MB · its AI app was gone";
            return o.Groups["verb"].Value switch
            {
                "closed" => new(time, "\uE73E", t.Good, $"Closed leftover {o.Groups["key"].Value}", detail),
                "would close" => new(time, "\uE711", t.Muted, $"Would close leftover {o.Groups["key"].Value}", detail),
                _ => new(time, "\uE7BA", t.Warn, $"Could not close {o.Groups["key"].Value}", detail + " · it may already have exited"),
            };
        }
        if (msg.StartsWith("installed ")) return new(time, "\uE896", t.Good, $"Installed version {msg.Split(' ')[1]}", "Start menu, autostart and Settings → Apps");
        if (msg.StartsWith("paused until ")) return new(time, "\uE769", t.Warn, $"Paused until {msg[13..]}", "Automatic rounds stopped");
        if (msg == "resumed") return new(time, "\uE768", t.Good, "Resumed guarding", "");
        if (msg.StartsWith("round failed: ") || msg.StartsWith("ignored ") || msg.StartsWith("install failed: "))
            return new(time, "\uE7BA", t.Warn, "Something went wrong", msg);
        return new(time, "\uE946", t.Muted, msg, "");
    }
}
/// <summary>Where AI tools write on disk, with one-click moves of caches to another drive (and back).</summary>
sealed class DiskPage : Page
{
    readonly Card _card = NewCard(120);
    readonly FlatButton _measure = new("Measure again", glyph: "");
    IReadOnlyList<SpotUsage>? _spots;
    bool _busy;

    public bool Busy => _busy;

    public DiskPage() : base("Disk", "Where AI tools and their caches write. Move a cache off the system drive; undo puts it back.")
    {
        _measure.Click += (_, _) => Measure();
        Actions.Controls.Add(_measure);
        Stack.Controls.Add(_card);
        ShowCard("Measuring…");
    }

    public override void Reload(AppState s)
    {
        if (_spots is null) Measure(); // only on first view: a walk of a big temp folder takes seconds
    }

    async void Measure()
    {
        if (_busy) return;
        _busy = true;
        _measure.Text = "Measuring…";
        IReadOnlyList<SpotUsage>? spots = null;
        try { spots = await Task.Run(Storage.Measure); }
        catch (Exception e) { Log.Write($"disk scan failed: {e.Message}"); } // async void: anything unhandled would crash the app
        OnUi(() => { _spots = spots; _busy = false; _measure.Text = "Measure again"; Render(); });
    }

    /// <summary>An await resumes off the UI thread when no WinForms context is installed (the screenshot run), so hop back first.</summary>
    void OnUi(Action a)
    {
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(a); else a();
    }

    void ShowCard(string title, params Control[] rows)
    {
        foreach (Control c in _card.Controls) c.Dispose();
        _card.Controls.Clear();
        _card.Width = ContentWidth;
        _card.Controls.Add(CardTitle(title));
        _card.Controls.AddRange(rows);
        Fit(_card);
    }

    void Render()
    {
        if (_spots is null) { ShowCard("Could not measure the disk. See the log."); return; }
        var moved = Mover.Undoable();
        var shown = _spots.Where(u => u.Exists || moved.Contains(u.Spot.Id)).OrderByDescending(u => u.Bytes).ToList();
        var onSystem = shown.Where(u => u.OnSystemDrive).Sum(u => u.Bytes);
        ShowCard($"{Theme.Size(onSystem)} on the system drive · {Theme.Size(shown.Sum(u => u.Bytes) - onSystem)} elsewhere",
            shown.Select(u => Row(u, moved.Contains(u.Spot.Id))).ToArray());
    }

    Control Row(SpotUsage u, bool movedByUs)
    {
        var where = $"{u.RealPath} · {(u.OnSystemDrive ? "system drive" : $"on {u.Drive}")}";
        var text = $"{u.Spot.Name} · {Theme.Size(u.Bytes)}";
        if (movedByUs) return Button(text, where + " · moved by AI RAM Guard", "Undo move", () => UndoMove(u));
        void Open() { if (Directory.Exists(u.RealPath)) MainForm.Open(u.RealPath); }
        if (!u.Spot.Movable) return Button(text, where + $" · holds sessions: close the app, then set {u.Spot.EnvVars[0]}", "Open", Open);
        if (u.Links > 0) return Button(text, where + $" · {u.Links} links inside: only an admin can move those, so it stays", "Open", Open);
        var targets = u.OnSystemDrive ? Storage.Targets(u).OrderByDescending(d => d.AvailableFreeSpace).ToList() : [];
        if (targets.Count == 0) return Button(text, where, "Open", Open);
        if (targets.Count == 1) return Button(text, where, $"Move to {targets[0].Name.TrimEnd('\\')}", () => MoveTo(u, targets[0]));
        return Button(text, where, "Move to…", () =>
        {
            var menu = new ContextMenuStrip { Renderer = new MenuRenderer(Theme.Current) };
            foreach (var d in targets) menu.Items.Add($"{d.Name.TrimEnd('\\')}  ({Theme.Size(d.AvailableFreeSpace)} free)", null, (_, _) => MoveTo(u, d));
            menu.Show(Cursor.Position);
        });
    }

    async void MoveTo(SpotUsage u, DriveInfo d)
    {
        if (_busy) return; // still measuring or moving: a yes here would be dropped
        var to = Storage.TargetFolder(u.Spot, d.Name);
        if (MessageBox.Show(FindForm(), $"Move {u.Spot.Name} ({Theme.Size(u.Bytes)}) to {to}?\n\nApps you start from now on use the new folder: restart the ones that are open. You can undo it on this page.",
            "AI RAM Guard", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        await RunAndReport(() => Mover.Move(u.Spot, to), $"Moving {u.Spot.Name}…");
    }

    async void UndoMove(SpotUsage u)
    {
        if (_busy) return;
        if (MessageBox.Show(FindForm(), $"Move {u.Spot.Name} back and restore {string.Join(" and ", u.Spot.EnvVars)} as it was?",
            "AI RAM Guard", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        await RunAndReport(() => Mover.Undo(u.Spot), $"Moving {u.Spot.Name} back…");
    }

    async Task RunAndReport(Func<MoveResult> work, string progress)
    {
        if (_busy) return;
        _busy = true;
        ShowCard(progress + " Large caches take a while.");
        MoveResult r;
        try { r = await Task.Run(work); }
        catch (Exception e) { r = new(false, e.Message); }
        OnUi(() =>
        {
            _busy = false;
            MessageBox.Show(FindForm(), r.Message, "AI RAM Guard", MessageBoxButtons.OK, r.Ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            Measure();
        });
    }

    protected override void Layout2()
    {
        if (_card.Width != ContentWidth) { _card.Width = ContentWidth; Fit(_card); }
    }
}

sealed class SettingsPage : Page
{
    readonly AppState _s;
    readonly Card _caps = NewCard(120);
    string _capsShown = "";

    public SettingsPage(AppState s) : base("Settings", "Saved automatically.")
    {
        _s = s;
        var g = s.Settings.Guard;

        var guard = Section("Automatic guard",
            Toggle("Guard automatically", "Check every few minutes and act on the rules below.", () => s.Settings.AutoGuard, v => s.Settings.AutoGuard = v),
            Toggle("Close leftover MCP servers", "Only MCP servers it saw an AI app start, once that app is gone. Never a running session.", () => g.KillOrphans, v => g.KillOrphans = v),
            Toggle("Free memory from AI apps", "Moves idle AI memory to the pagefile when RAM runs low. Apps keep running.", () => g.Trim, v => g.Trim = v),
            Toggle("Game mode", "Use the stricter limit while a fullscreen game is in front.", () => g.GameMode, v => g.GameMode = v),
            Toggle("Lower priority in the background", "Windows frees memory from AI apps you are not using first. In game mode they also get less CPU.", () => g.LowerPriority, v => g.LowerPriority = v),
            Toggle("Notifications", "Tell me when more than 1 GB was freed.", () => s.Settings.Notify, v => s.Settings.Notify = v),
            Toggle("Start with Windows", "Runs quietly in the notification area.", () => Autostart.Enabled, v => Autostart.Enabled = v));

        var limits = Section("Limits",
            Row("Free RAM below", "Start freeing AI memory under this much free RAM.",
                new Stepper { Min = 1, Max = 64, Value = g.MinFreeMB / 1024, Format = v => $"{v} GB" }, v => g.MinFreeMB = v * 1024),
            Row("Game mode: free RAM below", "Stricter limit while you play.",
                new Stepper { Min = 1, Max = 64, Value = g.GameModeMinFreeMB / 1024, Format = v => $"{v} GB" }, v => g.GameModeMinFreeMB = v * 1024),
            Row("Check every", "How often to scan. Each scan takes a fraction of a second.",
                new Stepper { Min = 1, Max = 30, Value = Math.Max(1, g.IntervalSeconds / 60), Format = v => $"{v} min" }, v => g.IntervalSeconds = v * 60));

        _caps.Controls.Add(CardTitle("RAM cap per AI app"));

        var more = Section("Advanced",
            Button("Add your own AI app", "Recognise a tool AI RAM Guard doesn't know yet (JSON, no code).", "Edit profiles", OpenUserProfiles),
            Button("Data folder", "Settings, history, statistics and the log.", "Open", () => { Directory.CreateDirectory(Paths.DataDir); MainForm.Open(Paths.DataDir); }));

        var about = Section($"About · version {MainForm.Version}",
            Button("Open source on GitHub", "Star the repo, report a bug or add an AI app.", "GitHub", () => MainForm.Open(MainForm.RepoUrl)),
            Button("Made by Magnus Steinmeier Olsen · Byens IT", "Danish gaming PCs and IT. We built this for our own machines.", "byens-it.dk", () => MainForm.Open(MainForm.SiteUrl)));
        if (Installer.IsInstalled)
            about.Controls.Add(Button("Uninstall", "Removes the app, its shortcut and autostart. Your settings stay unless you delete the data folder.", "Uninstall…", Installer.ConfirmUninstall));
        Fit(about);

        Stack.Controls.AddRange([guard, limits, _caps, more, about]);
    }

    public override void Reload(AppState s)
    {
        // Caps list: running apps plus any app that already has a cap.
        var ids = (s.Report?.Agents.Select(a => a.Profile) ?? [])
            .Concat(s.Profiles.Agents.Where(p => s.Settings.Guard.AgentCapsMB.ContainsKey(p.Id)))
            .DistinctBy(p => p.Id).ToList();
        var key = string.Join(",", ids.Select(p => p.Id));
        if (key == _capsShown) return;
        _capsShown = key;
        while (_caps.Controls.Count > 1) _caps.Controls[1].Dispose();
        if (ids.Count == 0)
            _caps.Controls.Add(new Label { Text = "No AI apps running right now.", AutoSize = true, ForeColor = Theme.Current.Muted, BackColor = Theme.Current.Surface });
        foreach (var p in ids)
        {
            s.Settings.Guard.AgentCapsMB.TryGetValue(p.Id, out var cap);
            _caps.Controls.Add(Row(p.Name, "Above this, its idle memory is freed even when RAM isn't low.",
                new Stepper { Min = 0, Max = 32, Value = cap / 1024, Format = v => v == 0 ? "no cap" : $"{v} GB" },
                v => { if (v == 0) s.Settings.Guard.AgentCapsMB.Remove(p.Id); else s.Settings.Guard.AgentCapsMB[p.Id] = v * 1024; }));
        }
        Fit(_caps);
    }

    Card Section(string title, params Control[] rows)
    {
        var c = NewCard(100);
        c.Width = ContentWidth;
        c.Controls.Add(CardTitle(title));
        c.Controls.AddRange(rows);
        Fit(c);
        return c;
    }

    protected override void Layout2()
    {
        foreach (Control c in Stack.Controls) if (c is Card card) Fit(card);
    }

    ToggleSwitch Toggle(string text, string hint, Func<bool> get, Action<bool> set)
    {
        var t = new ToggleSwitch(text, hint) { Checked = get(), BackColor = Theme.Current.Surface };
        t.CheckedChanged += (_, _) => { set(t.Checked); _s.Settings.Save(); _s.Notify(); };
        return t;
    }

    Control Row(string text, string hint, Stepper stepper, Action<int> set)
    {
        var t = Theme.Current;
        var row = new Panel { Height = 52, BackColor = t.Surface };
        var label = new Label { Text = text, AutoSize = true, Location = new Point(0, 6), ForeColor = t.Text, BackColor = t.Surface };
        var sub = new Label { Text = hint, AutoSize = true, Location = new Point(0, 28), ForeColor = t.Muted, BackColor = t.Surface, Font = t.Small };
        stepper.BackColor = t.Surface;
        stepper.ValueChanged += (_, _) => { set(stepper.Value); _s.Settings.Save(); _s.Notify(); };
        row.Controls.AddRange([label, sub, stepper]);
        row.Resize += (_, _) => stepper.Location = new Point(row.Width - stepper.Width, 9);
        return row;
    }

    static void OpenUserProfiles()
    {
        var file = Path.Combine(Paths.ConfigDir, "profiles.json");
        if (!File.Exists(file))
        {
            Directory.CreateDirectory(Paths.ConfigDir);
            File.WriteAllText(file, """
            {
              "helperPatterns": [],
              "agents": [
                { "id": "my-agent", "name": "My AI app", "processNames": ["MyAiApp"], "helperPatterns": ["my-mcp-server"] }
              ]
            }
            """);
        }
        MainForm.Open(file);
    }
}

/// <summary>Logo, navigation and a live status pill.</summary>
sealed class Sidebar : Panel
{
    readonly List<NavButton> _buttons = [];
    readonly AppState _state;

    public Sidebar(AppState state, (string Name, string Glyph)[] items, Action<string> navigate)
    {
        _state = state;
        var t = Theme.Current;
        Dock = DockStyle.Left;
        Width = 236;
        BackColor = t.Surface;
        DoubleBuffered = true;

        var logo = new PictureBox { Image = MainForm.Logo(64), SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(36, 36), Location = new Point(22, 24), BackColor = t.Surface };
        var name = new Label { Text = "AI RAM Guard", Font = new Font("Segoe UI Semibold", 13f), ForeColor = t.Text, AutoSize = true, Location = new Point(66, 22), BackColor = t.Surface };
        var by = new Label { Text = "by Byens IT", Font = t.Small, ForeColor = t.Muted, AutoSize = true, Location = new Point(68, 44), BackColor = t.Surface };
        Controls.AddRange([logo, name, by]);

        var y = 96;
        foreach (var (n, glyph) in items)
        {
            var b = new NavButton(n, glyph) { Location = new Point(12, y), Width = Width - 24 };
            b.Click += (_, _) => navigate(n);
            _buttons.Add(b);
            Controls.Add(b);
            y += 46;
        }
    }

    public void Select(string name) { foreach (var b in _buttons) b.Selected = b.Text == name; }

    public void UpdateBadges()
    {
        var n = _state.Advice.Count(a => a.SavingsBytes > 100L << 20);
        _buttons.First(b => b.Text == "Recommendations").Badge = n > 0 ? n.ToString() : "";
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var t = Theme.Current;
        using (var pen = new Pen(t.Border)) e.Graphics.DrawLine(pen, Width - 1, 0, Width - 1, Height);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var pill = new RectangleF(16, Height - 76, Width - 32, 36);
        using (var path = Theme.Rounded(pill, 18))
        using (var b = new SolidBrush(t.Surface2)) e.Graphics.FillPath(b, path);
        var color = _state.Paused ? t.Warn : t.Good;
        using (var dot = new SolidBrush(color)) e.Graphics.FillEllipse(dot, pill.X + 14, pill.Y + 13, 10, 10);
        TextRenderer.DrawText(e.Graphics, _state.StatusText, t.Bold, new Rectangle((int)pill.X + 32, (int)pill.Y, (int)pill.Width - 40, (int)pill.Height), t.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        TextRenderer.DrawText(e.Graphics, $"v{MainForm.Version} · open source (MIT)", t.Small, new Rectangle(16, Height - 32, Width - 32, 20), t.Muted, TextFormatFlags.Left);
    }

    sealed class NavButton : Painted
    {
        bool _selected;
        string _badge = "";
        readonly string _glyph;
        public bool Selected { get => _selected; set { _selected = value; Invalidate(); } }
        public string Badge { get => _badge; set { if (_badge == value) return; _badge = value; Invalidate(); } }

        public NavButton(string text, string glyph)
        {
            base.Text = text;
            _glyph = glyph;
            Height = 40;
            Cursor = Cursors.Hand;
            BackColor = Theme.Current.Surface;
            TabStop = true;
            AccessibleRole = AccessibleRole.PageTab;
            AccessibleName = text;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode is Keys.Enter or Keys.Space) OnClick(EventArgs.Empty);
            base.OnKeyDown(e);
        }

        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void Draw(Graphics g)
        {
            var r = new RectangleF(0, 0, Width - 1, Height - 1);
            if (_selected || Hover)
            {
                using var path = Theme.Rounded(r, 8);
                using var b = new SolidBrush(T.Surface2);
                g.FillPath(b, path);
            }
            if (_selected)
            {
                using var bar = new SolidBrush(Theme.Brand);
                using var path = Theme.Rounded(new RectangleF(0, 10, 4, Height - 20), 2);
                g.FillPath(bar, path);
            }
            if (Focused) using (var fp = new Pen(Theme.Brand)) using (var fpath = Theme.Rounded(r, 8)) g.DrawPath(fp, fpath);
            DrawText(g, _glyph, T.Icon, _selected ? T.BrandText : T.Muted, new RectangleF(16, 0, 24, Height));
            DrawText(g, base.Text, _selected ? T.Bold : T.Body, T.Text, new RectangleF(48, 0, Width - 90, Height));
            if (_badge.Length > 0)
            {
                var pill = new RectangleF(Width - 38, 11, 26, 18);
                using var path = Theme.Rounded(pill, 9);
                using var b = new SolidBrush(Theme.Brand);
                g.FillPath(b, path);
                DrawText(g, _badge, T.Small, Color.White, pill, StringAlignment.Center);
            }
        }
    }
}
