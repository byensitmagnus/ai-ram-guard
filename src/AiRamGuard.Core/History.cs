using System.Text.Json;

namespace AiRamGuard.Core;

public sealed record Sample(DateTime Time, int FreeMB, int AiMB, int TotalMB);

/// <summary>The last 24 hours of RAM samples, kept small enough to save on every change.</summary>
public sealed class History
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);
    readonly List<Sample> _samples = [];

    public IReadOnlyList<Sample> Samples => _samples;

    public void Add(Sample s)
    {
        if (_samples.Count > 0 && s.Time <= _samples[^1].Time) return; // clock went backwards: ignore
        _samples.Add(s);
        var cutoff = s.Time - Window;
        var old = _samples.FindIndex(x => x.Time >= cutoff);
        if (old > 0) _samples.RemoveRange(0, old);
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(_samples));
    }

    public static History Load(string path)
    {
        var h = new History();
        try
        {
            if (File.Exists(path))
                foreach (var s in JsonSerializer.Deserialize<List<Sample>>(File.ReadAllText(path)) ?? []) h.Add(s);
        }
        catch (JsonException) { } // a damaged history file only costs the graph, never the guard
        return h;
    }
}

/// <summary>Lifetime counters shown in the app.</summary>
public sealed class Stats
{
    public DateTime Since { get; set; } = DateTime.Now;
    public long FreedMB { get; set; }
    public int OrphansClosed { get; set; }
    public int Rounds { get; set; }

    public void Record(CleanResult r)
    {
        Rounds++;
        OrphansClosed += r.Killed;
        FreedMB += Math.Max(0, (r.FreeAfterBytes - r.FreeBeforeBytes) / (1024 * 1024));
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this));
    }

    public static Stats Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<Stats>(File.ReadAllText(path)) ?? new() : new(); }
        catch (JsonException) { return new(); }
    }
}
