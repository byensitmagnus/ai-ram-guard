using System.Diagnostics;
using AiRamGuard.Core;

namespace AiRamGuard.Tray;

/// <summary>The app window: sidebar navigation and five pages.</summary>
sealed class MainForm : Form
{
    public const string RepoUrl = "https://github.com/byensitmagnus/ai-ram-guard";
    public const string SiteUrl = "https://byens-it.dk";
    public static readonly string Version = typeof(MainForm).Assembly.GetName().Version?.ToString(3) ?? "";
    static readonly string[] Order = ["Overview", "Fixes", "Disk", "Activity", "Settings"];

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
            ["Fixes"] = new FixesPage(state),
            ["Disk"] = new DiskPage(),
            ["Activity"] = new ActivityPage(state),
            ["Settings"] = new SettingsPage(state),
        };
        _sidebar = new Sidebar(state, [("Overview", "\uE80F"), ("Fixes", "\uE90F"), ("Disk", "\uEDA2"), ("Activity", "\uE81C"), ("Settings", "\uE713")], Navigate);
        foreach (var p in _pages.Values) { p.Visible = false; _content.Controls.Add(p); }
        Controls.Add(_content);
        Controls.Add(_sidebar);
        _current = _pages["Overview"];
        Navigate("Overview");
        _sidebar.UpdateBadges();

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
        if (keys >= (Keys.Control | Keys.D1) && keys <= (Keys.Control | Keys.D5)) { Navigate(Order[(int)(keys & Keys.KeyCode) - (int)Keys.D1]); return true; }
        if (keys == Keys.F5) { _state.RunNow(false); return true; }
        if (keys == Keys.Escape) { Close(); return true; }
        return base.ProcessCmdKey(ref msg, keys);
    }
}

/// <summary>
/// A scrollable page: a header (title, one line, at most one primary button) and a column of cards.
/// Spacing on a 4 px grid: 32 page gutter, 24 inside a card, 16 between cards.
/// </summary>
abstract class Page : Panel
{
    protected const int Gutter = 32, Pad = 24, Gap = 16, HeaderTop = 28;
    protected readonly FlowLayoutPanel Stack = new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Location = new Point(Gutter, HeaderTop + 64 + 24) };
    // GrowAndShrink: with the default GrowOnly the panel keeps its 200×100 default size and paints over the first card.
    protected readonly FlowLayoutPanel Actions = new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
    readonly PageHeader _header = new() { Location = new Point(Gutter, HeaderTop) };

    protected Page(string title, string subtitle)
    {
        var t = Theme.Current;
        Dock = DockStyle.Fill;
        AutoScroll = true;
        t.ApplyScrollbars(this);
        BackColor = t.Bg;
        _header.BackColor = t.Bg;
        _header.Set(title, subtitle);
        Actions.SizeChanged += (_, _) => PlaceHeader();
        Actions.ControlAdded += (_, e) => e.Control!.Margin = new Padding(Actions.Controls.Count > 1 ? 10 : 0, 0, 0, 0);
        Controls.AddRange([_header, Actions, Stack]);
    }

    protected int ContentWidth => Math.Max(560, ClientSize.Width - 2 * Gutter);

    void PlaceHeader()
    {
        Actions.Location = new Point(ClientSize.Width - Actions.Width - Gutter, HeaderTop - 1 + AutoScrollPosition.Y);
        _header.Width = Math.Max(200, Actions.Left - Gutter - 16);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        PlaceHeader();
        foreach (Control c in Stack.Controls) c.Width = ContentWidth;
        Layout2();
    }

    /// <summary>Re-flow children that depend on the width.</summary>
    protected virtual void Layout2() { }

    public abstract void Reload(AppState s);

    protected static Card NewCard(int height) => new() { Height = height, Margin = new Padding(0, 0, 0, Gap) };

    protected static void Replace(FlowLayoutPanel panel, IEnumerable<Control> controls)
    {
        var old = panel.Controls.Cast<Control>().ToList();
        panel.Controls.Clear();
        foreach (var c in old) c.Dispose();
        panel.Controls.AddRange(controls.ToArray());
    }

    protected static TextBlock CardTitle(string text) =>
        new(text, Theme.Current.Section, Theme.Current.Text, Theme.Current.Surface) { Location = new Point(Pad, Pad - 2), Width = 600, Tag = "title" };

    /// <summary>A small heading between cards: "Needs you", "Done".</summary>
    protected Control Section(string title, string hint)
    {
        var t = Theme.Current;
        var p = new Panel { Height = 52, Width = ContentWidth, BackColor = t.Bg, Margin = new Padding(0, 8, 0, 4) };
        p.Controls.Add(new TextBlock(title, t.Bold, t.Text, t.Bg) { Location = new Point(2, 4), Width = p.Width, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top });
        p.Controls.Add(new TextBlock(hint, t.Small, t.Muted, t.Bg) { Location = new Point(2, 26), Width = p.Width, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top });
        return p;
    }

    /// <summary>Stack rows under the title, with a divider between them, and size the card to them.</summary>
    protected static void Fit(Card c)
    {
        var rows = c.Controls.Cast<Control>().Where(r => r.Tag as string != "title").ToList();
        foreach (Control title in c.Controls) if (title.Tag as string == "title") title.Width = c.Width - 2 * Pad;
        var y = rows.Count < c.Controls.Count ? Pad + 40 : Pad - 4;
        var lines = new List<int>();
        for (var i = 0; i < rows.Count; i++)
        {
            if (i > 0) { lines.Add(y + 8); y += 17; }
            rows[i].Location = new Point(Pad, y);
            rows[i].Width = c.Width - 2 * Pad;
            y += rows[i].Height;
        }
        c.Dividers = [.. lines];
        c.Height = y + Pad - 8;
    }

    /// <summary>A row: text, a hint under it, and up to two buttons on the right.</summary>
    protected static Control Button(string text, string hint, string? buttonText, Action onClick, (string Text, Action OnClick)? second = null)
    {
        var t = Theme.Current;
        var row = new Panel { Height = 52, BackColor = t.Surface };
        var label = new TextBlock(text, t.Body, t.Text, t.Surface) { Location = new Point(0, 7) };
        var sub = new TextBlock(hint, t.Small, t.Muted, t.Surface) { Location = new Point(0, 29) };
        row.Controls.AddRange([label, sub]);
        var buttons = new List<FlatButton>();
        if (buttonText is not null) buttons.Add(new FlatButton(buttonText) { BackColor = t.Surface });
        if (second is { } s) buttons.Add(new FlatButton(s.Text) { BackColor = t.Surface });
        if (buttons.Count > 0) buttons[0].Click += (_, _) => onClick();
        if (buttons.Count > 1) buttons[1].Click += (_, _) => second!.Value.OnClick();
        row.Controls.AddRange(buttons.ToArray());
        row.Resize += (_, _) =>
        {
            var x = row.Width;
            foreach (var b in buttons) { x -= b.Width; b.Location = new Point(x, 7); x -= 10; }
            label.Width = sub.Width = Math.Max(100, x - 16);
        };
        return row;
    }
}

