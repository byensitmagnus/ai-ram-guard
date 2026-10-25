using System.Text.Json;
using AiRamGuard.Core;

const string Help = """
AI RAM Guard (airg) - stop AI agents and MCP servers from eating your RAM.

Usage:
  airg scan [--json]          Show RAM per AI app, duplicate MCP servers and orphans
  airg advise [--json]        Permanent fixes: duplicated MCP servers, npx wrappers, which config to change
  airg clean [--dry-run]      Close orphans and trim AI apps now (ignores the threshold)
  airg watch [--min-free GB] [--interval SEC] [--no-game-mode]
                              Guard continuously (what the tray app does)
  airg disk [--json]          Where AI tools and their caches write, and how much
  airg move <id> <drive|folder>
                              Move a cache to another drive, e.g. airg move npm D:
  airg undo <id>              Move it back and restore the variable as it was
  airg --version              Print the version

https://github.com/byensitmagnus/ai-ram-guard
""";

// One number format everywhere ("4,600 MB", "8.4 GB"), and UTF-8 so "×" survives a pipe or a file.
System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
Console.OutputEncoding = new System.Text.UTF8Encoding(false);

var cmd = args.FirstOrDefault() ?? "scan";
bool Has(string flag) => args.Contains(flag);
string? Opt(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }

var profiles = ProfileSet.Load();
var settings = new GuardSettings();
if (Opt("--min-free") is { } mf) settings.MinFreeMB = (int)(double.Parse(mf, System.Globalization.CultureInfo.InvariantCulture) * 1024);
if (Opt("--interval") is { } iv) settings.IntervalSeconds = int.Parse(iv);
if (Has("--no-game-mode")) settings.GameMode = false;

switch (cmd)
{
    case "scan":
        var report = Guard.Scan(profiles);
        if (Has("--json")) Console.WriteLine(JsonSerializer.Serialize(ToJson(report), new JsonSerializerOptions { WriteIndented = true }));
        else Print(report);
        return 0;

    case "advise":
        var advice = Advisor.Advise(Guard.Scan(profiles), KnownConfigs.Read());
        if (Has("--json"))
            Console.WriteLine(JsonSerializer.Serialize(advice.Select(a => new { kind = a.Kind.ToString(), a.Title, a.Detail, savingsMB = a.SavingsBytes / 1048576, a.ConfigPath }), new JsonSerializerOptions { WriteIndented = true }));
        else if (advice.Count == 0) Console.WriteLine("Nothing to fix. No duplicated MCP servers and no leftovers.");
        else
            foreach (var a in advice)
            {
                Console.WriteLine($"{a.Title}{(a.SavingsBytes > 0 ? $"  (saves ~{a.SavingsBytes / 1048576:N0} MB)" : "")}");
                Console.WriteLine($"  {a.Detail}");
                Console.WriteLine();
            }
        return 0;

    case "clean":
        var r = Guard.Run(settings, profiles, force: true, dryRun: Has("--dry-run"));
        Console.WriteLine($"{(Has("--dry-run") ? "[dry-run] " : "")}Closed {r.Killed} orphans, trimmed {r.Trimmed} AI processes, freed {r.FreedBytes / 1048576:N0} MB. Free RAM {Guard.Gb(r.FreeBeforeBytes)} -> {Guard.Gb(r.FreeAfterBytes)} GB");
        return 0;

    case "watch":
        Console.WriteLine($"Guarding every {settings.IntervalSeconds}s, min free {settings.MinFreeMB / 1024.0:0.#} GB. Ctrl+C to stop.");
        while (true)
        {
            var w = Guard.Run(settings, profiles);
            if (w.Killed + w.Trimmed > 0) Console.WriteLine($"{DateTime.Now:HH:mm} {w.Reason}: closed {w.Killed}, trimmed {w.Trimmed}, freed {w.FreedBytes / 1048576:N0} MB, free {Guard.Gb(w.FreeAfterBytes)} GB");
            Thread.Sleep(TimeSpan.FromSeconds(settings.IntervalSeconds));
        }

    case "disk":
        var spots = Storage.Measure();
        if (Has("--json"))
            Console.WriteLine(JsonSerializer.Serialize(spots.Select(u => new { id = u.Spot.Id, name = u.Spot.Name, path = u.Path, realPath = u.RealPath, mb = u.Bytes / 1048576, u.OnSystemDrive, movable = u.Spot.Movable && u.Links == 0, links = u.Links, variable = u.Spot.EnvVars[0] }), new JsonSerializerOptions { WriteIndented = true }));
        else
        {
            foreach (var u in spots.Where(u => u.Exists).OrderByDescending(u => u.Bytes))
                Console.WriteLine($"{u.Spot.Id,-12} {Guard.Gb(u.Bytes),6} GB  {(u.OnSystemDrive ? "system drive" : u.Drive),-12}  {u.RealPath}{(u.Links > 0 ? $"  ({u.Links} links: not movable)" : "")}");
            Console.WriteLine("\nMove one with 'airg move <id> D:'. Claude Code and Codex hold sessions and settings: they are listed, not moved.");
        }
        return 0;

    case "move" or "undo":
        if (args.Length < (cmd == "move" ? 3 : 2) || Storage.Find(args[1]) is not { } spot)
        {
            Console.WriteLine($"Usage: airg {cmd} <id>{(cmd == "move" ? " <drive|folder>" : "")}. Ids: {string.Join(", ", Storage.Known.Where(k => k.Movable).Select(k => k.Id))}");
            return 1;
        }
        var target = cmd == "move" ? args[2] : "";
        if (target.TrimEnd('\\', '/').Length == 2 && target[1] == ':') target = Storage.TargetFolder(spot, target[..2] + "\\");
        var mr = cmd == "move" ? Mover.Move(spot, target) : Mover.Undo(spot);
        Console.WriteLine(mr.Message);
        return mr.Ok ? 0 : 1;

    case "--version" or "-v" or "version":
        Console.WriteLine(typeof(Guard).Assembly.GetName().Version?.ToString(3));
        return 0;

    default:
        Console.WriteLine(Help);
        return cmd is "help" or "--help" or "-h" ? 0 : 1;
}

