using System.Text.Json;
using AiRamGuard.Core;
using Microsoft.Win32;

namespace AiRamGuard.Tray;

sealed class AppSettings
{
    public GuardSettings Guard { get; set; } = new();
    public bool AutoGuard { get; set; } = true;
    public bool Notify { get; set; } = true;
    /// <summary>The user chose "run without installing"; don't ask again.</summary>
    public bool Portable { get; set; }

    static string File_ => Path.Combine(Paths.ConfigDir, "settings.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try { return File.Exists(File_) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(File_)) ?? new() : new(); }
        catch (JsonException) { return new(); } // a broken file falls back to defaults instead of crashing at login
    }

    public void Save()
    {
        Directory.CreateDirectory(Paths.ConfigDir);
        File.WriteAllText(File_, JsonSerializer.Serialize(this, Json));
    }
}

static class Autostart
{
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run", Name = "AI RAM Guard";

    public static bool Enabled
    {
        get => Registry.CurrentUser.OpenSubKey(Key)?.GetValue(Name) is string;
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(Key);
            if (value) k.SetValue(Name, $"\"{Environment.ProcessPath}\" --background");
            else k.DeleteValue(Name, false);
        }
    }
}
