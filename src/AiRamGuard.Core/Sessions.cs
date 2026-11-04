using System.Text.RegularExpressions;

namespace AiRamGuard.Core;

/// <summary>A Claude desktop session nobody wrote in for a while, with the MCP servers it started.</summary>
public sealed record IdleSession(string Id, IReadOnlyList<ProcessInfo> Processes, TimeSpan Idle)
{
    public long Bytes => Processes.Sum(p => p.WorkingSetBytes);
}

/// <summary>
/// Claude (Desktop / Code) runs each session as its own Claude Code process ("claude.exe ... --input-format stream-json --resume=&lt;id&gt;")
/// and keeps it, with every MCP server, until the app closes. Closing the process is safe: the app starts it again with
/// the same --resume id when you write in the session, and the conversation is read back from its transcript.
/// </summary>
public static class Sessions
{
    static readonly Regex Resume = new(@"--input-format[= ]stream-json\b.*--resume[= ]""?([0-9a-f]{8}-[0-9a-f-]{27})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <param name="lastWrite">When the session's transcript was last written, or null when there is none.</param>
    public static IReadOnlyList<IdleSession> FindIdle(Snapshot snap, TimeSpan min, Func<string, DateTime?> lastWrite, DateTime now)
    {
        var children = snap.Processes.ToLookup(p => p.ParentPid);
        var list = new List<IdleSession>();
        foreach (var p in snap.Processes)
        {
            if (!string.Equals(p.Name, "claude.exe", StringComparison.OrdinalIgnoreCase) || Resume.Match(p.CommandLine) is not { Success: true } m) continue;
            if (p.Created == DateTime.MinValue) continue; // unknown start: TryKill couldn't check it is still the same process
            var id = m.Groups[1].Value;
            if (lastWrite(id) is not { } w) continue; // no transcript where we look (another config dir): we can't tell it is idle
            // A process started after the last write is a session just reopened: count from its start.
            var last = w > p.Created ? w : p.Created;
            if (now - last < min) continue;
            list.Add(new IdleSession(id, Tree(p, children), now - last));
        }
        return list;
    }

    /// <summary>The session first, then what it started: closing the session first stops it from restarting a server.</summary>
    static List<ProcessInfo> Tree(ProcessInfo root, ILookup<int, ProcessInfo> children)
    {
        var list = new List<ProcessInfo> { root };
        for (var i = 0; i < list.Count && list.Count < 256; i++)
            list.AddRange(children[list[i].Pid].Where(c => c.Created >= list[i].Created && !list.Contains(c))); // a PID reused by an older process is not a child
        return list;
    }

    /// <summary>The transcript's write time: ~\.claude\projects\&lt;project&gt;\&lt;id&gt;.jsonl.</summary>
    public static DateTime? LastWrite(string id)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
            if (!Directory.Exists(dir)) return null;
            return Directory.EnumerateFiles(dir, id + ".jsonl", SearchOption.AllDirectories)
                .Select(f => (DateTime?)File.GetLastWriteTime(f)).Max();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