sealed class OverviewPage : Page
{
    const int Ring = 208, Col = Pad + Ring + 40;
    readonly RingGauge _ring = new() { Size = new Size(Ring, Ring), Location = new Point(Pad, Pad) };
    readonly TextBlock _status, _headline, _sub, _listTitle;
    readonly StatTile _free = new(), _save = new();
    readonly FlatButton _clean = new("Free RAM now", primary: true, glyph: "\uE945"), _fixes = new("See fixes", glyph: "\uE90F"), _pause = new("Pause 1 h", glyph: "\uE769");
    readonly HistoryChart _chart = new();
    readonly AppList _apps = new();
    readonly Card _hero = NewCard(256), _list = NewCard(120), _hist = NewCard(300);
    readonly ToolTip _tips = new();

    public OverviewPage(AppState s, Action<string> navigate) : base("Overview", "How much RAM your AI tools use right now.")
    {
        var t = Theme.Current;
        _clean.Click += (_, _) => s.RunNow(true);
        _fixes.Click += (_, _) => navigate("Fixes");
        _pause.Click += (_, _) => s.Pause(s.Paused ? null : TimeSpan.FromHours(1));
        Actions.Controls.Add(_pause);

        _status = new TextBlock("", t.Small, t.Good, t.Surface);
        _headline = new TextBlock("Scanning…", t.Title, t.Text, t.Surface);
        _sub = new TextBlock("", t.Body, t.Muted, t.Surface);
        _ring.BackColor = _free.BackColor = _save.BackColor = _clean.BackColor = _fixes.BackColor = t.Surface;
        _hero.Controls.AddRange([_ring, _status, _headline, _sub, _free, _save, _clean, _fixes]);

        // The apps come before the graph: what uses the RAM now matters more than how it went.
        _listTitle = CardTitle("AI apps");
        _apps.BackColor = t.Surface;
        _apps.Location = new Point(Pad, Pad + 40);
        _list.Controls.AddRange([_listTitle, _apps]);
        _list.Resize += (_, _) => { _apps.Width = _list.Width - 2 * Pad; _listTitle.Width = _apps.Width; };

        _chart.BackColor = t.Surface;
        _chart.Location = new Point(Pad - 4, Pad - 6);
        _hist.Controls.Add(_chart);
        _hist.Resize += (_, _) => _chart.Size = new Size(_hist.Width - 2 * Pad + 8, _hist.Height - Pad - 12);

        _tips.SetToolTip(_clean, "Close leftovers and free idle AI memory now, whatever the limit. Also in the tray menu.");
        _tips.SetToolTip(_pause, "Stop automatic rounds for an hour. \"Free RAM now\" still works.");
        _tips.SetToolTip(_ring, "Brand colour: AI apps. Grey: everything else in use. Empty: free RAM.");
        Stack.Controls.AddRange([_hero, _list, _hist]);
    }

