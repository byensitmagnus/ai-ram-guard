using System.Diagnostics;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace AiRamGuard.Core;

/// <summary>A folder AI tools write to, and the environment variable(s) that point the tool somewhere else.</summary>
/// <param name="Defaults">Where the tool writes when the variable is not set. The first one that exists wins.</param>
/// <param name="Movable">False for folders that hold sessions and settings, not just downloads: the app has to be closed to move those.</param>
/// <param name="CopyContents">False for temp: its files are short-lived and often open, so only new files go to the new place.</param>
public sealed record StorageSpot(string Id, string Name, string[] EnvVars, string[] Defaults, bool Movable = true, bool CopyContents = true);

/// <param name="Path">Where the tool writes now.</param>
/// <param name="RealPath">The same, after following a junction or symbolic link. Its drive is where the bytes really go.</param>
public sealed record SpotUsage(StorageSpot Spot, string Path, string RealPath, bool Exists, long Bytes, bool OnSystemDrive)
{
    public string Drive => System.IO.Path.GetPathRoot(RealPath) ?? "";
}

public sealed record EnvBackup(string Name, string? Value, bool Expand);

/// <summary>One move, with everything needed to undo it.</summary>
public sealed record MoveRecord(string Id, string From, string To, List<EnvBackup> Env, DateTime At);

/// <param name="LeftBytes">Files that were in use and stayed in the old folder. Moving again after closing the app picks them up.</param>
public sealed record MoveResult(bool Ok, string Message, long MovedBytes = 0, long LeftBytes = 0);

public static class Storage
{
    static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    static string Roaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    /// <summary>Locations and variables from each tool's own docs (npm, pip, uv, NuGet, Playwright, Hugging Face, Ollama, Claude Code, Codex).</summary>
    public static IReadOnlyList<StorageSpot> Known { get; } =
    [
        new("npm", "npm cache (npx MCP servers)", ["npm_config_cache"], [Path.Combine(Local, "npm-cache"), Path.Combine(Roaming, "npm-cache")]),
        new("uv", "uv cache (uvx MCP servers)", ["UV_CACHE_DIR"], [Path.Combine(Local, "uv", "cache")]),
        new("pip", "pip cache", ["PIP_CACHE_DIR"], [Path.Combine(Local, "pip", "Cache")]),
        new("nuget", "NuGet packages", ["NUGET_PACKAGES"], [Path.Combine(Home, ".nuget", "packages")]),
        new("playwright", "Playwright browsers", ["PLAYWRIGHT_BROWSERS_PATH"], [Path.Combine(Local, "ms-playwright")]),
        new("huggingface", "Hugging Face models", ["HF_HOME"], [Path.Combine(Home, ".cache", "huggingface")]),
        new("ollama", "Ollama models", ["OLLAMA_MODELS"], [Path.Combine(Home, ".ollama", "models")]),
        new("temp", "Temp files", ["TEMP", "TMP"], [Path.Combine(Local, "Temp")], CopyContents: false),
        new("claude", "Claude Code (sessions, settings)", ["CLAUDE_CONFIG_DIR"], [Path.Combine(Home, ".claude")], Movable: false),
        new("codex", "Codex (sessions, settings)", ["CODEX_HOME"], [Path.Combine(Home, ".codex")], Movable: false),
    ];

    public static StorageSpot? Find(string id) => Known.FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Where the tool writes now: the user's variable, then the machine's, then the default. Read fresh from the registry, not from this process.</summary>
    public static string CurrentPath(StorageSpot s)
    {
        var set = Environment.GetEnvironmentVariable(s.EnvVars[0], EnvironmentVariableTarget.User)
            ?? Environment.GetEnvironmentVariable(s.EnvVars[0], EnvironmentVariableTarget.Machine)
            ?? (s.Id == "npm" ? NpmrcCache() : null); // npm reads its variable before ~/.npmrc, so this order matches npm's
        return !string.IsNullOrWhiteSpace(set) ? Path.GetFullPath(Environment.ExpandEnvironmentVariables(set)) : s.Defaults.FirstOrDefault(Directory.Exists) ?? s.Defaults[0];
    }

    /// <summary>
    /// "cache=..." in the user's .npmrc, a common way to move npm's cache without a variable.
    /// shortcut: only npm's config file is read; pip.ini, uv.toml and NuGet.Config settings are not, add them if users report wrong paths.
    /// </summary>
    static string? NpmrcCache()
    {
        var rc = Environment.GetEnvironmentVariable("npm_config_userconfig", EnvironmentVariableTarget.User) ?? Path.Combine(Home, ".npmrc");
        try
        {
            return File.Exists(rc)
                ? File.ReadLines(rc).Select(l => l.Trim()).Where(l => l.StartsWith("cache", StringComparison.OrdinalIgnoreCase))
                    .Select(l => l.Split('=', 2)).Where(p => p.Length == 2 && p[0].Trim().Equals("cache", StringComparison.OrdinalIgnoreCase))
                    .Select(p => p[1].Trim().Trim('"')).LastOrDefault()
                : null;
        }
        catch (IOException) { return null; }
    }

