using System.Diagnostics;
using System.Drawing.Drawing2D;
using AiRamGuard.Core;

namespace AiRamGuard.Tray;

sealed class TrayContext : ApplicationContext
{
    public static readonly Color Brand = ColorTranslator.FromHtml("#ba01c4");
    const string RepoUrl = "https://github.com/byensitmagnus/ai-ram-guard";

    readonly NotifyIcon _icon = new() { Visible = true, Text = "AI RAM Guard" };
    readonly System.Windows.Forms.Timer _timer = new();
    readonly AppSettings _s = AppSettings.Load();
    readonly ProfileSet _profiles = ProfileSet.Load();
    readonly ToolStripMenuItem _status = new() { Enabled = false };
    DateTime _lastToast = DateTime.MinValue;
    ReportForm? _form;
    bool _busy;

    public TrayContext()
    {
        _icon.ContextMenuStrip = BuildMenu();
        _icon.DoubleClick += (_, _) => ShowReport();
        _timer.Interval = Math.Max(30, _s.Guard.IntervalSeconds) * 1000;
        _timer.Tick += (_, _) => Tick(force: false);
        _timer.Start();
        Tick(force: false);
    }

    ContextMenuStrip BuildMenu()
    {
        var m = new ContextMenuStrip();
        m.Items.Add(_status);
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Show AI RAM report…", null, (_, _) => ShowReport());
        m.Items.Add("Free RAM now", null, (_, _) => Tick(force: true));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(Toggle("Auto guard", () => _s.AutoGuard, v => _s.AutoGuard = v));
        m.Items.Add(Toggle("Game mode (stricter while a fullscreen game runs)", () => _s.Guard.GameMode, v => _s.Guard.GameMode = v));
        m.Items.Add(Toggle("Close orphaned MCP servers", () => _s.Guard.KillOrphans, v => _s.Guard.KillOrphans = v));
        m.Items.Add(Toggle("Notifications", () => _s.Notify, v => _s.Notify = v));

        var limit = new ToolStripMenuItem("Free AI memory when free RAM is below");
        foreach (var gb in new[] { 2, 4, 6, 8, 12, 16 })
        {
            var item = new ToolStripMenuItem($"{gb} GB") { Checked = _s.Guard.MinFreeMB == gb * 1024 };
            item.Click += (_, _) =>
            {
                _s.Guard.MinFreeMB = gb * 1024;
                _s.Guard.GameModeMinFreeMB = Math.Max(_s.Guard.GameModeMinFreeMB, gb * 1024);
                _s.Save();
                foreach (ToolStripMenuItem i in limit.DropDownItems) i.Checked = i == item;
            };
            limit.DropDownItems.Add(item);
        }
        m.Items.Add(limit);
        m.Items.Add(Toggle("Start with Windows", () => Autostart.Enabled, v => Autostart.Enabled = v, save: false));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Open log", null, (_, _) => Open(File.Exists(Paths.LogFile) ? Paths.LogFile : Path.GetDirectoryName(Paths.LogFile)!));
        m.Items.Add("Add your own AI app…", null, (_, _) => OpenUserProfiles());
        m.Items.Add("About / GitHub", null, (_, _) => Open(RepoUrl));
        m.Items.Add("Exit", null, (_, _) => ExitThread());
        return m;
    }

    ToolStripMenuItem Toggle(string text, Func<bool> get, Action<bool> set, bool save = true)
    {
        var item = new ToolStripMenuItem(text) { Checked = get(), CheckOnClick = true };
        item.CheckedChanged += (_, _) => { set(item.Checked); if (save) _s.Save(); };
        return item;
    }

    async void Tick(bool force)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var dryRun = !force && !_s.AutoGuard; // auto guard off: only measure
            var (result, report) = await Task.Run(() =>
            {
                var r = Guard.Run(_s.Guard, _profiles, force, dryRun);
                return (r, Analyzer.Analyze(SnapshotReader.Read(), _profiles));
            });
            Render(report);
            var freed = result.FreeAfterBytes - result.FreeBeforeBytes;
            if (force)
                _icon.ShowBalloonTip(3000, "AI RAM Guard", $"Freed {Guard.Gb(Math.Max(0, freed))} GB. Closed {result.Killed} orphans, trimmed {result.Trimmed} AI processes.", ToolTipIcon.Info);
            else if (_s.Notify && !dryRun && freed > 1L << 30 && DateTime.Now - _lastToast > TimeSpan.FromMinutes(30))
            {
                _lastToast = DateTime.Now;
                _icon.ShowBalloonTip(3000, "AI RAM Guard", $"Freed {Guard.Gb(freed)} GB from AI apps ({result.Reason}).", ToolTipIcon.Info);
            }
        }
        catch (Exception e) when (e is System.Management.ManagementException or System.Runtime.InteropServices.COMException)
        {
            Log.Write($"scan failed: {e.Message}");
        }
        finally
        {
            _busy = false;
            if (_form is null) Guard.TrimSelf();
        }
    }

    void Render(Report r)
    {
        var s = r.Snapshot;
        var usedPct = 1 - (double)s.FreeRamBytes / s.TotalRamBytes;
        var aiPct = (double)r.AiBytes / s.TotalRamBytes;
        var text = $"Free {Guard.Gb(s.FreeRamBytes)} GB · AI apps {Guard.Gb(r.AiBytes)} GB";
        _status.Text = text + (r.Orphans.Count > 0 ? $" · {r.Orphans.Count} orphans" : "");
        _icon.Text = ("AI RAM Guard\n" + text)[..Math.Min(127, 13 + text.Length)];
        var old = _icon.Icon;
        _icon.Icon = DrawIcon(usedPct, aiPct);
        old?.Dispose();
        _form?.Render(r);
    }

    /// <summary>A ring: grey = RAM used by everything, brand colour = the part AI apps use.</summary>
    static Icon DrawIcon(double usedPct, double aiPct)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(3, 3, 26, 26);
            using var track = new Pen(Color.FromArgb(90, 128, 128, 128), 6);
            using var used = new Pen(usedPct > 0.85 ? Color.OrangeRed : Color.Gray, 6);
            using var ai = new Pen(Brand, 6);
            g.DrawEllipse(track, rect);
            g.DrawArc(used, rect, -90, (float)(360 * usedPct));
            g.DrawArc(ai, rect, -90, (float)(360 * Math.Min(aiPct, usedPct)));
            using var dot = new SolidBrush(Brand);
            g.FillEllipse(dot, 12, 12, 8, 8);
        }
        var h = bmp.GetHicon();
        using var tmp = Icon.FromHandle(h);
        var icon = (Icon)tmp.Clone();
        DestroyIcon(h);
        return icon;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

    void ShowReport()
    {
        if (_form is null || _form.IsDisposed)
        {
            _form = new ReportForm(() => Tick(force: true));
            _form.FormClosed += (_, _) => _form = null;
        }
        _form.Visible = true;
        _form.Activate();
        Tick(force: false);
    }

    void OpenUserProfiles()
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
        Open(file);
    }

    static void Open(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });

    protected override void ExitThreadCore()
    {
        _timer.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        base.ExitThreadCore();
    }
}