    protected override void Layout2()
    {
        var w = _hero.Width - Col - Pad;
        _status.SetBounds(Col, 34, w, 18);
        _headline.SetBounds(Col, 58, w, 30);
        _sub.SetBounds(Col, 92, w, 20);
        var tile = Math.Min(240, (w - 24) / 2);
        _free.SetBounds(Col, 132, tile, 58);
        _save.SetBounds(Col + tile + 24, 132, tile, 58);
        _clean.Location = new Point(Col, 196);
        _fixes.Location = new Point(_clean.Right + 12, 196);
        _hero.Height = Math.Max(Pad + Ring + Pad, _clean.Bottom + Pad);
    }

    public override void Reload(AppState s)
    {
        var t = Theme.Current;
        _pause.Text = s.Paused ? "Resume" : "Pause 1 h";
        _pause.Glyph = s.Paused ? "\uE768" : "\uE769";
        _clean.Text = s.Busy ? "Working…" : "Free RAM now";
        _status.Text = $"● {s.StatusText}" + (s.LastRun == default ? "" : $"  ·  checked {s.LastRun:HH:mm}  ·  every {s.Settings.Guard.IntervalSeconds / 60.0:0.#} min");
        _status.ForeColor = s.Paused ? t.Warn : t.Good;
        _chart.Set(s.History.Samples);
        _fixes.Visible = s.Advice.Count > 0;
        _fixes.Text = s.Advice.Count == 1 ? "See the fix" : $"See {s.Advice.Count} fixes";

        if (s.Report is { } r)
        {
            var snap = r.Snapshot;
            var used = snap.TotalRamBytes - snap.FreeRamBytes;
            _ring.Set(snap.TotalRamBytes, r.AiBytes, snap.FreeRamBytes);
            _headline.Text = $"AI apps use {Theme.Size(r.AiBytes)}";
            _sub.Text = $"{(double)r.AiBytes / snap.TotalRamBytes:P0} of your {Theme.Size(snap.TotalRamBytes)}. Windows, games and everything else use {Theme.Size(Math.Max(0, used - r.AiBytes))}.";
            var lowMB = s.GameActive && s.Settings.Guard.GameMode ? s.Settings.Guard.GameModeMinFreeMB : s.Settings.Guard.MinFreeMB;
            _free.Set(Theme.Size(snap.FreeRamBytes), $"free · it steps in below {lowMB / 1024} GB", snap.FreeRamBytes >> 20 < lowMB ? t.Warn : t.Good);
            var saves = s.Advice.Sum(a => a.SavingsBytes);
            if (s.Advice.Count == 0) _save.Set("All clear", "no duplicates, no leftovers", t.Good);
            else if (saves > 0) _save.Set($"~{Theme.Size(saves)}", $"{s.Advice.Count} fix{(s.Advice.Count == 1 ? "" : "es")} could save", t.BrandText);
            else _save.Set($"{s.Advice.Count}", s.Advice.Count == 1 ? "thing to check" : "things to check", t.BrandText);
            _listTitle.Text = $"AI apps  ·  {r.Agents.Sum(a => a.Processes.Count)} processes";
            _apps.Set(r.Agents, s.Settings.Guard.AgentCapsMB, snap.TotalRamBytes);
            _list.Height = Pad + 40 + _apps.Height + Pad - 8;
        }
        Layout2();
    }
}

/// <summary>Problems with a button that solves them. What the app can't change itself is listed apart, and what it changed can be undone.</summary>
sealed class FixesPage : Page
{
    readonly AppState _s;
    readonly FlatButton _all = new("Fix all", primary: true, glyph: "\uE90F"), _check = new("Check again", glyph: "\uE72C");
    string _shown = "";
    bool _busy;

    public FixesPage(AppState s) : base("Fixes", "Stop the waste at the source. Whatever AI RAM Guard changes, you can undo here.")
    {
        _s = s;
        _check.Click += (_, _) => s.RunNow(false);
        _all.Click += (_, _) => FixAll();
        Actions.Controls.AddRange([_check, _all]);
    }

