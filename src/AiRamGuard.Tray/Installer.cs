using System.Diagnostics;
using AiRamGuard.Core;
using Microsoft.Win32;

namespace AiRamGuard.Tray;

/// <summary>
/// Per-user install with no admin rights and no installer framework: copy to %LOCALAPPDATA%\Programs,
/// Start menu shortcut, autostart, and an entry in Settings → Apps so it uninstalls like any other app.
/// </summary>
static class Installer
{
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\AiRamGuard";
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "AiRamGuard");
    static readonly string Shortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "AI RAM Guard.lnk");
    static string InstalledExe => Path.Combine(Dir, "AiRamGuard.exe");

    public static bool IsInstalled => string.Equals(Path.GetDirectoryName(Environment.ProcessPath), Dir, StringComparison.OrdinalIgnoreCase);

    /// <summary>Copies this exe (and airg.exe next to it) into place, then starts the installed copy.</summary>
    public static void Install()
    {
        // An older installed copy may be running: ask it to exit so its files can be replaced.
        if (Signals.Send(Signals.Exit))
            foreach (var p in Process.GetProcessesByName("AiRamGuard").Where(p => p.Id != Environment.ProcessId))
                p.WaitForExit(5000);

        Directory.CreateDirectory(Dir);
        var src = Path.GetDirectoryName(Environment.ProcessPath)!;
        if (!string.Equals(Path.GetFullPath(src), Path.GetFullPath(Dir), StringComparison.OrdinalIgnoreCase))
            foreach (var name in new[] { "AiRamGuard.exe", "airg.exe" })
                if (File.Exists(Path.Combine(src, name))) File.Copy(Path.Combine(src, name), Path.Combine(Dir, name), overwrite: true);

        CreateShortcut(Shortcut, InstalledExe);
        using (var run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            run.SetValue("AI RAM Guard", $"\"{InstalledExe}\" --background");
        using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            k.SetValue("DisplayName", "AI RAM Guard");
            k.SetValue("DisplayVersion", MainForm.Version);
            k.SetValue("Publisher", "Byens IT (Magnus Steinmeier Olsen)");
            k.SetValue("DisplayIcon", InstalledExe);
            k.SetValue("InstallLocation", Dir);
            k.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
            k.SetValue("URLInfoAbout", MainForm.RepoUrl);
            k.SetValue("HelpLink", MainForm.RepoUrl);
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            k.SetValue("EstimatedSize", (int)(new DirectoryInfo(Dir).EnumerateFiles().Sum(f => f.Length) / 1024), RegistryValueKind.DWord);
        }
        Process.Start(new ProcessStartInfo(InstalledExe, "--background") { UseShellExecute = true });
        Log.Write($"installed {MainForm.Version} to %LOCALAPPDATA%\\Programs\\AiRamGuard");
    }

    public static void ConfirmUninstall()
    {
        if (MessageBox.Show("Remove AI RAM Guard from this PC?\n\nYour settings and history stay in your user folder.", "AI RAM Guard",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        Uninstall();
        Application.Exit();
    }

    /// <summary>Removes shortcut, autostart and the Apps entry, then deletes the folder once this process has exited.</summary>
    public static void Uninstall()
    {
        Signals.Send(Signals.Exit);
        Autostart.Enabled = false;
        File.Delete(Shortcut);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        // The running exe can't delete itself: a hidden cmd waits a moment, then removes the folder.
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 3 /nobreak >nul & rmdir /s /q \"{Dir}\"")
        {
            CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath(),
        });
    }

    static void CreateShortcut(string lnk, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lnk)!);
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new PlatformNotSupportedException("WScript.Shell missing");
        dynamic shell = Activator.CreateInstance(shellType)!;
        var s = shell.CreateShortcut(lnk);
        s.TargetPath = target;
        s.WorkingDirectory = Path.GetDirectoryName(target);
        s.Description = "Stop AI agents and MCP servers from eating your RAM";
        s.IconLocation = target + ",0";
        s.Save();
        System.Runtime.InteropServices.Marshal.FinalReleaseComObject(s);
        System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
    }
}

/// <summary>First start from a download: install properly, or just run.</summary>
sealed class WelcomeForm : Form
{
    public bool InstallChosen { get; private set; }

    public WelcomeForm()
    {
        var t = Theme.Detect();
        Text = "AI RAM Guard";
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        ClientSize = new Size(560, 340);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = t.Bg;
        Font = t.Body;
        HandleCreated += (_, _) => t.ApplyTitleBar(this);

        var logo = new PictureBox { Image = MainForm.Logo(64), SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(64, 64), Location = new Point(36, 36) };
        var title = new Label { Text = "AI RAM Guard", Font = new Font("Segoe UI Semibold", 20f), ForeColor = t.Text, AutoSize = true, Location = new Point(116, 36) };
        var sub = new Label { Text = $"Version {MainForm.Version} · by Byens IT", ForeColor = t.Muted, AutoSize = true, Location = new Point(120, 76) };
        var body = new Label
        {
            Text = "Keeps Claude Code, Codex, Cursor and their MCP servers from eating your RAM.\n\n"
                 + "Install adds a Start menu shortcut, starts with Windows and can be removed from Settings → Apps. No admin rights needed.",
            ForeColor = t.Text, Location = new Point(36, 128), Size = new Size(490, 100),
        };
        var install = new FlatButton("Install", primary: true, glyph: "") { Location = new Point(36, 260), BackColor = t.Bg };
        var run = new FlatButton("Just run it") { Location = new Point(install.Right + 12, 260), BackColor = t.Bg };
        install.Click += (_, _) => { InstallChosen = true; DialogResult = DialogResult.OK; };
        run.Click += (_, _) => { DialogResult = DialogResult.Cancel; };
        Controls.AddRange([logo, title, sub, body, install, run]);
    }
}
