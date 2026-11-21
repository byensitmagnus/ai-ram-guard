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

    public static IReadOnlyList<IdleSession> FindIdle(Snapshot snap, GuardSettings s) => FindIdle(snap, SuggestAfter(s), LastWrite, DateTime.Now);

    /// <param name="lastWrite">When the session last wrote anything, or null when it has no transcript.</param>
    public static IReadOnlyList<IdleSession> FindIdle(Snapshot snap, TimeSpan min, Func<string, DateTime?> lastWrite, DateTime now)
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
            var id = m.Groups[1].Value;
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
    /// The last write to ~\.claude\projects\&lt;project&gt;\&lt;id&gt;.jsonl or to the folder next to it. Subagents and tool results
    /// write there, not in the transcript: a session waiting an hour for a subagent is not idle.
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
                var w = File.GetLastWriteTime(transcript);
                var side = Path.Combine(project, id);
                if (Directory.Exists(side))
                    foreach (var f in new DirectoryInfo(side).EnumerateFiles("*", SearchOption.AllDirectories))
                        if (f.LastWriteTime > w) w = f.LastWriteTime;
                if (last is null || w > last) last = w;
            }
            return last;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