    public override void Reload(AppState s)
    {
        var fixable = s.Advice.Where(a => a.CanFix).ToList();
        _all.Visible = fixable.Count > 1; // with one, its own button is the action
        _all.Text = $"Fix all ({fixable.Count})";
        var key = string.Join("|", s.Advice.Select(a => a.Title + a.SavingsBytes / (50 << 20))) + "#" + string.Join(",", s.Fixed.Select(f => f.Id)) + _busy;
        if (key == _shown && Stack.Controls.Count > 0) return; // avoid flicker when nothing meaningful changed
        _shown = key;

        var cards = new List<Control>();
        var needsYou = s.Advice.Where(a => !a.CanFix).ToList();
        if (fixable.Count == 0) cards.Add(AllClear(needsYou.Count > 0));
        cards.AddRange(fixable.Select(a => Build(a, primary: fixable.Count == 1)));
        if (needsYou.Count > 0)
        {
            cards.Add(Section("Needs you", "AI RAM Guard can't change these itself. Each says where to do it."));
            cards.AddRange(needsYou.Select(a => Build(a, primary: false)));
        }
        if (s.Fixed.Count > 0)
        {
            cards.Add(Section("Turned off", "New sessions start without these. Sessions that are open keep their copy until you close them."));
            cards.Add(Done(s.Fixed));
        }
        Stack.SuspendLayout();
        Replace(Stack, cards);
        Stack.ResumeLayout();
    }

    Card AllClear(bool rest)
    {
        var t = Theme.Current;
        var card = NewCard(Pad + 40 + Pad);
        card.Width = ContentWidth;
        card.Controls.Add(new IconCircle("\uE73E", t.Good) { BackColor = t.Surface, Location = new Point(Pad, Pad) });
        card.Controls.Add(new TextBlock(rest ? "Nothing AI RAM Guard can fix by itself" : "Nothing to fix", t.Section, t.Text, t.Surface) { Location = new Point(Pad + 56, Pad - 1), Width = 640 });
        card.Controls.Add(new TextBlock(rest ? "What's left is below, with where to change it." : "No duplicated MCP servers, no leftovers. Your AI tools are behaving.", t.Body, t.Muted, t.Surface) { Location = new Point(Pad + 56, Pad + 24), Width = 640 });
        return card;
    }

    Card Build(Advice a, bool primary)
    {
        var t = Theme.Current;
        const int x = Pad + 40 + 18;
        var card = NewCard(124);
        card.Width = ContentWidth;
        var (glyph, color) = a.Kind switch
        {
            AdviceKind.Orphans => ("\uE74D", t.Warn),
            AdviceKind.Duplicates => ("\uE8C8", a.CanFix ? t.BrandText : t.Muted),
            AdviceKind.NpxWrapper => ("\uE943", t.Muted),
            _ => ("\uE9D9", t.BrandText),
        };
        var icon = new IconCircle(glyph, color) { BackColor = t.Surface, Location = new Point(Pad, Pad) };
        var title = new TextBlock(a.Title, t.Section, t.Text, t.Surface) { Location = new Point(x, Pad) };
        var detail = new TextBlock(a.Detail, t.Body, t.Muted, t.Surface) { Wrap = true, Location = new Point(x, Pad + 30) };
        card.Controls.AddRange([icon, title, detail]);
        var save = a.SavingsBytes > 0 ? new Pill($"saves ~{Theme.Size(a.SavingsBytes)}", t.Good, Color.FromArgb(t.Dark ? 40 : 30, t.Good)) { BackColor = t.Surface } : null;
        if (save is not null) card.Controls.Add(save);
        var action = ActionFor(a, primary);
        if (action is not null) card.Controls.Add(action);

        void Place()
        {
            var right = Math.Max(action?.Width ?? 0, save?.Width ?? 0) + 24;
            title.Width = card.Width - x - right - Pad;
            detail.Width = Math.Min(660, card.Width - x - right - Pad);
            if (save is not null) save.Location = new Point(card.Width - save.Width - Pad, Pad);
            var h = Math.Max(action is null ? Pad + 40 + Pad : 124, detail.Bottom + Pad);
            if (card.Height != h) card.Height = h;
            if (action is not null) action.Location = new Point(card.Width - action.Width - Pad, card.Height - action.Height - Pad);
        }
        card.Resize += (_, _) => Place();
        Place();
        return card;
    }

