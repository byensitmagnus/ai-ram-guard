using AiRamGuard.Core;

namespace AiRamGuard.Tray;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        // The UI is English; keep numbers and dates in the same language as the words around them.
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.CurrentUICulture = new("en-US");

        if (args is ["--screenshot", var dir]) return Screenshots.Render(dir);
        if (args is ["--uninstall"]) { Installer.ConfirmUninstall(); return 0; }
        if (args is ["--install"]) { Installer.Install(); return 0; } // silent install for scripts and package managers

        using var single = new Mutex(true, @"Local\AiRamGuard.Tray", out var first);
        var settings = AppSettings.Load();
        if (!first && (Installer.IsInstalled || settings.Portable))
        {
            Signals.Send(Signals.Show); // already running: bring its window up instead
            return 0;
        }

        // Started from a download (new install, or a newer version while the installed one runs).
        if (!Installer.IsInstalled && (!settings.Portable || !first) && !args.Contains("--background"))
        {
            using var welcome = new WelcomeForm();
            welcome.ShowDialog();
            if (!first && !welcome.InstallChosen)
            {
                Signals.Send(Signals.Show);
                return 0;
            }
            if (welcome.InstallChosen)
            {
                if (first) single.ReleaseMutex();
                try
                {
                    Installer.Install();
                    return 0;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
                {
                    MessageBox.Show($"Install failed: {e.Message}\n\nAI RAM Guard will run from this folder instead.", "AI RAM Guard");
                    Log.Write($"install failed: {e.Message}");
                    if (!single.WaitOne(0)) return 1;
                }
            }
            settings.Portable = !welcome.InstallChosen;
            settings.Save();
        }

        Application.Run(new TrayContext(showWindow: !args.Contains("--background")));
        return 0;
    }
}
