using System.Drawing.Drawing2D;
using System.Drawing.Text;
using AiRamGuard.Core;

namespace AiRamGuard.Tray;

/// <summary>Base for owner-drawn controls: double buffered, anti-aliased, theme aware.</summary>
abstract class Painted : Control
{
    protected Theme T => Theme.Current;
    protected bool Hover;

    protected Painted()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
            | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        Draw(e.Graphics);
    }

    protected abstract void Draw(Graphics g);

    protected override void OnMouseEnter(EventArgs e) { Hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { Hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected static void DrawText(Graphics g, string s, Font f, Color c, RectangleF r, StringAlignment h = StringAlignment.Near, StringAlignment v = StringAlignment.Center)
    {
        using var b = new SolidBrush(c);
        using var fmt = new StringFormat { Alignment = h, LineAlignment = v, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        g.DrawString(s, f, b, r, fmt);
    }
}

/// <summary>A rounded surface that holds other controls.</summary>
sealed class Card : Panel
{
    public Card()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Padding = new Padding(18);
        BackColor = Theme.Current.Bg;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var t = Theme.Current;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Theme.Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 12);
        using var fill = new SolidBrush(t.Surface);
        using var pen = new Pen(t.Border);
        e.Graphics.FillPath(fill, path);
        e.Graphics.DrawPath(pen, path);
    }
}

/// <summary>Donut: AI share in brand colour, everything else grey, free RAM as the empty track.</summary>
sealed class RingGauge : Painted
{
    long _total = 1, _ai, _free;

    public void Set(long total, long ai, long free) { _total = Math.Max(1, total); _ai = ai; _free = free; Invalidate(); }

