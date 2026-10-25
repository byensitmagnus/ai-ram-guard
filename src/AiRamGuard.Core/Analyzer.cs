using System.Text.RegularExpressions;

namespace AiRamGuard.Core;

/// <summary>Turns a snapshot into "which AI app uses how much RAM, and what is left behind".</summary>
public static class Analyzer
{
    public static Report Analyze(Snapshot snap, ProfileSet profiles)
    {
        var byPid = snap.Processes.ToDictionary(p => p.Pid);
        var owned = new Dictionary<AgentProfile, List<ProcessInfo>>();
        var orphans = new List<ProcessInfo>();

        foreach (var p in snap.Processes)
        {
            if (profiles.IsHelper(p) && IsOrphan(p, byPid, profiles))
            {
                orphans.Add(p);
                continue;
            }
            var agent = OwningAgent(p, byPid, profiles);
            if (agent is not null) (owned.TryGetValue(agent, out var list) ? list : owned[agent] = []).Add(p);
        }

        var agents = owned
            .Select(kv => new AgentUsage(kv.Key, kv.Value, GroupHelpers(kv.Value.Where(profiles.IsHelper))))
            .OrderByDescending(a => a.Bytes)
            .ToList();
        return new Report(snap, agents, orphans);
    }

    /// <summary>Walks up the parent chain until it meets a process an agent profile owns.</summary>
    internal static AgentProfile? OwningAgent(ProcessInfo p, IReadOnlyDictionary<int, ProcessInfo> byPid, ProfileSet profiles)
    {
        var current = p;
        for (var depth = 0; depth < 32; depth++) // depth cap: PID cycles exist after reuse
        {
            var agent = profiles.Agents.FirstOrDefault(a => a.OwnsProcessName(current.Name));
            if (agent is not null) return agent;
            if (!byPid.TryGetValue(current.ParentPid, out var parent) || parent.Created > current.Created) break;
            current = parent;
        }
        // CLI agents (Gemini CLI etc.) have no app process; recognise them by their own command line.
        return profiles.AgentByHelperPattern(p.CommandLine);
    }

    /// <summary>
    /// Walking up through helper wrappers (npx, cmd /c ..., the server itself) ends at a parent that is gone
    /// or whose PID now belongs to a newer process. A helper the user started from a live terminal is not an orphan.
    /// </summary>
    internal static bool IsOrphan(ProcessInfo p, IReadOnlyDictionary<int, ProcessInfo> byPid, ProfileSet profiles)
    {
        var current = p;
        for (var depth = 0; depth < 32; depth++)
        {
            if (!byPid.TryGetValue(current.ParentPid, out var parent) || parent.Created > current.Created) return true;
            if (!profiles.IsHelper(parent)) return false;
            current = parent;
        }
        return false;
    }

    public static IReadOnlyList<HelperGroup> GroupHelpers(IEnumerable<ProcessInfo> helpers) =>
        helpers.GroupBy(h => HelperKey(h.CommandLine))
            .Select(g => new HelperGroup(g.Key, g.ToList()))
            .OrderByDescending(g => g.Bytes)
            .ToList();

    static readonly Regex Scoped = new(@"@[a-z0-9][\w.-]*[\\/][\w.-]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex Script = new(@"([\w.-]+)\.(?:m?js|cjs|py|ts)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex PackageDir = new(@"node_modules[\\/]((?:@[\w.-]+[\\/])?[\w.-]+)[\\/]", RegexOptions.Compiled);

    /// <summary>A short human name for a helper command line, e.g. "@wonderwhy-er/desktop-commander".</summary>
    public static string HelperKey(string commandLine)
    {
        var scoped = Scoped.Match(commandLine);
        if (scoped.Success) return Regex.Replace(scoped.Value.Replace('\\', '/'), @"@(latest|\d[\w.-]*)$", "");
        var dirs = PackageDir.Matches(commandLine);
        if (dirs.Count > 0)
        {
            var last = dirs[^1].Groups[1].Value.Replace('\\', '/');
            if (last is not ("npm" or ".bin")) return last;
        }
        var script = Script.Match(commandLine);
        if (script.Success) return script.Value;
        var exe = commandLine.StartsWith('"') ? commandLine[1..].Split('"', 2)[0] : commandLine.Split(' ', 2)[0];
        return Path.GetFileName(exe);
    }
}
