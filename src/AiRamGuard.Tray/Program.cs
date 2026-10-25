namespace AiRamGuard.Tray;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args is ["--screenshot", var path])
        {
            // Docs helper: render the report window off-screen to a PNG.
            ApplicationConfiguration.Initialize();
            using var form = new ReportForm(() => { }) { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), ShowInTaskbar = false };
            form.Show();
            form.Render(Core.Analyzer.Analyze(Core.SnapshotReader.Read(), Core.ProfileSet.Load()));
            Application.DoEvents();
            using var bmp = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bmp, new Rectangle(Point.Empty, form.Size));
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            return;
        }
        using var single = new Mutex(true, @"Local\AiRamGuard.Tray", out var first);
        if (!first) return;
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext());
    }
}