    protected override void Draw(Graphics g)
    {
        var size = Math.Min(Width, Height) - 8;
        var r = new RectangleF((Width - size) / 2f, (Height - size) / 2f, size, size);
        var thick = size * 0.12f;
        r.Inflate(-thick / 2, -thick / 2);
        var used = _total - _free;
        var aiShown = Math.Min(_ai, used);

        using (var track = new Pen(T.Surface2, thick)) g.DrawEllipse(track, r);
        using (var other = new Pen(T.Other, thick) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            if (used > 0) g.DrawArc(other, r, -90, 360f * used / _total);
        using (var ai = new LinearGradientBrush(r, Theme.Brand, Theme.BrandDeep, 45f))
        using (var aiPen = new Pen(ai, thick) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            if (aiShown > 0) g.DrawArc(aiPen, r, -90, 360f * aiShown / _total);

        var center = new RectangleF(r.X, r.Y + r.Height * 0.28f, r.Width, r.Height * 0.3f);
        DrawText(g, Theme.Size(_free), T.Big, T.Text, center, StringAlignment.Center);
        DrawText(g, "free", T.Body, T.Muted, new RectangleF(r.X, center.Bottom, r.Width, 22), StringAlignment.Center);
    }
}

/// <summary>24-hour graph: RAM in use (grey) and the part AI apps use (brand), with a hover readout.</summary>
sealed class HistoryChart : Painted
{
    IReadOnlyList<Sample> _samples = [];
    int _hoverX = -1;

    public void Set(IReadOnlyList<Sample> samples) { _samples = samples; Invalidate(); }

    protected override void OnMouseMove(MouseEventArgs e) { _hoverX = e.X; Invalidate(); base.OnMouseMove(e); }
    protected override void OnMouseLeave(EventArgs e) { _hoverX = -1; base.OnMouseLeave(e); }

    protected override void Draw(Graphics g)
    {
        var plot = new RectangleF(44, 52, Width - 64, Height - 76);
        // A new install has minutes of data, not a day: zoom to the smallest window that holds it.
        var now = DateTime.Now;
        var oldest = _samples.Count > 0 ? now - _samples[0].Time : TimeSpan.Zero;
        var hours = new[] { 1, 3, 6, 12, 24 }.First(h => h == 24 || oldest <= TimeSpan.FromHours(h));
        var window = TimeSpan.FromHours(hours);
        DrawText(g, hours == 1 ? "Last hour" : $"Last {hours} hours", new Font("Segoe UI Semibold", 12f), T.Text, new RectangleF(4, 0, 300, 30));
        Legend(g, Width - 8, 6, ("AI apps", Theme.Brand), ("all RAM in use", T.Other));
        using var grid = new Pen(T.Border) { DashStyle = DashStyle.Dot };
        var totalMB = _samples.Count > 0 ? _samples.Max(s => s.TotalMB) : 32768;
        for (var i = 0; i <= 4; i++)
        {
            var y = plot.Bottom - plot.Height * i / 4;
            g.DrawLine(grid, plot.Left, y, plot.Right, y);
            DrawText(g, $"{totalMB * i / 4 / 1024} GB", T.Small, T.Muted, new RectangleF(0, y - 9, 40, 18), StringAlignment.Far);
        }

        var start = now - window;
        for (var i = 0; i <= 4; i++)
        {
            var x = plot.Left + plot.Width * i / 4;
            var ago = window * (4 - i) / 4;
            var text = i == 4 ? "now" : ago.TotalHours >= 1 && ago.TotalHours % 1 == 0 ? $"-{ago.TotalHours:0}h" : $"-{ago.TotalMinutes:0} min";
            DrawText(g, text, T.Small, T.Muted, i == 4 ? new RectangleF(x - 60, plot.Bottom + 6, 62, 18) : new RectangleF(x - 30, plot.Bottom + 6, 60, 18), i == 4 ? StringAlignment.Far : StringAlignment.Center);
        }

        if (_samples.Count < 2)
        {
            DrawText(g, "Collecting data… the graph fills in over the next hours.", T.Body, T.Muted, plot, StringAlignment.Center);
            return;
        }

        PointF Pt(Sample s, int mb) => new(
            plot.Left + plot.Width * (float)((s.Time - start).TotalSeconds / window.TotalSeconds),
            plot.Bottom - plot.Height * mb / totalMB);

        var visible = _samples.Where(s => s.Time >= start).ToList();
        if (visible.Count < 2) return;
        Area(g, visible.Select(s => Pt(s, s.TotalMB - s.FreeMB)).ToList(), plot, T.Other, 110);
        Area(g, visible.Select(s => Pt(s, Math.Min(s.AiMB, s.TotalMB - s.FreeMB))).ToList(), plot, Theme.Brand, 150);

        if (_hoverX >= plot.Left && _hoverX <= plot.Right)
        {
            var nearest = visible.MinBy(s => Math.Abs(Pt(s, 0).X - _hoverX))!;
            var x = Pt(nearest, 0).X;
            using var line = new Pen(T.Muted);
            g.DrawLine(line, x, plot.Top, x, plot.Bottom);
            var label = $"{nearest.Time:HH:mm}  ·  used {(nearest.TotalMB - nearest.FreeMB) / 1024.0:0.0} GB  ·  AI {nearest.AiMB / 1024.0:0.0} GB";
            var w = g.MeasureString(label, T.Small).Width + 16;
            var box = new RectangleF(Math.Min(x + 8, plot.Right - w), plot.Top, w, 24);
            using var bg = new SolidBrush(T.Surface2);
            using var path = Theme.Rounded(box, 6);
            g.FillPath(bg, path);
            DrawText(g, label, T.Small, T.Text, box, StringAlignment.Center);
        }
    }

    /// <summary>Right-aligned colour keys.</summary>
    void Legend(Graphics g, float right, float y, params (string Label, Color Color)[] items)
    {
        var x = right;
        foreach (var (label, color) in items.Reverse())
        {
            var w = g.MeasureString(label, T.Small).Width;
            x -= w;
            DrawText(g, label, T.Small, T.Muted, new RectangleF(x, y, w + 4, 18));
            x -= 16;
            using var b = new SolidBrush(color);
            g.FillEllipse(b, x, y + 4, 10, 10);
            x -= 18;
        }
    }

    static void Area(Graphics g, List<PointF> pts, RectangleF plot, Color c, int alpha)
    {
        using var path = new GraphicsPath();
        path.AddLine(pts[0].X, plot.Bottom, pts[0].X, pts[0].Y);
        path.AddLines(pts.ToArray());
        path.AddLine(pts[^1].X, pts[^1].Y, pts[^1].X, plot.Bottom);
        path.CloseFigure();
        using var fill = new LinearGradientBrush(plot, Color.FromArgb(alpha, c), Color.FromArgb(alpha / 6, c), 90f);
        g.FillPath(fill, path);
        using var pen = new Pen(c, 2f);
        g.DrawLines(pen, pts.ToArray());
    }
}

/// <summary>One row per AI app: icon, name, MCP servers, a bar and its RAM.</summary>
sealed class AppList : Painted
{
    const int RowHeight = 62;
    IReadOnlyList<AgentUsage> _agents = [];
    IReadOnlyDictionary<string, int> _caps = new Dictionary<string, int>();
    static readonly Dictionary<string, Bitmap?> Icons = [];

    public void Set(IReadOnlyList<AgentUsage> agents, IReadOnlyDictionary<string, int> caps)
    {
        _agents = agents;
        _caps = caps;
        Height = Math.Max(1, agents.Count) * RowHeight;
        Invalidate();
    }

    protected override void Draw(Graphics g)
    {
        if (_agents.Count == 0)
        {
            DrawText(g, "No AI apps running right now.", T.Body, T.Muted, ClientRectangle, StringAlignment.Center);
            return;
        }
        var max = _agents.Max(a => a.Bytes);
        for (var i = 0; i < _agents.Count; i++)
        {
            var a = _agents[i];
            var y = i * RowHeight;
            if (i > 0) using (var sep = new Pen(T.Border)) g.DrawLine(sep, 0, y, Width, y);

            var icon = IconFor(a);
            var iconRect = new Rectangle(0, y + 15, 32, 32);
            if (icon is not null) g.DrawImage(icon, iconRect);
            else
            {
                using var b = new SolidBrush(Theme.Brand);
                g.FillEllipse(b, iconRect);
                DrawText(g, a.Profile.Name[..1], T.Bold, Color.White, iconRect, StringAlignment.Center);
            }

            var textX = 46;
            var right = Width - 96;
            DrawText(g, a.Profile.Name, T.Bold, T.Text, new RectangleF(textX, y + 8, right - textX, 22));
            var helpers = string.Join("  ·  ", a.Helpers.Take(3).Select(h => h.Count > 1 ? $"{h.Key} ×{h.Count}" : h.Key));
            var sub = $"{a.Processes.Count} processes" + (helpers.Length > 0 ? $"  ·  {helpers}" : "");
            DrawText(g, sub, T.Small, T.Muted, new RectangleF(textX, y + 28, right - textX, 18));

            var bar = new RectangleF(textX, y + 48, right - textX, 5);
            using (var track = new SolidBrush(T.Surface2)) g.FillRectangle(track, bar);
            var fill = bar with { Width = Math.Max(3, bar.Width * a.Bytes / Math.Max(1, max)) };
            using (var b = new LinearGradientBrush(bar, Theme.Brand, Theme.BrandDeep, 0f)) g.FillRectangle(b, fill);

            var over = _caps.TryGetValue(a.Profile.Id, out var cap) && a.Bytes / 1048576 > cap;
            DrawText(g, Theme.Size(a.Bytes), T.Bold, over ? T.Warn : T.Text, new RectangleF(right, y + 8, 96, 22), StringAlignment.Far);
            if (cap > 0) DrawText(g, $"cap {Theme.Size((long)cap << 20)}", T.Small, over ? T.Warn : T.Muted, new RectangleF(right, y + 28, 96, 18), StringAlignment.Far);
        }
    }

    static Bitmap? IconFor(AgentUsage a)
    {
        var path = a.MainExecutable;
        if (path.Length == 0) return null;
        if (Icons.TryGetValue(path, out var cached)) return cached;
        Bitmap? bmp = null;
        try { using var ico = Icon.ExtractAssociatedIcon(path); bmp = ico?.ToBitmap(); }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException) { }
        return Icons[path] = bmp;
    }
}

/// <summary>Rounded button, filled (primary) or outlined, with an optional icon glyph.</summary>
sealed class FlatButton : Painted
{
    public bool Primary { get; set; }
    public string Glyph { get; set; } = "";