    /// <summary>All spots at once: each walk is mostly waiting on the disk, so they overlap well.</summary>
    public static IReadOnlyList<SpotUsage> Measure() => Known.AsParallel().AsOrdered().Select(Measure).ToList();

    public static SpotUsage Measure(StorageSpot s)
    {
        var path = CurrentPath(s);
        var real = RealPath(path);
        var system = Path.GetPathRoot(Environment.SystemDirectory);
        return new SpotUsage(s, path, real, Directory.Exists(path), FolderBytes(path),
            string.Equals(Path.GetPathRoot(real), system, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Follows a junction or symbolic link at <paramref name="path"/> itself (not deeper ones).</summary>
    static string RealPath(string path)
    {
        try { return new DirectoryInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path; }
        catch (IOException) { return path; }
    }

    /// <summary>Total size of the files in a folder. Links inside are not followed: their bytes live elsewhere and would count twice.</summary>
    public static long FolderBytes(string path)
    {
        if (!Directory.Exists(path)) return 0;
        // Top-level folders are walked in parallel: a temp folder with 25,000 of them took a minute one by one.
        var top = Sum(path, recurse: false);
        var dirs = Safe(() => new DirectoryInfo(path).EnumerateDirectories("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).Select(d => d.FullName).ToList()) ?? [];
        return top + dirs.AsParallel().WithDegreeOfParallelism(8).Sum(d => Sum(d, recurse: true));
    }

    static long Sum(string path, bool recurse) => Safe(() =>
        new FileSystemEnumerable<long>(path, (ref FileSystemEntry e) => e.Length,
            new EnumerationOptions { RecurseSubdirectories = recurse, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
        { ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory }.Sum());

    /// <summary>A folder can vanish or lock mid-walk (temp files do): count what was there instead of failing.</summary>
    static T? Safe<T>(Func<T> f)
    {
        try { return f(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return default; }
    }

    /// <summary>Fixed drives other than the one the folder is on, with room for it plus a margin.</summary>
    public static IReadOnlyList<DriveInfo> Targets(SpotUsage u) =>
        DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady && !string.Equals(d.Name, u.Drive, StringComparison.OrdinalIgnoreCase))
            .Where(d => d.AvailableFreeSpace > u.Bytes + (2L << 30))
            .ToList();

    /// <summary>The folder a move to <paramref name="drive"/> lands in, e.g. D:\AiCache\npm.</summary>
    public static string TargetFolder(StorageSpot s, string drive) => Path.Combine(drive, "AiCache", s.Id);
}

/// <summary>Moves a cache to another folder and points its tool there, with a record to undo it.</summary>
public static class Mover
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static List<MoveRecord> History(string? file = null)
    {
        file ??= Paths.MovesFile;
        try { return File.Exists(file) ? JsonSerializer.Deserialize<List<MoveRecord>>(File.ReadAllText(file)) ?? [] : []; }
        catch (JsonException) { return []; }
    }

    static void Save(List<MoveRecord> records, string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(records, Json));
    }

    /// <param name="file">The undo record (tests use their own).</param>
    public static MoveResult Move(StorageSpot s, string to, string? file = null)
    {
        file ??= Paths.MovesFile;
        if (!s.Movable) return new(false, $"{s.Name} holds sessions and settings. Close the app and move it yourself with {s.EnvVars[0]}.");
        to = Path.GetFullPath(to);
        var records = History(file);
        var last = records.LastOrDefault(m => m.Id == s.Id);
        // Moving again to the same folder picks up what was in use last time.
        var retry = last is not null && Same(last.To, to) && Same(Storage.CurrentPath(s), to);
        var from = retry ? last!.From : Storage.CurrentPath(s);
        var root = Path.GetPathRoot(to)!;
        if (Same(root, to)) return new(false, "Pick a folder, not a whole drive.");
        if (new DriveInfo(root).DriveType != DriveType.Fixed) return new(false, $"{root} is not a fixed drive: tools would break while it is unplugged.");
        if (!retry && (IsInside(to, from) || IsInside(from, to))) return new(false, "The new folder can't be inside the old one, or the other way round.");
        // An undo moves everything in the new folder back, so it must start out empty: never a folder with your own files in it.
        if (!retry && Directory.Exists(to) && Directory.EnumerateFileSystemEntries(to).Any()) return new(false, $"{to} is not empty. Pick an empty or new folder.");
        var bytes = Storage.FolderBytes(from);
        if (new DriveInfo(root).AvailableFreeSpace < bytes + (1L << 30)) return new(false, $"Not enough free space on {root}.");

        // The undo record is written before anything changes, so a crash halfway can still be undone.
        if (!retry)
        {
            records.Add(new MoveRecord(s.Id, from, to, s.EnvVars.Select(Env.Backup).ToList(), DateTime.Now));
            Save(records, file);
        }

        Directory.CreateDirectory(to);
        if (s.CopyContents && Directory.Exists(from))
        {
            var code = Robocopy(from, to);
            if (code >= 16) return new(false, $"Copy failed (robocopy {code}). Nothing was switched.", 0, Storage.FolderBytes(from));
        }
        foreach (var name in s.EnvVars) Env.Set(name, to, expand: false);
        Env.Broadcast();
        var left = s.CopyContents ? Storage.FolderBytes(from) : 0;
        Log.Write($"moved {s.Id} {from} -> {to}, {(bytes - left) >> 20} MB moved, {left >> 20} MB left (in use)");
        return new(true, left > 0
            ? $"Moved to {to}. {left >> 20} MB was in use and stayed behind: close the app using it and move again."
            : $"Moved to {to}. Apps you start from now on use it.", bytes - left, left);
    }

    /// <summary>Moves the files back and puts the variables back exactly as they were (or removes them).</summary>
    public static MoveResult Undo(StorageSpot s, string? file = null)
    {
        file ??= Paths.MovesFile;
        var records = History(file);
        var r = records.LastOrDefault(m => m.Id == s.Id);
        if (r is null) return new(false, $"{s.Name} was not moved by AI RAM Guard.");
        if (s.CopyContents && Directory.Exists(r.To))
        {
            Directory.CreateDirectory(r.From);
            var code = Robocopy(r.To, r.From);
            if (code >= 16) return new(false, $"Copy back failed (robocopy {code}). Nothing was switched.");
        }
        foreach (var e in r.Env) Env.Set(e.Name, e.Value, e.Expand);
        Env.Broadcast();
        records.Remove(r);
        Save(records, file);
        var left = Storage.FolderBytes(r.To);
        if (left == 0 && Directory.Exists(r.To) && !Directory.EnumerateFileSystemEntries(r.To).Any()) Directory.Delete(r.To);
        Log.Write($"undid move of {s.Id}: back to {r.From}");
        return new(true, left > 0 ? $"Back in {r.From}. {left >> 20} MB was in use and stayed in {r.To}." : $"Back in {r.From}.", 0, left);
    }

    static bool Same(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    static bool IsInside(string path, string folder)
    {
        var f = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        return (Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) + Path.DirectorySeparatorChar).StartsWith(f, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Windows' own robocopy: moves file by file and deletes a source file only once it is copied, so a file in use simply stays.
    /// /XJ: junctions are never followed, so nothing outside the folder is ever moved.
    /// </summary>
    static int Robocopy(string from, string to)
    {
        var psi = new ProcessStartInfo("robocopy.exe") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
        foreach (var a in new[] { from, to, "/E", "/MOVE", "/XJ", "/R:0", "/W:0", "/NP", "/NFL", "/NDL", "/NJH", "/NJS" }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd(); // drain, or a full pipe blocks it
        p.WaitForExit();
        return p.ExitCode;
    }
}

/// <summary>User environment variables, read and written raw so an undo restores exactly what was there (%USERPROFILE%-style values too).</summary>
public static class Env
{
    public static EnvBackup Backup(string name)
    {
        using var k = Registry.CurrentUser.OpenSubKey("Environment");
        return k?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string v
            ? new(name, v, k.GetValueKind(name) == RegistryValueKind.ExpandString)
            : new(name, null, false);
    }

    public static void Set(string name, string? value, bool expand)
    {
        using var k = Registry.CurrentUser.CreateSubKey("Environment");
        if (value is null) k.DeleteValue(name, throwOnMissingValue: false);
        else k.SetValue(name, value, expand ? RegistryValueKind.ExpandString : RegistryValueKind.String);
    }

    /// <summary>Adds a folder to the user's PATH once, keeping the value's type. True when PATH changed.</summary>
    /// <param name="name">The variable (tests use their own).</param>
    public static bool AddToPath(string dir, string name = "Path")
    {
        var b = Backup(name);
        var entries = Split(b.Value);
        if (entries.Any(e => SameDir(e, dir))) return false;
        Set(name, string.Join(';', entries.Append(dir)), b.Value is null || b.Expand); // Windows' own user PATH is REG_EXPAND_SZ
        return true;
    }

    /// <summary>Removes exactly that folder from the user's PATH. True when PATH changed.</summary>
    public static bool RemoveFromPath(string dir, string name = "Path")
    {
        var b = Backup(name);
        var entries = Split(b.Value);
        var kept = entries.Where(e => !SameDir(e, dir)).ToList();
        if (kept.Count == entries.Count) return false;
        Set(name, kept.Count == 0 ? null : string.Join(';', kept), b.Expand);
        return true;
    }

    static List<string> Split(string? path) => (path ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();

    static bool SameDir(string entry, string dir) => string.Equals(
        Environment.ExpandEnvironmentVariables(entry).Trim().TrimEnd('\\'), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Tells Explorer and other apps the variables changed, so programs started from now on see them.</summary>
    public static void Broadcast() => SendMessageTimeout(0xFFFF, 0x001A, UIntPtr.Zero, "Environment", 2, 1000, out _); // HWND_BROADCAST, WM_SETTINGCHANGE, SMTO_ABORTIFHUNG

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, UIntPtr wParam, string lParam, uint flags, uint timeout, out UIntPtr result);
}
