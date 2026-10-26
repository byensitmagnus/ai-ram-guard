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
        var configs = KnownConfigs.Read();
        state.Fixed = Fixer.Applied(configs);
        state.Advice = Advisor.Advise(inspected.Report, configs, state.Fixed, state.Settings.Guard.AgentCapsMB);
        state.LastRun = DateTime.Now;

        using var form = new MainForm(state) { StartPosition = FormStartPosition.Manual, Location = new Point(-5000, -5000), ShowInTaskbar = false, Size = new Size(1180, 860) };
        form.Show();
        foreach (var page in new[] { "Overview", "Fixes", "Disk", "Settings", "Activity" })
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

    /// <summary>PrintWindow, not DrawToBitmap: DrawToBitmap paints siblings in reverse z-order and hid a header panel covering a card.</summary>
    static void Save(Form f, string path)
    {
        // PW_RENDERFULLCONTENT reads DWM's copy: paint everything, then wait for DWM to take it, or the shot is a frame old.
        f.Refresh();
        DwmFlush();
        using var bmp = new Bitmap(f.ClientSize.Width, f.ClientSize.Height);
        using (var g = Graphics.FromImage(bmp))
        {
            var hdc = g.GetHdc();
            try { PrintWindow(f.Handle, hdc, 0x1 | 0x2); } // PW_CLIENTONLY | PW_RENDERFULLCONTENT
            finally { g.ReleaseHdc(hdc); }
        }
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")] static extern int DwmFlush();
}
