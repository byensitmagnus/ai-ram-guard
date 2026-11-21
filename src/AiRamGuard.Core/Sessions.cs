using System.Text.RegularExpressions;

namespace AiRamGuard.Core;

/// <summary>A Claude session nobody wrote in for a while, with the MCP servers it started.</summary>
public sealed record IdleSession(string Id, IReadOnlyList<ProcessInfo> Processes, TimeSpan Idle)
{
    /// <summary>What closing it gives back, RAM and pagefile: idle sessions are mostly paged out.</summary>
    public long Bytes => Processes.Sum(p => p.HeldBytes);
}

/// <summary>
/// Claude runs each session as its own Claude Code process and keeps it, with every MCP server, long after you stopped using it:
/// the desktop app as "claude.exe ... --input-format stream-json --resume=&lt;id&gt;", 'claude --bg' as "claude.exe --resume &lt;id&gt;"
/// under a "--bg-pty-host" process of the daemon. Closing the process is safe: the conversation is read back from its transcript
/// when you write in the session again (desktop) or run 'claude attach' (background; the daemon lists it as stopped).
/// </summary>
public static class Sessions
{
    static readonly Regex DesktopSession = new(@"--input-format[= ]stream-json\b.*--resume[= ]""?([0-9a-f]{8}-[0-9a-f-]{27})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // --resume can also be the transcript's path; a new session has --session-id.
    static readonly Regex BackgroundSession = new(@"--(?:resume|session-id)[= ]""?(?:[^""\s]*[\\/])?([0-9a-f]{8}-[0-9a-f-]{27})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Idle time for the Fixes card: the user's own setting, or 3 hours when closing by itself is off.</summary>
    public static TimeSpan SuggestAfter(GuardSettings s) => TimeSpan.FromHours(s.CloseIdleSessionHours > 0 ? s.CloseIdleSessionHours : 3);

    public static IReadOnlyList<IdleSession> FindIdle(Snapshot snap, GuardSettings s) => FindIdle(snap, SuggestAfter(s));

    /// <summary>Against the real disk: transcripts, and Claude's own word on whether a session is busy.</summary>
    public static IReadOnlyList<IdleSession> FindIdle(Snapshot snap, TimeSpan min) => FindIdle(snap, min, LastWrite, DateTime.Now, Registered);

    /// <summary>What Claude Code says about one of its own session processes.</summary>
    /// <param name="Id">The session it runs now: /clear gives a process a new one, and the command line keeps the old.</param>
    public sealed record State(string Id, bool Busy);

    /// <param name="lastWrite">When the session last wrote anything, or null when it has no transcript.</param>
    /// <param name="registered">Claude's own record of the process, or null when it keeps none.</param>
    public static IReadOnlyList<IdleSession> FindIdle(Snapshot snap, TimeSpan min, Func<string, DateTime?> lastWrite, DateTime now, Func<ProcessInfo, State?>? registered = null)
    {
        var children = snap.Processes.ToLookup(p => p.ParentPid);
        var byPid = snap.Processes.ToDictionary(p => p.Pid);
        var list = new List<IdleSession>();
        foreach (var p in snap.Processes)
        {
            if (!string.Equals(p.Name, "claude.exe", StringComparison.OrdinalIgnoreCase)) continue;
            var host = byPid.TryGetValue(p.ParentPid, out var parent) && parent.Created <= p.Created && parent.Created != DateTime.MinValue
                && string.Equals(parent.Name, "claude.exe", StringComparison.OrdinalIgnoreCase) && parent.CommandLine.Contains("--bg-pty-host", StringComparison.OrdinalIgnoreCase) ? parent : null;
            if ((host is null ? DesktopSession : BackgroundSession).Match(p.CommandLine) is not { Success: true } m) continue;
            if (p.Created == DateTime.MinValue) continue; // unknown start: TryKill couldn't check it is still the same process
            if (now - p.Created < min) continue; // also spares the disk: no transcript lookup for a young session
            var state = registered?.Invoke(p);
            if (state is { Busy: true }) continue; // working without writing: a long build, a job in the background
            var id = state?.Id ?? m.Groups[1].Value;
            if (lastWrite(id) is not { } w) continue; // no transcript where we look (another config dir): we can't tell it is idle
            // A process started after the last write is a session just reopened: count from its start.
            var last = w > p.Created ? w : p.Created;
            if (now - last < min) continue;
            var tree = Tree(p, children);
            if (host is not null) tree.Add(host); // last: it only carries the session's terminal
            list.Add(new IdleSession(id, tree, now - last));
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

    /// <summary>
    /// The last write to ~\.claude\projects\&lt;project&gt;\&lt;id&gt;.jsonl, to the folder next to it (subagents, tool results) or to
    /// %TEMP%\claude\*\&lt;id&gt; (output of background jobs): a session waiting an hour for a subagent or a build is not idle.
    /// </summary>
    public static DateTime? LastWrite(string id)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
            if (!Directory.Exists(dir)) return null;
            DateTime? last = null;
            foreach (var project in Directory.EnumerateDirectories(dir))
            {
                var transcript = Path.Combine(project, id + ".jsonl");
                if (!File.Exists(transcript)) continue;
                var w = Newest(Path.Combine(project, id), File.GetLastWriteTime(transcript));
                if (last is null || w > last) last = w;
            }
            var temp = Path.Combine(Path.GetTempPath(), "claude");
            if (last is { } t && Directory.Exists(temp))
                foreach (var scope in Directory.EnumerateDirectories(temp)) last = t = Newest(Path.Combine(scope, id), t);
            return last;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    static DateTime Newest(string dir, DateTime atLeast)
    {
        if (Directory.Exists(dir))
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories))
                if (f.LastWriteTime > atLeast) atLeast = f.LastWriteTime;
        return atLeast;
    }

    /// <summary>~\.claude\sessions\&lt;pid&gt;.json, only while it describes this process and not an older one with the same PID.</summary>
    public static State? Registered(ProcessInfo p)
    {
        try
        {
            var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "sessions", p.Pid + ".json");
            if (!File.Exists(file)) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
            var r = doc.RootElement;
            if (!long.TryParse(r.GetProperty("procStart").GetString(), out var start) || Math.Abs((DateTime.FromFileTime(start) - p.Created).TotalSeconds) > 2) return null;
            return r.GetProperty("sessionId").GetString() is { Length: > 0 } id
                ? new State(id, r.TryGetProperty("status", out var s) && s.GetString() == "busy") : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException or ArgumentOutOfRangeException) { return null; }
    }
}