    FlatButton? ActionFor(Advice a, bool primary)
    {
        FlatButton? b = a.Kind switch
        {
            AdviceKind.Orphans => new FlatButton("Close them", primary),
            AdviceKind.BigApp when _s.Report is { } r => new FlatButton($"Cap at {Advisor.SuggestedCapMB(r.Snapshot.TotalRamBytes) / 1024} GB", primary),
            AdviceKind.Duplicates when a.CanFix => new FlatButton("Turn off", primary, glyph: "\uE7E8"),
            _ when a.ConfigPath is not null => new FlatButton("Open config"),
            _ => null,
        };
        if (b is null) return null;
        b.BackColor = Theme.Current.Surface;
        b.Click += async (_, _) =>
        {
            if (_busy) return;
            if (a.Kind == AdviceKind.Orphans) _s.RunNow(true);
            else if (a.Kind == AdviceKind.BigApp) { SetCap(a); await Recompute(); }
            else if (a.CanFix) await Run(() => Fixer.TurnOff(a.Title, a.Servers!));
            else MainForm.Open(a.ConfigPath!);
        };
        return b;
    }

    Card Done(IReadOnlyList<FixRecord> fixes)
    {
        var card = NewCard(100);
        card.Width = ContentWidth;
        foreach (var f in fixes)
        {
            var names = string.Join(", ", f.Entries.Select(e => e.Server.Name).Distinct());
            var where = string.Join(", ", f.Entries.Select(e => Advisor.Display(e.Server.Path)).Distinct());
            var projects = f.Entries.Count(e => e.Server.Project is not null);
            card.Controls.Add(Button($"{names} is off", $"{where}{(projects > 0 ? $" · and {projects} project{(projects > 1 ? "s" : "")}" : "")} · {f.At:d MMM HH:mm}",
                "Turn on again", async () => await Run(() => Fixer.Undo(f.Id))));
        }
        Fit(card);
        return card;
    }