static void Print(Report r)
{
    var s = r.Snapshot;
    Console.WriteLine($"RAM: {Guard.Gb(s.FreeRamBytes)} GB free of {Guard.Gb(s.TotalRamBytes)} GB. AI apps use {Guard.Gb(r.AiBytes)} GB.\n");
    foreach (var a in r.Agents)
    {
        Console.WriteLine($"{a.Profile.Name,-32} {a.Processes.Count,4} proc  {a.Bytes / 1048576,7:N0} MB");
        foreach (var h in a.Helpers.Where(h => h.Count > 1 || h.Bytes > 100 * 1048576))
            Console.WriteLine($"  {(h.Count > 1 ? $"{h.Count}x" : "  "),4} {h.Key,-44} {h.Bytes / 1048576,7:N0} MB{(h.Count > 2 ? "  <- duplicate MCP servers" : "")}");
    }
    if (r.Orphans.Count > 0)
    {
        Console.WriteLine($"\nOrphans (their AI session is gone): {r.Orphans.Count} processes, {r.Orphans.Sum(o => o.WorkingSetBytes) / 1048576:N0} MB");
        foreach (var g in Analyzer.GroupHelpers(r.Orphans))
            Console.WriteLine($"  {g.Count,3}x {g.Key,-44} {g.Bytes / 1048576,7:N0} MB");
        Console.WriteLine("Run 'airg clean' to close them.");
    }
}

static object ToJson(Report r) => new
{
    totalRamMB = r.Snapshot.TotalRamBytes / 1048576,
    freeRamMB = r.Snapshot.FreeRamBytes / 1048576,
    aiMB = r.AiBytes / 1048576,
    agents = r.Agents.Select(a => new
    {
        id = a.Profile.Id,
        name = a.Profile.Name,
        processes = a.Processes.Count,
        mb = a.Bytes / 1048576,
        helpers = a.Helpers.Select(h => new { key = h.Key, count = h.Count, mb = h.Bytes / 1048576 }),
    }),
    orphans = r.Orphans.Select(o => new { pid = o.Pid, name = o.Name, key = Analyzer.HelperKey(o.CommandLine), mb = o.WorkingSetBytes / 1048576 }),
};
