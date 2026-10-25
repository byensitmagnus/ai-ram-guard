using System.Text.RegularExpressions;

namespace AiRamGuard.Core;

/// <summary>Turns a snapshot into "which AI app uses how much RAM, and what is left behind".</summary>
public static class Analyzer
{
    /// <param name="lineage">Helpers seen under a live AI app in earlier scans; updated with this one. Only those can be orphans.</param>
    public static Report Analyze(Snapshot snap, ProfileSet profiles, Lineage? lineage = null)
    {
        lineage ??= new();
        var byPid = snap.Processes.ToDictionary(p => p.Pid);
        var owned = new Dictionary<AgentProfile, List<ProcessInfo>>();
        var orphans = new List<ProcessInfo>();

        foreach (var p in snap.Processes)
        {
            if (profiles.IsHelper(p))
            {
                var root = HelperRoot(p, byPid, profiles);
                if (root is null && lineage.Contains(p))
                {
                    orphans.Add(p);
                    continue;
                }
                // Started by the AI app itself (through helpers only). A dev server an agent ran in a shell is not recorded.
                if (root is not null && profiles.Agents.Any(a => a.Owns(root))) lineage.Add(p);
            }
            var agent = OwningAgent(p, byPid, profiles);
            if (agent is not null) (owned.TryGetValue(agent, out var list) ? list : owned[agent] = []).Add(p);
        }
        lineage.Prune(snap.Processes);

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
            var agent = profiles.Agents.FirstOrDefault(a => a.Owns(current));
            if (agent is not null) return agent;
            if (!byPid.TryGetValue(current.ParentPid, out var parent) || parent.Created > current.Created) break;
            current = parent;
        }
        // A Codex REPL whose app is gone still belongs to Codex. Only script runtimes: an editor open on such a path is not one.
        return AgentProfile.IsRuntime(p.Name) ? profiles.AgentByHelperPattern(p.CommandLine) : null;
    }

    /// <summary>
    /// Walks up through helper wrappers (npx, cmd /c ..., the server itself) to the first non-helper parent:
    /// the AI app, a terminal, ... Null when the chain ends at a parent that is gone or whose PID now belongs to a newer process.
    /// </summary>
    internal static ProcessInfo? HelperRoot(ProcessInfo p, IReadOnlyDictionary<int, ProcessInfo> byPid, ProfileSet profiles)
    {
        var current = p;
        for (var depth = 0; depth < 32; depth++)
        {
            if (!byPid.TryGetValue(current.ParentPid, out var parent) || parent.Created > current.Created) return null;
            if (!profiles.IsHelper(parent)) return parent;
            current = parent;
        }
        return current; // PID cycle: treat as alive, never as an orphan
    }

    /// <summary>True when <paramref name="ancestorPid"/> started <paramref name="p"/>, directly or further up.</summary>
    public static bool HasAncestor(ProcessInfo p, int ancestorPid, IReadOnlyDictionary<int, ProcessInfo> byPid)
    {
        var current = p;
        for (var depth = 0; depth < 32; depth++)
        {
            if (!byPid.TryGetValue(current.ParentPid, out var parent) || parent.Created > current.Created) return false;
            if (parent.Pid == ancestorPid) return true;
            current = parent;
        }
        return false;
    }

    public static IReadOnlyList<HelperGroup> GroupHelpers(IEnumerable<ProcessInfo> helpers)
    {
        var keyed = helpers.Select(h => (Key: HelperKey(h.CommandLine), Process: h)).ToList();
        // "desktop-commander" (a bin shim) and "@wonderwhy-er/desktop-commander" are the same server.
        var scoped = keyed.Select(k => k.Key).Where(k => k.StartsWith('@')).Distinct()
            .GroupBy(k => k.Split('/')[^1]).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single());
        return keyed
            .GroupBy(k => scoped.TryGetValue(k.Key, out var full) ? full : k.Key)
            .Select(g => new HelperGroup(g.Key, g.Select(x => x.Process).ToList()))
            .OrderByDescending(g => g.Bytes)
            .ToList();
    }

    static readonly Regex CmdWrapper = new(@"^""?[^""]*\bcmd(?:\.exe)?""?\s+(?:/\w\s+)*/c\s+(?:call\s+)?(.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    static readonly Regex Scoped = new(@"@[a-z0-9][\w.-]*[\\/][\w.-]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex Script = new(@"([\w.-]+)\.(?:m?js|cjs|py|ts)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex PackageDir = new(@"node_modules[\\/]((?:@[\w.-]+[\\/])?[\w.-]+)[\\/]", RegexOptions.Compiled);

    /// <summary>A short human name for a helper command line, e.g. "@wonderwhy-er/desktop-commander".</summary>
    public static string HelperKey(string commandLine)
    {
        // "cmd /d /s /c <real command>": name what it runs, not cmd.exe.
        var wrapped = CmdWrapper.Match(commandLine);
        if (wrapped.Success) commandLine = wrapped.Groups[1].Value.Replace("^", "").Trim().Trim('"');
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