    void SetCap(Advice a)
    {
        if (a.AgentId is null || _s.Report is not { } r) return;
        var cap = Advisor.SuggestedCapMB(r.Snapshot.TotalRamBytes);
        _s.Settings.Guard.AgentCapsMB[a.AgentId] = cap;
        try { _s.Settings.Save(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(FindForm(), $"The cap is on until AI RAM Guard restarts: the settings file couldn't be saved ({e.Message}).", "AI RAM Guard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        Log.Write($"capped {_s.Profiles.Agents.FirstOrDefault(p => p.Id == a.AgentId)?.Name ?? a.AgentId} at {cap / 1024} GB");
    }

    async void FixAll()
    {
        if (_busy) return;
        var fixable = _s.Advice.Where(a => a.CanFix).ToList();
        var cap = _s.Report is { } rep ? Advisor.SuggestedCapMB(rep.Snapshot.TotalRamBytes) / 1024 : 0;
        var steps = fixable.Select(a => "•  " + (a.Kind switch
        {
            AdviceKind.Orphans => $"Close {a.Title}",
            AdviceKind.BigApp => $"{a.Title}: cap it at {cap} GB",
            _ => $"Turn off {string.Join(", ", a.Servers!.Select(s => s.Name).Distinct())} in {string.Join(", ", a.Servers!.Select(s => Advisor.Display(s.Path)).Distinct())}",
        }));
        if (MessageBox.Show(FindForm(), "AI RAM Guard will:\n\n" + string.Join("\n", steps) + "\n\nTurned-off servers can be turned on again on this page.",
            "Fix all", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;

        foreach (var a in fixable.Where(a => a.Kind == AdviceKind.BigApp)) SetCap(a);
        var servers = fixable.Where(a => a.Kind == AdviceKind.Duplicates).ToList();
        await Run(() =>
        {
            var failed = servers.Select(a => Fixer.TurnOff(a.Title, a.Servers!)).Where(r => !r.Ok).Select(r => r.Message).ToList();
            return failed.Count == 0 ? new FixResult(true, "") : new FixResult(false, string.Join("\n\n", failed));
        });
        if (fixable.Any(a => a.Kind == AdviceKind.Orphans)) _s.RunNow(true);
    }

    /// <summary>Off the UI thread (a config can be big), then the page shows the new state straight away.</summary>
    async Task Run(Func<FixResult> work)
    {
        if (_busy) return;
        _busy = true;
        FixResult r;
        try { r = await Task.Run(work); }
        catch (Exception e) { r = new(false, e.Message); }
        _busy = false;
        if (IsDisposed) return;
        if (!r.Ok) MessageBox.Show(FindForm(), r.Message, "AI RAM Guard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        await Recompute();
    }

    /// <summary>New advice from the last scan and the configs as they are now: the next round may be two minutes away.</summary>
    async Task Recompute()
    {
        var caps = _s.Settings.Guard.AgentCapsMB;
        var report = _s.Report;
        IReadOnlyList<Advice> advice;
        IReadOnlyList<FixRecord> applied;
        try
        {
            (advice, applied) = await Task.Run(() =>
            {
                var configs = KnownConfigs.Read();
                var f = Fixer.Applied(configs);
                return (report is null ? _s.Advice : Advisor.Advise(report, configs, f, caps), f);
            });
        }
        catch (Exception e) { Log.Write($"fixes refresh failed: {e.Message}"); return; } // async void caller: the next round shows the new state
        _s.Advice = advice;
        _s.Fixed = applied;
        _s.Notify();
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
        _list.Location = new Point(Pad, Pad - 8);
        _card.Controls.Add(_list);
        _card.Resize += (_, _) => _list.Width = _card.Width - 2 * Pad;
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
        _list.Set(Group(Enumerable.Reverse(lines).Select(Parse).OfType<ActivityEntry>()).Take(150).ToList());
        _card.Height = _list.Height + Pad;
    }

    /// <summary>Back-to-back trims are one story ("Freed 41 GB in 12 rounds"), not twelve rows; so are priority changes.</summary>
    internal static IEnumerable<ActivityEntry> Group(IEnumerable<ActivityEntry> newestFirst)
    {
        ActivityEntry? run = null;
        foreach (var e in newestFirst)
        {
            if (run is { Group: not null } && e.Group == run.Group && e.Time.Date == run.Time.Date)
            {
                run = run with { Rounds = run.Rounds + 1, Freed = run.Freed + e.Freed, Since = e.Time };
                continue;
            }
            if (run is not null) yield return Finish(run);
            run = e with { Since = e.Time };
        }
        if (run is not null) yield return Finish(run);

        static ActivityEntry Finish(ActivityEntry r) => r.Rounds < 2 ? r : r.Group == "trim"
            ? r with { Title = $"Freed {Theme.Size(r.Freed)} in {r.Rounds} rounds", Detail = $"{r.Since:HH:mm}–{r.Time:HH:mm} · idle AI memory freed while RAM was low" }
            : r with { Title = $"CPU priority changed {r.Rounds} times", Detail = $"{r.Since:HH:mm}–{r.Time:HH:mm} · lowered while a game was in front, back to normal after" };
    }

    static readonly System.Text.RegularExpressions.Regex Round = new(
        @"^(?<dry>\[dry-run\] )?(?<reason>.+): closed (?<k>\d+) orphans, trimmed (?<t>\d+) AI processes(?:, freed (?<f>\d+) MB)?, free (?<a>[\d.]+) -> (?<b>[\d.]+) GB$");
    static readonly System.Text.RegularExpressions.Regex Orphan = new(@"^(?<verb>closed|could not close|would close) orphan (?<name>\S+) (?<pid>\d+) (?<mb>\d+) MB (?<key>.+)$");
    static readonly System.Text.RegularExpressions.Regex Switched = new(@"^turned (?<how>off|on) (?<name>\S+) (?:again )?in (?<where>.+)$");
    static readonly System.Text.RegularExpressions.Regex Priority = new(@"^(?<how>game in front: lowered|restored) CPU priority of (?<n>\d+) AI processes$");
    static readonly System.Text.RegularExpressions.Regex Capped = new(@"^capped (?<name>.+) at (?<gb>\d+) GB$");
    static readonly System.Text.RegularExpressions.Regex Temp = new(@"^cleaned temp (?<dir>.+): (?<n>\d+) files, (?<mb>\d+) MB deleted, (?<s>\d+) in use$");

    /// <summary>"2026-10-25 12:20:21 installed 1.0.0 to …" → a timeline entry in plain words.</summary>
    internal static ActivityEntry? Parse(string line)
    {
        if (line.Length < 20 || !DateTime.TryParseExact(line[..19], "yyyy-MM-dd HH:mm:ss", null, System.Globalization.DateTimeStyles.None, out var time)) return null;
        var msg = line[20..];
        var t = Theme.Current;
        if (Round.Match(msg) is { Success: true } m)
        {
            var mb = m.Groups["f"].Success ? long.Parse(m.Groups["f"].Value) : 0;
            var freed = m.Groups["f"].Success ? $"Freed {Theme.Size(mb << 20)}" : "Freed memory";
            var parts = new List<string>();
            if (m.Groups["t"].Value != "0") parts.Add($"{m.Groups["t"].Value} AI processes trimmed");
            if (m.Groups["k"].Value != "0") parts.Add($"{m.Groups["k"].Value} leftovers closed");
            parts.Add(m.Groups["reason"].Value);
            return m.Groups["dry"].Success
                ? new(time, "\uE945", t.Muted, "Would have freed memory (guard is off)", string.Join(" · ", parts))
                // Only plain trims group: a round that closed leftovers is worth its own row.
                : new(time, "\uE945", Theme.Brand, freed, string.Join(" · ", parts) + $" · free {m.Groups["a"].Value} → {m.Groups["b"].Value} GB",
                    mb << 20, m.Groups["k"].Value == "0" ? "trim" : null);
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
        if (Switched.Match(msg) is { Success: true } sw)
            return sw.Groups["how"].Value == "off"
                ? new(time, "\uE7E8", t.Good, $"Turned off {sw.Groups["name"].Value}", $"{sw.Groups["where"].Value} · new sessions start without it")
                : new(time, "\uE7A7", t.Muted, $"Turned {sw.Groups["name"].Value} on again", sw.Groups["where"].Value);
        if (Temp.Match(msg) is { Success: true } tm)
            return new(time, "\uE74D", t.Good, $"Cleaned up {Theme.Size(long.Parse(tm.Groups["mb"].Value) << 20)} of old temp files",
                $"{long.Parse(tm.Groups["n"].Value):N0} files · {tm.Groups["dir"].Value}" + (tm.Groups["s"].Value != "0" ? $" · {tm.Groups["s"].Value} in use, kept" : ""));
        if (Capped.Match(msg) is { Success: true } cp)
            return new(time, "\uE9D9", Theme.Brand, $"Capped {cp.Groups["name"].Value} at {cp.Groups["gb"].Value} GB", "Above it, its idle memory is freed even when RAM isn't low");
        if (Priority.Match(msg) is { Success: true } pr)
            return pr.Groups["how"].Value == "restored"
                ? new(time, "\uE7FC", t.Muted, "Game closed: CPU priority back to normal", $"{pr.Groups["n"].Value} AI processes", Group: "priority")
                : new(time, "\uE7FC", t.Muted, "Game in front: AI apps got less CPU", $"{pr.Groups["n"].Value} AI processes at below-normal priority", Group: "priority");
        if (msg.StartsWith("installed ")) return new(time, "\uE896", t.Good, $"Installed version {msg.Split(' ')[1]}", "Start menu, autostart and Settings → Apps");
        if (msg.StartsWith("paused until ")) return new(time, "\uE769", t.Warn, $"Paused until {msg[13..]}", "Automatic rounds stopped");
        if (msg == "resumed") return new(time, "\uE768", t.Good, "Resumed guarding", "");
        if (msg.StartsWith("round failed: ") || msg.StartsWith("ignored ") || msg.StartsWith("install failed: "))
            return new(time, "\uE7BA", t.Warn, "Something went wrong", msg);
        return new(time, "\uE946", t.Muted, msg, "");
    }
}

/// <summary>Where AI tools write on disk, with one-click moves of caches to another drive (and back) and a temp clean-up.</summary>
sealed class DiskPage : Page
{
    readonly Card _card = NewCard(120);
    readonly FlatButton _measure = new("Measure again", glyph: "\uE72C");
    IReadOnlyList<SpotUsage>? _spots;
    bool _busy;

    public bool Busy => _busy;

    public DiskPage() : base("Disk", "Where AI tools and their caches write. Clean up old temp files, or move a cache off the system drive.")
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
        var old = _card.Controls.Cast<Control>().ToList();
        _card.Controls.Clear();
        foreach (var c in old) c.Dispose();
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
        ShowCard($"{Theme.Size(onSystem)} on the system drive  ·  {Theme.Size(shown.Sum(u => u.Bytes) - onSystem)} elsewhere",
            shown.Select(u => Row(u, moved.Contains(u.Spot.Id))).ToArray());
    }

    Control Row(SpotUsage u, bool movedByUs)
    {
        var where = $"{Advisor.Display(u.RealPath)} · {(u.OnSystemDrive ? "system drive" : $"on {u.Drive}")}";
        var text = $"{u.Spot.Name} · {Theme.Size(u.Bytes)}";
        if (movedByUs) return Button(text, where + " · moved by AI RAM Guard", "Undo move", () => UndoMove(u));
        void Open() { if (Directory.Exists(u.RealPath)) MainForm.Open(u.RealPath); }
        if (!u.Spot.Movable) return Button(text, where + $" · holds sessions: close the app, then set {u.Spot.EnvVars[0]}", "Open", Open);
        if (u.Links > 0) return Button(text, where + $" · {u.Links} links inside: only an admin can move those, so it stays", "Open", Open);
        var targets = u.OnSystemDrive ? Storage.Targets(u).OrderByDescending(d => d.AvailableFreeSpace).ToList() : [];
        (string, Action)? move = targets.Count switch
        {
            0 => null,
            1 => ($"Move to {targets[0].Name.TrimEnd('\\')}", () => MoveTo(u, targets[0])),
            _ => ("Move to…", () =>
            {
                var menu = new ContextMenuStrip { Renderer = new MenuRenderer(Theme.Current) };
                foreach (var d in targets) menu.Items.Add($"{d.Name.TrimEnd('\\')}  ({Theme.Size(d.AvailableFreeSpace)} free)", null, (_, _) => MoveTo(u, d));
                menu.Show(Cursor.Position);
            }),
        };
        if (u.Spot.Id == "temp") return Button(text, where + " · old files can go", "Clean up", () => CleanTemp(u), move);
        return move is { } m ? Button(text, where, m.Item1, m.Item2) : Button(text, where, "Open", Open);
    }

    async void CleanTemp(SpotUsage u)
    {
        if (_busy) return;
        _busy = true;
        ShowCard("Looking for temp files nobody touched for 7 days…");
        TempFiles found;
        try { found = await Task.Run(() => TempCleaner.Find(u.RealPath)); }
        catch (Exception e) { found = new(0, 0); Log.Write($"temp scan failed: {e.Message}"); }
        _busy = false;
        OnUi(Render);
        if (found.Files == 0)
        {
            MessageBox.Show(FindForm(), $"Nothing to clean: no file in {u.RealPath} is older than 7 days.", "AI RAM Guard", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (MessageBox.Show(FindForm(), $"Delete {found.Files:N0} files ({Theme.Size(found.Bytes)}) from {u.RealPath} that nothing has touched for 7 days?\n\nFiles in use are skipped. Deleted files don't go to the Recycle Bin. This is the rule Windows' own Disk Cleanup uses.",
            "AI RAM Guard", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
        _busy = true;
        ShowCard("Cleaning up old temp files…");
        TempFiles r;
        try { r = await Task.Run(() => TempCleaner.Clean(u.RealPath)); }
        catch (Exception e) { r = new(0, 0); Log.Write($"temp clean-up failed: {e.Message}"); }
        _busy = false;
        OnUi(() =>
        {
            MessageBox.Show(FindForm(), $"Deleted {r.Files:N0} files and freed {Theme.Size(r.Bytes)}." + (r.Skipped > 0 ? $" {r.Skipped:N0} were in use and stayed." : ""),
                "AI RAM Guard", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Measure();
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
            Button("Data folder", "Settings, history, statistics, config backups and the log.", "Open", () => { Directory.CreateDirectory(Paths.DataDir); MainForm.Open(Paths.DataDir); }));

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
        var key = string.Join(",", ids.Select(p => p.Id + s.Settings.Guard.AgentCapsMB.GetValueOrDefault(p.Id)));
        if (key == _capsShown) return;
        _capsShown = key;
        while (_caps.Controls.Count > 1) _caps.Controls[1].Dispose();
        if (ids.Count == 0)
            _caps.Controls.Add(new TextBlock("No AI apps running right now.", Theme.Current.Body, Theme.Current.Muted, Theme.Current.Surface));
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
        var label = new TextBlock(text, t.Body, t.Text, t.Surface) { Location = new Point(0, 7) };
        var sub = new TextBlock(hint, t.Small, t.Muted, t.Surface) { Location = new Point(0, 29) };
        stepper.BackColor = t.Surface;
        stepper.ValueChanged += (_, _) => { set(stepper.Value); _s.Settings.Save(); _s.Notify(); };
        row.Controls.AddRange([label, sub, stepper]);
        row.Resize += (_, _) =>
        {
            stepper.Location = new Point(row.Width - stepper.Width, 9);
            label.Width = sub.Width = Math.Max(100, stepper.Left - 16);
        };
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

        var logo = new PictureBox { Image = MainForm.Logo(64), SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(36, 36), Location = new Point(22, 26), BackColor = t.Surface };
        var name = new Label { Text = "AI RAM Guard", Font = new Font("Segoe UI Semibold", 13f), ForeColor = t.Text, AutoSize = true, Location = new Point(66, 24), BackColor = t.Surface };
        var by = new Label { Text = "by Byens IT", Font = t.Small, ForeColor = t.Muted, AutoSize = true, Location = new Point(68, 46), BackColor = t.Surface };
        Controls.AddRange([logo, name, by]);

        var y = 104;
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
        var n = _state.Advice.Count(a => a.CanFix);
        _buttons.First(b => b.Text == "Fixes").Badge = n > 0 ? n.ToString() : "";
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
