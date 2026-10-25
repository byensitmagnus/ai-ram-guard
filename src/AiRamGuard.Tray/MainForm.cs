using System.Diagnostics;
using AiRamGuard.Core;

namespace AiRamGuard.Tray;

/// <summary>The app window: sidebar navigation and four pages.</summary>
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
            ["Activity"] = new ActivityPage(state),
            ["Settings"] = new SettingsPage(state),
        };
        _sidebar = new Sidebar(state, [("Overview", ""), ("Recommendations", ""), ("Activity", ""), ("Settings", "")], Navigate);
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

    void Navigate(string name)
    {
        _current.Visible = false;
        _current = _pages[name];
        _current.Visible = true;
        _current.Reload(_state);
        _sidebar.Select(name);
    }

    public static void Open(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
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
        Heading.Font = new Font("Segoe UI Semibold", 20f);
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
        Text = text, AutoSize = true, Font = new Font("Segoe UI Semibold", 12f), ForeColor = Theme.Current.Text,
        BackColor = Theme.Current.Surface, Location = new Point(18, 14),
    };

    protected static Label Wrapping(string text, Font font, Color color, int width) => new()
    {
        Text = text, Font = font, ForeColor = color, BackColor = Theme.Current.Surface, AutoSize = true, MaximumSize = new Size(width, 0),
    };
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

        _list.Controls.Add(CardTitle("AI apps"));
        var more = new LinkLabel { Text = "See how to fix the waste →", AutoSize = true, BackColor = t.Surface, LinkColor = Theme.Brand, ActiveLinkColor = Theme.Brand };
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
        _ai.Set("AI apps use", Theme.Size(r.AiBytes), $"{r.Agents.Sum(a => a.Processes.Count)} processes in {r.Agents.Count} apps", Theme.Brand);
        _other.Set("Everything else", Theme.Size(Math.Max(0, used - r.AiBytes)), $"of {Theme.Size(snap.TotalRamBytes)} installed");
        _freed.Set("Freed by AI RAM Guard", Theme.Size(s.Stats.FreedMB << 20), $"since {s.Stats.Since:d MMM yyyy}");
        _orphans.Set("Leftovers right now", r.Orphans.Count == 0 ? "None" : $"{r.Orphans.Count} · {Theme.Size(r.Orphans.Sum(o => o.WorkingSetBytes))}",
            $"{s.Stats.OrphansClosed} closed so far", r.Orphans.Count > 0 ? t.Warn : t.Good);
        _apps.Set(r.Agents, s.Settings.Guard.AgentCapsMB);
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
        var title = new Label { Text = a.Title, Font = t.Bold, ForeColor = t.Text, BackColor = t.Surface, AutoSize = true, Location = new Point(60, 18) };
        var detail = Wrapping(a.Detail, t.Body, t.Muted, ContentWidth - 260);
        detail.Location = new Point(60, 44);
        card.Controls.AddRange([icon, title, detail]);

        if (a.SavingsBytes > 0)
        {
            var save = new Label { Text = $"saves ~{Theme.Size(a.SavingsBytes)}", Font = t.Bold, ForeColor = t.Good, BackColor = t.Surface, AutoSize = true };
            card.Controls.Add(save);
            card.Resize += (_, _) => save.Location = new Point(card.Width - save.Width - 22, 18);
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
    readonly Card _card = NewCard(520);
    readonly TextBox _log = new() { Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, ScrollBars = ScrollBars.Vertical, WordWrap = false };

    public ActivityPage(AppState s) : base("Activity", "Everything AI RAM Guard did, newest first.")
    {
        var t = Theme.Current;
        _log.BackColor = t.Surface;
        _log.ForeColor = t.Text;
        t.ApplyScrollbars(_log);
        _log.Font = new Font("Cascadia Mono", 9.5f);
        if (_log.Font.Name != "Cascadia Mono") _log.Font = new Font("Consolas", 9.5f);
        _log.Location = new Point(18, 18);
        _card.Controls.Add(_log);
        _card.Resize += (_, _) => _log.Size = new Size(_card.Width - 36, _card.Height - 36);
        var open = new FlatButton("Open log file", glyph: "");
        open.Click += (_, _) => { if (File.Exists(Paths.LogFile)) MainForm.Open(Paths.LogFile); };
        Actions.Controls.Add(open);
        Stack.Controls.Add(_card);
    }

    protected override void Layout2() => _card.Height = Math.Max(300, ClientSize.Height - 120);

    public override void Reload(AppState s)
    {
        string[] lines;
        try { lines = File.Exists(Paths.LogFile) ? File.ReadAllLines(Paths.LogFile) : []; }
        catch (IOException) { return; } // the guard is writing right now; next refresh picks it up
        var text = lines.Length == 0
            ? "Nothing yet. When AI RAM Guard closes a leftover or frees memory, it shows up here."
            : string.Join(Environment.NewLine, Enumerable.Reverse(lines).Take(400));
        if (_log.Text != text) _log.Text = text;
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

    /// <summary>Stack rows under the title and size the card to them.</summary>
    static void Fit(Card c)
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

    static Control Button(string text, string hint, string buttonText, Action onClick)
    {
        var t = Theme.Current;
        var row = new Panel { Height = 52, BackColor = t.Surface };
        var label = new Label { Text = text, AutoSize = true, Location = new Point(0, 6), ForeColor = t.Text, BackColor = t.Surface };
        var sub = new Label { Text = hint, AutoSize = true, Location = new Point(0, 28), ForeColor = t.Muted, BackColor = t.Surface, Font = t.Small };
        var b = new FlatButton(buttonText) { BackColor = t.Surface };
        b.Click += (_, _) => onClick();
        row.Controls.AddRange([label, sub, b]);
        row.Resize += (_, _) => b.Location = new Point(row.Width - b.Width, 7);
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

        var logo = new PictureBox { Image = Icon.ExtractAssociatedIcon(Environment.ProcessPath!)?.ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(36, 36), Location = new Point(22, 24), BackColor = t.Surface };
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

        public NavButton(string text, string glyph) { base.Text = text; _glyph = glyph; Height = 40; Cursor = Cursors.Hand; BackColor = Theme.Current.Surface; }

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
            DrawText(g, _glyph, T.Icon, _selected ? Theme.Brand : T.Muted, new RectangleF(16, 0, 24, Height));
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
