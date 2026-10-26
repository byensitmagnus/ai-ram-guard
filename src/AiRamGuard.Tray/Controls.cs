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

    /// <summary>GDI text (TextRenderer): ClearType, and stays sharp when Windows scales the app for high DPI.</summary>
    protected static void DrawText(Graphics g, string s, Font f, Color c, RectangleF r, StringAlignment h = StringAlignment.Near, StringAlignment v = StringAlignment.Center)
    {
        var flags = TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding
            | h switch { StringAlignment.Center => TextFormatFlags.HorizontalCenter, StringAlignment.Far => TextFormatFlags.Right, _ => TextFormatFlags.Left }
            | v switch { StringAlignment.Near => TextFormatFlags.Top, StringAlignment.Far => TextFormatFlags.Bottom, _ => TextFormatFlags.VerticalCenter };
        TextRenderer.DrawText(g, s, f, Rectangle.Round(r), c, flags);
    }

    protected static int TextWidth(string s, Font f) => TextRenderer.MeasureText(s, f, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;

    /// <summary>Small rounded label: "×48", "saves ~1.0 GB".</summary>
    protected static float DrawChip(Graphics g, string s, Font f, Color fg, Color bg, float x, float y, float h = 20)
    {
        var w = TextWidth(s, f) + 16;
        var r = new RectangleF(x, y, w, h);
        using (var path = Theme.Rounded(r, h / 2))
        using (var b = new SolidBrush(bg)) g.FillPath(b, path);
        DrawText(g, s, f, fg, r, StringAlignment.Center);
        return w;
    }
}

/// <summary>A chip as a control, for pages built from controls.</summary>
sealed class Pill : Painted
{
    readonly Color _fg, _bg;
    public Pill(string text, Color fg, Color bg)
    {
        base.Text = text;
        (_fg, _bg) = (fg, bg);
        Size = new Size(TextWidth(text, Theme.Current.Bold) + 20, 26);
    }

    protected override void Draw(Graphics g) => DrawChip(g, base.Text, T.Bold, _fg, _bg, 0, 0, Height - 1);
}

/// <summary>A rounded surface that holds other controls, with thin lines between its rows.</summary>
sealed class Card : Panel
{
    /// <summary>Where to draw a divider (y), set by the page's layout.</summary>
    public int[] Dividers { get; set; } = [];

    public Card()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
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
        e.Graphics.SmoothingMode = SmoothingMode.None;
        foreach (var y in Dividers) e.Graphics.DrawLine(pen, 24, y, Width - 25, y);
    }
}

/// <summary>Text drawn without GDI's side padding, so it lines up with everything else that is painted. Grows to fit when it wraps.</summary>
sealed class TextBlock : Painted
{
    public bool Wrap { get; init; }
    public bool AlignRight { get; init; }

    public TextBlock(string text, Font font, Color color, Color back)
    {
        Font = font;
        ForeColor = color;
        BackColor = back;
        base.Text = text;
        Height = font.Height + 2;
    }

    TextFormatFlags Flags => TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix
        | (Wrap ? TextFormatFlags.WordBreak : TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis)
        | (AlignRight ? TextFormatFlags.Right : TextFormatFlags.Left);

    void Fit() { if (Wrap) Height = Math.Max(Font.Height, TextRenderer.MeasureText(base.Text, Font, new Size(Math.Max(1, Width), 0), Flags).Height); }

    protected override void OnTextChanged(EventArgs e) { Fit(); Invalidate(); base.OnTextChanged(e); }
    protected override void OnResize(EventArgs e) { Fit(); base.OnResize(e); }

    protected override void Draw(Graphics g) => TextRenderer.DrawText(g, base.Text, Font, ClientRectangle, ForeColor, Flags);
}

/// <summary>Page title and one line under it, lined up with the cards' left edge.</summary>
sealed class PageHeader : Painted
{
    string _title = "", _sub = "";
    public PageHeader() { Height = 64; }
    public void Set(string title, string sub) { (_title, _sub) = (title, sub); Invalidate(); }

    protected override void Draw(Graphics g)
    {
        DrawText(g, _title, T.Heading, T.Text, new RectangleF(0, 0, Width, 36), v: StringAlignment.Near);
        DrawText(g, _sub, T.Body, T.Muted, new RectangleF(0, 42, Width, 20), v: StringAlignment.Near);
    }
}

