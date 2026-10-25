using AiRamGuard.Core;

namespace AiRamGuard.Tray;

/// <summary>
/// Docs helper: renders every page off-screen from a live, read-only scan (nothing is closed or trimmed),
/// so README screenshots always show the real app.
/// </summary>
static class Screenshots
{
    public static int Render(string dir)
    {
        Directory.CreateDirectory(dir);
        var state = new AppState();
        var inspected = Guard.Inspect(state.Settings.Guard, state.Profiles);
        state.Report = inspected.Report;
        state.Advice = Advisor.Advise(inspected.Report, KnownConfigs.Read());
        state.LastRun = DateTime.Now;

        using var form = new MainForm(state) { StartPosition = FormStartPosition.Manual, Location = new Point(-5000, -5000), ShowInTaskbar = false, Size = new Size(1180, 860) };
        form.Show();
        foreach (var page in new[] { "Overview", "Recommendations", "Disk", "Settings", "Activity" })
        {
            form.ShowPage(page);
            Application.DoEvents();
            for (var i = 0; i < 1200 && form.DiskBusy; i++) { Thread.Sleep(50); Application.DoEvents(); } // the disk walk runs in the background
            Save(form, Path.Combine(dir, $"screenshot-{page.ToLowerInvariant()}.png"));
        }

        using var welcome = new WelcomeForm { StartPosition = FormStartPosition.Manual, Location = new Point(-5000, -5000), ShowInTaskbar = false };
        welcome.Show();
        Application.DoEvents();
        Save(welcome, Path.Combine(dir, "screenshot-welcome.png"));
        return 0;
    }

    static void Save(Form f, string path)
    {
        using var bmp = new Bitmap(f.ClientSize.Width, f.ClientSize.Height);
        f.DrawToBitmap(bmp, new Rectangle(Point.Empty, f.ClientSize));
        // DrawToBitmap includes the frame; crop to the client area.
        var offset = f.PointToScreen(Point.Empty);
        var frame = new Point(offset.X - f.Left, offset.Y - f.Top);
        using var full = new Bitmap(f.Width, f.Height);
        f.DrawToBitmap(full, new Rectangle(Point.Empty, f.Size));
        using var g = Graphics.FromImage(bmp);
        g.DrawImage(full, new Rectangle(Point.Empty, f.ClientSize), new Rectangle(frame, f.ClientSize), GraphicsUnit.Pixel);
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }
}