    public FlatButton(string text, bool primary = false, string glyph = "")
    {
        base.Text = text;
        Primary = primary;
        Glyph = glyph;
        Cursor = Cursors.Hand;
        Height = 38;
        Width = TextRenderer.MeasureText(text, Theme.Current.Bold).Width + (glyph.Length > 0 ? 58 : 36);
        TabStop = true;
    }

    protected override void OnTextChanged(EventArgs e)
    {
        Width = TextRenderer.MeasureText(base.Text, Theme.Current.Bold).Width + (Glyph.Length > 0 ? 58 : 36);
        base.OnTextChanged(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Enter or Keys.Space) OnClick(EventArgs.Empty);
        base.OnKeyDown(e);
    }

    protected override void Draw(Graphics g)
    {
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var path = Theme.Rounded(r, 8);
        if (Primary)
        {
            using var b = new LinearGradientBrush(r, Hover ? ControlPaint.Light(Theme.Brand, 0.15f) : Theme.Brand, Theme.BrandDeep, 0f);
            g.FillPath(b, path);
        }
        else
        {
            using var b = new SolidBrush(Hover ? T.Surface2 : T.Surface);
            using var p = new Pen(T.Border);
            g.FillPath(b, path);
            g.DrawPath(p, path);
        }
        if (Focused) using (var f = new Pen(Theme.Brand, 2)) g.DrawPath(f, path);
        var color = Primary ? Color.White : T.Text;
        var textRect = new RectangleF(0, 0, Width, Height);
        if (Glyph.Length > 0)
        {
            DrawText(g, Glyph, T.Icon, color, new RectangleF(14, 0, 22, Height));
            textRect = new RectangleF(40, 0, Width - 52, Height);
            DrawText(g, base.Text, T.Bold, color, textRect);
        }
        else DrawText(g, base.Text, T.Bold, color, textRect, StringAlignment.Center);
    }
}

/// <summary>On/off switch with a label.</summary>
sealed class ToggleSwitch : Painted
{
    bool _checked;
    public event EventHandler? CheckedChanged;
    public string Hint { get; }