/// <summary>An icon in a soft circle of its own colour: the marker at the left of a fix.</summary>
sealed class IconCircle : Painted
{
    readonly string _glyph;
    readonly Color _color;
    public IconCircle(string glyph, Color color) { (_glyph, _color) = (glyph, color); Size = new Size(40, 40); }

    protected override void Draw(Graphics g)
    {
        using (var b = new SolidBrush(Color.FromArgb(T.Dark ? 40 : 28, _color))) g.FillEllipse(b, 0, 0, Width - 1, Height - 1);
        DrawText(g, _glyph, T.Icon, _color, new RectangleF(0, 0, Width, Height), StringAlignment.Center);
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

        // The story is what AI takes, so that is the hero number; free RAM is the first tile next to it.
        var center = new RectangleF(r.X, r.Y + r.Height * 0.26f, r.Width, r.Height * 0.3f);
        if (_total <= 1)
        {
            DrawText(g, "Scanning…", T.Body, T.Muted, r, StringAlignment.Center);
            return;
        }
        DrawText(g, Theme.Size(_ai), T.Big, T.BrandText, center, StringAlignment.Center);
        DrawText(g, $"AI apps · {(double)_ai / _total:P0} of RAM", T.Small, T.Muted, new RectangleF(r.X, center.Bottom, r.Width, 22), StringAlignment.Center);
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
        DrawText(g, hours == 1 ? "Last hour" : $"Last {hours} hours", T.Section, T.Text, new RectangleF(4, 0, 300, 30));
        Legend(g, Width - 8, 6, ("AI apps", Theme.Brand), ("all RAM in use", T.Other));
        using var grid = new Pen(T.Border) { DashStyle = DashStyle.Dot };

        // Round gridlines (0 / 8 / 16 / 24 / 32 GB), not quarters of 31.8.
        var installedGB = Math.Max(1, (int)Math.Ceiling((_samples.Count > 0 ? _samples.Max(s => s.TotalMB) : 32768) / 1024.0));
        var stepGB = installedGB <= 8 ? 2 : installedGB <= 16 ? 4 : installedGB <= 32 ? 8 : installedGB <= 64 ? 16 : 32;
        var topGB = (installedGB + stepGB - 1) / stepGB * stepGB;
        var totalMB = topGB * 1024;
        for (var gb = 0; gb <= topGB; gb += stepGB)
        {
            var y = plot.Bottom - plot.Height * gb / topGB;
            g.DrawLine(grid, plot.Left, y, plot.Right, y);
            DrawText(g, $"{gb} GB", T.Small, T.Muted, new RectangleF(0, y - 9, 38, 18), StringAlignment.Far);
        }

        // Clock times on round boundaries, like Task Manager and every other monitor.
        var start = now - window;
        var tick = TimeSpan.FromMinutes(hours switch { 1 => 15, 3 => 30, 6 => 60, 12 => 120, _ => 240 });
        var nowLabel = new RectangleF(plot.Right - 60, plot.Bottom + 6, 62, 18);
        DrawText(g, "now", T.Small, T.Muted, nowLabel, StringAlignment.Far);
        for (var t = new DateTime(start.Ticks / tick.Ticks * tick.Ticks + tick.Ticks); t < now; t += tick)
        {
            var x = plot.Left + plot.Width * (float)((t - start).TotalSeconds / window.TotalSeconds);
            if (x + 24 > nowLabel.Left + 30) continue; // would collide with "now"
            DrawText(g, $"{t:HH:mm}", T.Small, T.Muted, new RectangleF(x - 30, plot.Bottom + 6, 60, 18), StringAlignment.Center);
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
            var w = TextWidth(label, T.Small) + 20;
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
        foreach (var (label, color) in Enumerable.Reverse(items))
        {
            var w = TextWidth(label, T.Small);
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
    const int RowHeight = 68;
    IReadOnlyList<AgentUsage> _agents = [];
    IReadOnlyDictionary<string, int> _caps = new Dictionary<string, int>();
    static readonly Dictionary<string, Bitmap?> Icons = [];

    long _totalRam = 1;

    public void Set(IReadOnlyList<AgentUsage> agents, IReadOnlyDictionary<string, int> caps, long totalRam)
    {
        _agents = agents;
        _caps = caps;
        _totalRam = Math.Max(1, totalRam);
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
        for (var i = 0; i < _agents.Count; i++)
        {
            var a = _agents[i];
            var y = i * RowHeight;
            if (i > 0) using (var sep = new Pen(T.Border)) g.DrawLine(sep, 0, y, Width, y);

            var icon = IconFor(a);
            var iconRect = new Rectangle(0, y + 18, 32, 32);
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
            // The insight is the duplicates: "desktop-commander ×48" as an orange chip, not muted small print.
            var procs = $"{a.Processes.Count} processes";
            DrawText(g, procs, T.Small, T.Muted, new RectangleF(textX, y + 28, 120, 18));
            var chipX = textX + TextWidth(procs, T.Small) + 10f;
            foreach (var h in a.Helpers.Take(3))
            {
                var dup = h.Count >= 3;
                var label = $"{h.Key.Split('/')[^1]}{(h.Count > 1 ? $" ×{h.Count}" : "")}";
                if (chipX + TextWidth(label, T.Small) + 16 > right) break;
                chipX += DrawChip(g, label, T.Small, dup ? T.Warn : T.Muted, dup ? Color.FromArgb(T.Dark ? 45 : 35, T.Warn) : T.Surface2, chipX, y + 27, 20) + 6;
            }

            // Share of installed RAM, so one app alone isn't always a full bar. A tick marks its cap.
            var bar = new RectangleF(textX, y + 52, right - textX, 5);
            using (var track = Theme.Rounded(bar, 2.5f))
            using (var tb = new SolidBrush(T.Surface2)) g.FillPath(tb, track);
            var fill = bar with { Width = Math.Max(5, bar.Width * a.Bytes / _totalRam) };
            using (var fp = Theme.Rounded(fill, 2.5f))
            using (var b = new LinearGradientBrush(bar, Theme.Brand, Theme.BrandDeep, 0f)) g.FillPath(b, fp);

            var over = _caps.TryGetValue(a.Profile.Id, out var cap) && a.Bytes / 1048576 > cap;
            if (cap > 0)
            {
                var tx = bar.X + bar.Width * Math.Min(1f, ((long)cap << 20) / (float)_totalRam);
                using var tp = new Pen(over ? T.Warn : T.Text, 2);
                g.DrawLine(tp, tx, bar.Y - 3, tx, bar.Bottom + 3);
            }
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
        AccessibleRole = AccessibleRole.PushButton;
        AccessibleName = text;
    }

    protected override void OnTextChanged(EventArgs e)
    {
        Width = TextRenderer.MeasureText(base.Text, Theme.Current.Bold).Width + (Glyph.Length > 0 ? 58 : 36);
        AccessibleName = base.Text;
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
        set
        {
            if (_checked == value) return;
            _checked = value;
            AccessibleDescription = value ? "On" : "Off";
            if (IsHandleCreated && Visible) _anim.Start(); else _knob = value ? 1 : 0;
            Invalidate();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // Knob position 0..1, eased over ~150 ms so the switch slides instead of jumping.
    float _knob;
    readonly System.Windows.Forms.Timer _anim = new() { Interval = 15 };

    public ToggleSwitch(string text, string hint = "")
    {
        _anim.Tick += (_, _) =>
        {
            var target = _checked ? 1f : 0f;
            _knob += (target - _knob) * 0.35f;
            if (Math.Abs(target - _knob) < 0.02f) { _knob = target; _anim.Stop(); }
            Invalidate();
        };
        base.Text = text;
        AccessibleName = text;
        Hint = hint;
        Height = hint.Length > 0 ? 52 : 34;
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.CheckButton;
    }

    static Color Blend(Color a, Color b, float t) =>
        Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    protected override void Dispose(bool disposing) { if (disposing) _anim.Dispose(); base.Dispose(disposing); }

    protected override void OnClick(EventArgs e) { Checked = !Checked; base.OnClick(e); }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space) Checked = !Checked; base.OnKeyDown(e); }

    protected override void Draw(Graphics g)
    {
        var sw = new RectangleF(Width - 46, 6, 40, 22);
        var fill = Blend(T.Surface2, Theme.Brand, _knob);
        using (var path = Theme.Rounded(sw, 11))
        using (var b = new SolidBrush(fill))
        using (var p = new Pen(Blend(T.Muted, Theme.Brand, _knob)))
        {
            g.FillPath(b, path);
            g.DrawPath(p, path);
        }
        var size = 14 + 2 * _knob;
        var knob = new RectangleF(sw.X + 3 + (sw.Width - 6 - size) * _knob, sw.Y + (sw.Height - size) / 2, size, size);
        using (var k = new SolidBrush(Blend(T.Muted, Color.White, _knob))) g.FillEllipse(k, knob);
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
        set { var v = Math.Clamp(value, Min, Max); if (v == _value) return; _value = v; AccessibleDescription = Format(v); Invalidate(); ValueChanged?.Invoke(this, EventArgs.Empty); }
    }

    public Stepper() { Size = new Size(150, 34); Cursor = Cursors.Hand; TabStop = true; AccessibleRole = AccessibleRole.SpinButton; }

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

/// <summary>A number with what it means under it: the number is the point, the words explain it.</summary>
sealed class StatTile : Painted
{
    string _value = "", _label = "";
    Color? _accent;

    public StatTile() { Size = new Size(200, 58); }

    public void Set(string value, string label, Color? accent = null)
    {
        (_value, _label, _accent) = (value, label, accent);
        AccessibleName = $"{value} {label}";
        Invalidate();
    }

    protected override void Draw(Graphics g)
    {
        // A thin bar in the number's colour, like PC Manager's metric tiles: it ties the number to its meaning.
        using (var bar = new SolidBrush(_accent ?? T.Other)) g.FillRectangle(bar, 0, 4, 3, Height - 8);
        DrawText(g, _value, T.Metric, _accent ?? T.Text, new RectangleF(14, 0, Width - 14, 32), v: StringAlignment.Near);
        DrawText(g, _label, T.Small, T.Muted, new RectangleF(14, 36, Width - 14, 20), v: StringAlignment.Near);
    }
}


/// <summary>One entry on the Activity page, already in plain words.</summary>
/// <param name="Group">Back-to-back entries of one group ("trim", "priority") show as one row: Rounds counts them, Since is the oldest.</param>
sealed record ActivityEntry(DateTime Time, string Glyph, Color Color, string Title, string Detail, long Freed = 0, string? Group = null, int Rounds = 1, DateTime Since = default);

/// <summary>The guard's log as a readable timeline: grouped by day, an icon per kind, newest first.</summary>
sealed class ActivityList : Painted
{
    const int Row = 52, DayHeader = 40;
    IReadOnlyList<ActivityEntry> _entries = [];

    public void Set(IReadOnlyList<ActivityEntry> entries)
    {
        _entries = entries;
        Height = Math.Max(60, entries.Count * Row + entries.Select(e => e.Time.Date).Distinct().Count() * DayHeader);
        Invalidate();
    }

    protected override void Draw(Graphics g)
    {
        if (_entries.Count == 0)
        {
            DrawText(g, "Nothing yet. When AI RAM Guard closes a leftover or frees memory, it shows up here.", T.Body, T.Muted, ClientRectangle, StringAlignment.Center);
            return;
        }
        var y = 0f;
        DateTime? day = null;
        foreach (var e in _entries)
        {
            if (day != e.Time.Date)
            {
                day = e.Time.Date;
                var label = day == DateTime.Today ? "Today" : day == DateTime.Today.AddDays(-1) ? "Yesterday" : $"{day:dddd d MMMM}";
                DrawText(g, label, T.Bold, T.Text, new RectangleF(0, y + 8, Width, 26));
                y += DayHeader;
            }
            var circle = new RectangleF(0, y + 8, 34, 34);
            using (var b = new SolidBrush(Color.FromArgb(T.Dark ? 40 : 28, e.Color))) g.FillEllipse(b, circle);
            DrawText(g, e.Glyph, T.Icon, e.Color, circle, StringAlignment.Center);
            DrawText(g, e.Title, T.Bold, T.Text, new RectangleF(48, y + 6, Width - 120, 22));
            DrawText(g, e.Detail, T.Small, T.Muted, new RectangleF(48, y + 27, Width - 120, 18));
            DrawText(g, $"{e.Time:HH:mm}", T.Small, T.Muted, new RectangleF(Width - 70, y + 6, 70, 22), StringAlignment.Far);
            y += Row;
        }
    }
}