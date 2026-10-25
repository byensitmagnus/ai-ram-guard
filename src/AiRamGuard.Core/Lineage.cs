using System.Text.Json;

namespace AiRamGuard.Core;

/// <summary>
/// Helpers seen running under a live AI app. Only these may later be closed as leftovers:
/// a dead parent alone is normal for apps started through a launcher, `start` or Task Scheduler.
/// </summary>
public sealed class Lineage
{
    readonly HashSet<string> _seen;

    public Lineage(IEnumerable<string>? seen = null) => _seen = new(seen ?? []);

    /// <summary>PID plus start time: a reused PID is a different process.</summary>
    static string Key(ProcessInfo p) => $"{p.Pid}:{p.Created.Ticks}";

    public bool Contains(ProcessInfo p) => _seen.Contains(Key(p));

    public void Add(ProcessInfo p)
    {
        if (p.Created != DateTime.MinValue) _seen.Add(Key(p));
    }

    /// <summary>Forget processes that no longer run, so the file stays small.</summary>
    public void Prune(IEnumerable<ProcessInfo> alive) => _seen.IntersectWith(alive.Select(Key));

    public static Lineage Load(string file)
    {
        try { return new(JsonSerializer.Deserialize<string[]>(File.ReadAllText(file))); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    // shortcut: the app and the CLI can both write this file; last writer wins. Fine while both see the same processes.
    public void Save(string file)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonSerializer.Serialize(_seen));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
