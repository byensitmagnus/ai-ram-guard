using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AiRamGuard.Tray;

/// <summary>Colours and fonts. Follows the Windows light/dark setting.</summary>
sealed class Theme
{
    public static readonly Color Brand = ColorTranslator.FromHtml("#ba01c4");
    public static readonly Color BrandDeep = ColorTranslator.FromHtml("#5b0aa8");

    public bool Dark { get; }
    public Color Bg { get; }
    public Color Surface { get; }
    public Color Surface2 { get; }
    public Color Border { get; }
    public Color Text { get; }
    public Color Muted { get; }
    public Color Good { get; } = ColorTranslator.FromHtml("#2fb380");
    public Color Warn { get; } = ColorTranslator.FromHtml("#f08a3e");
    public Color Other { get; }

    public static readonly string IconFont = FontFamily.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
    public Font Body { get; } = new("Segoe UI", 9.75f);
    public Font Small { get; } = new("Segoe UI", 8.75f);
    public Font Bold { get; } = new("Segoe UI Semibold", 10f);
    public Font Title { get; } = new("Segoe UI Semibold", 15f);
    public Font Big { get; } = new("Segoe UI Semibold", 22f);
    public Font Icon { get; } = new(IconFont, 12f);

    Theme(bool dark)
    {
        Dark = dark;
        Bg = dark ? ColorTranslator.FromHtml("#121117") : ColorTranslator.FromHtml("#f5f3f8");
        Surface = dark ? ColorTranslator.FromHtml("#1c1a23") : Color.White;
        Surface2 = dark ? ColorTranslator.FromHtml("#26232f") : ColorTranslator.FromHtml("#efecf3");
        Border = dark ? ColorTranslator.FromHtml("#2f2b3a") : ColorTranslator.FromHtml("#e2dee8");
        Text = dark ? ColorTranslator.FromHtml("#f3effa") : ColorTranslator.FromHtml("#1b1a21");
        Muted = dark ? ColorTranslator.FromHtml("#a49fb2") : ColorTranslator.FromHtml("#6b6677");
        Other = dark ? ColorTranslator.FromHtml("#4a4558") : ColorTranslator.FromHtml("#c9c3d3");
    }

    public static Theme Current { get; private set; } = Detect();

    public static Theme Detect()
    {
        var light = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")?.GetValue("AppsUseLightTheme");
        return Current = new Theme(light is not int v || v == 0);
    }

    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Dark title bar and Windows 11 Mica-less rounded window to match the theme.</summary>
    public void ApplyTitleBar(Form f)
    {
        var on = Dark ? 1 : 0;
        DwmSetWindowAttribute(f.Handle, 20, ref on, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
        var caption = ColorTranslator.ToWin32(Bg);
        DwmSetWindowAttribute(f.Handle, 35, ref caption, sizeof(int)); // DWMWA_CAPTION_COLOR (Windows 11)
    }

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr hwnd, string app, string? idList);

    /// <summary>Dark scrollbars on native controls (a white bar in a dark window looks broken).</summary>
    public void ApplyScrollbars(Control c)
    {
        if (Dark) c.HandleCreated += (_, _) => SetWindowTheme(c.Handle, "DarkMode_Explorer", null);
    }

    public static string Size(long bytes) => bytes >= 1L << 30
        ? $"{bytes / 1073741824.0:0.0} GB"
        : $"{bytes / 1048576.0:0} MB";
}

/// <summary>Tray menu in the app's colours.</summary>
sealed class MenuRenderer(Theme t) : ToolStripProfessionalRenderer(new MenuColors(t))
{
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? t.Text : t.Muted;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = t.Muted;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(e.ImageRectangle.X, e.ImageRectangle.Y, 16, 16);
        using var b = new SolidBrush(Theme.Brand);
        using var path = Theme.Rounded(r, 4);
        g.FillPath(b, path);
        using var pen = new Pen(Color.White, 2);
        g.DrawLines(pen, new Point[] {new Point(r.X + 4, r.Y + 8), new Point(r.X + 7, r.Y + 11), new Point(r.X + 12, r.Y + 5)});
    }

    sealed class MenuColors(Theme t) : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => t.Surface;
        public override Color ImageMarginGradientBegin => t.Surface;
        public override Color ImageMarginGradientMiddle => t.Surface;
        public override Color ImageMarginGradientEnd => t.Surface;
        public override Color MenuBorder => t.Border;
        public override Color MenuItemBorder => t.Surface2;
        public override Color MenuItemSelected => t.Surface2;
        public override Color MenuItemSelectedGradientBegin => t.Surface2;
        public override Color MenuItemSelectedGradientEnd => t.Surface2;
        public override Color SeparatorDark => t.Border;
        public override Color SeparatorLight => t.Border;
        public override Color CheckBackground => t.Surface2;
        public override Color CheckSelectedBackground => t.Surface2;
    }
}
