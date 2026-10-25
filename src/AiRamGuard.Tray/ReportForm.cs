using AiRamGuard.Core;

namespace AiRamGuard.Tray;

/// <summary>Which AI app uses how much RAM, its MCP servers, and leftovers from closed sessions.</summary>
sealed class ReportForm : Form
{
    readonly ListView _list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, BorderStyle = BorderStyle.None };
    readonly Label _summary = new() { Dock = DockStyle.Fill, ForeColor = Color.White, Font = new Font("Segoe UI", 11f), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(12, 0, 0, 0) };

    public ReportForm(Action clean)
    {
        Text = "AI RAM Guard";
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        Size = new Size(720, 520);
        MinimumSize = new Size(520, 320);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);

        var header = new TableLayoutPanel { Dock = DockStyle.Top, Height = 56, BackColor = TrayContext.Brand, ColumnCount = 2 };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var btn = new Button { Text = "Free RAM now", AutoSize = true, FlatStyle = FlatStyle.Flat, ForeColor = Color.White, Margin = new Padding(8, 12, 12, 8) };
        btn.FlatAppearance.BorderColor = Color.White;
        btn.Click += (_, _) => clean();
        header.Controls.Add(_summary, 0, 0);
        header.Controls.Add(btn, 1, 0);

        _list.Columns.Add("AI app / helper", 400);
        _list.Columns.Add("Processes", 90, HorizontalAlignment.Right);
        _list.Columns.Add("RAM", 110, HorizontalAlignment.Right);

        var footer = new LinkLabel
        {
            Dock = DockStyle.Bottom, Height = 28, TextAlign = ContentAlignment.MiddleCenter,
            Text = "Open source by Magnus Steinmeier Olsen · Byens IT — github.com/byensitmagnus/ai-ram-guard",
        };
        footer.LinkArea = new LinkArea(footer.Text.IndexOf("github", StringComparison.Ordinal), "github.com/byensitmagnus/ai-ram-guard".Length);
        footer.LinkClicked += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/byensitmagnus/ai-ram-guard") { UseShellExecute = true });

        Controls.Add(_list);
        Controls.Add(footer);
        Controls.Add(header);
    }

    public void Render(Report r)
    {
        if (IsDisposed) return;
        var s = r.Snapshot;
        _summary.Text = $"{Guard.Gb(s.FreeRamBytes)} GB free of {Guard.Gb(s.TotalRamBytes)} GB  ·  AI apps use {Guard.Gb(r.AiBytes)} GB";

        _list.BeginUpdate();
        _list.Items.Clear();
        _list.Groups.Clear();
        foreach (var a in r.Agents)
        {
            var g = _list.Groups.Add(a.Profile.Id, a.Profile.Name);
            Add(g, $"{a.Profile.Name} (total)", a.Processes.Count, a.Bytes, bold: true);
            foreach (var h in a.Helpers)
                Add(g, h.Count > 2 ? $"   {h.Key}   ⚠ {h.Count} copies" : $"   {h.Key}", h.Count, h.Bytes);
        }
        if (r.Orphans.Count > 0)
        {
            var g = _list.Groups.Add("orphans", "Orphans — their AI session is closed, safe to close");
            foreach (var h in Analyzer.GroupHelpers(r.Orphans)) Add(g, $"   {h.Key}", h.Count, h.Bytes, warn: true);
        }
        _list.EndUpdate();
    }

    void Add(ListViewGroup g, string name, int count, long bytes, bool bold = false, bool warn = false)
    {
        var item = new ListViewItem([name, count.ToString(), $"{bytes / 1048576:N0} MB"], g);
        if (bold) item.Font = new Font(Font, FontStyle.Bold);
        if (warn) item.ForeColor = Color.OrangeRed;
        _list.Items.Add(item);
    }
}