    public bool Checked
    {
        get => _checked;
        set { if (_checked == value) return; _checked = value; Invalidate(); CheckedChanged?.Invoke(this, EventArgs.Empty); }
    }

    public ToggleSwitch(string text, string hint = "")
    {
        base.Text = text;
        Hint = hint;
        Height = hint.Length > 0 ? 52 : 34;
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.CheckButton;
    }

    protected override void OnClick(EventArgs e) { Checked = !Checked; base.OnClick(e); }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space) Checked = !Checked; base.OnKeyDown(e); }

    protected override void Draw(Graphics g)
    {
        var sw = new RectangleF(Width - 46, 6, 40, 22);
        using (var path = Theme.Rounded(sw, 11))
        using (var b = new SolidBrush(_checked ? Theme.Brand : T.Surface2))
        using (var p = new Pen(_checked ? Theme.Brand : T.Muted))
        {
            g.FillPath(b, path);
            g.DrawPath(p, path);
        }
        var knob = _checked ? new RectangleF(sw.Right - 19, sw.Y + 3, 16, 16) : new RectangleF(sw.X + 3, sw.Y + 3, 16, 16);
        using (var k = new SolidBrush(_checked ? Color.White : T.Muted)) g.FillEllipse(k, knob);
        if (Focused) using (var f = new Pen(Theme.Brand)) g.DrawRectangle(f, 0, 0, Width - 1, Height - 1);

        DrawText(g, base.Text, T.Body, T.Text, new RectangleF(0, 4, Width - 60, 26));
        if (Hint.Length > 0) DrawText(g, Hint, T.Small, T.Muted, new RectangleF(0, 28, Width - 60, 20));
    }
}

/// <summary>"−  6 GB  +" number picker that fits the theme (the native NumericUpDown can't be themed).</summary>
sealed class Stepper : Painted
{
    int _value;
    public int Min { get; init; }
    public int Max { get; init; } = 64;
    public int Step { get; init; } = 1;
    public Func<int, string> Format { get; init; } = v => v.ToString();
    public event EventHandler? ValueChanged;

    public int Value
    {
        get => _value;
        set { var v = Math.Clamp(value, Min, Max); if (v == _value) return; _value = v; Invalidate(); ValueChanged?.Invoke(this, EventArgs.Empty); }
    }

    public Stepper() { Size = new Size(150, 34); Cursor = Cursors.Hand; TabStop = true; }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        if (e.X < 36) Value -= Step;
        else if (e.X > Width - 36) Value += Step;
        base.OnMouseDown(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Left or Keys.Down) Value -= Step;
        if (e.KeyCode is Keys.Right or Keys.Up) Value += Step;
        base.OnKeyDown(e);
    }

    protected override bool IsInputKey(Keys k) => k is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(k);

    protected override void Draw(Graphics g)
    {
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var path = Theme.Rounded(r, 8);
        using var b = new SolidBrush(T.Surface2);
        using var p = new Pen(Focused ? Theme.Brand : T.Border);
        g.FillPath(b, path);
        g.DrawPath(p, path);
        DrawText(g, "−", T.Title, _value > Min ? T.Text : T.Muted, new RectangleF(0, 0, 36, Height), StringAlignment.Center);
        DrawText(g, "+", T.Title, _value < Max ? T.Text : T.Muted, new RectangleF(Width - 36, 0, 36, Height), StringAlignment.Center);
        DrawText(g, Format(_value), T.Bold, T.Text, new RectangleF(36, 0, Width - 72, Height), StringAlignment.Center);
    }
}

/// <summary>Small number tile: label, big value, hint.</summary>
sealed class StatTile : Painted
{
    string _label = "", _value = "", _hint = "";
    Color? _accent;

    public StatTile() { Size = new Size(200, 86); }

    public void Set(string label, string value, string hint, Color? accent = null)
    {
        (_label, _value, _hint, _accent) = (label, value, hint, accent);
        Invalidate();
    }

    protected override void Draw(Graphics g)
    {
        DrawText(g, _label, T.Small, T.Muted, new RectangleF(0, 0, Width, 20));
        DrawText(g, _value, T.Title, _accent ?? T.Text, new RectangleF(0, 20, Width, 34));
        DrawText(g, _hint, T.Small, T.Muted, new RectangleF(0, 56, Width, 20));
    }
}
