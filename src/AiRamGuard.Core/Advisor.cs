namespace AiRamGuard.Core;

public enum AdviceKind { Orphans, Duplicates, NpxWrapper, BigApp, Connections, IdleSessions, Commit }

/// <summary>A concrete, permanent fix, with an estimate of what it saves.</summary>
/// <param name="Servers">The config entries that start a duplicated server: when there are any, the app can switch them off itself.</param>
/// <param name="AgentId">The AI app it is about (for a cap).</param>
/// <param name="Frees">The processes the fix closes. Fixes overlap (an idle session's servers are also somebody's duplicates): see <see cref="Advisor.TotalSavings"/>.</param>
public sealed record Advice(AdviceKind Kind, string Title, string Detail, long SavingsBytes, string? ConfigPath = null,
    IReadOnlyList<ServerEntry>? Servers = null, string? AgentId = null, IReadOnlyList<ProcessInfo>? Frees = null, bool Share = false)
{
    /// <summary>The app can do this one itself; the others need the user (a plugin setting, an npm install).</summary>
    public bool CanFix => Kind is AdviceKind.Orphans or AdviceKind.BigApp or AdviceKind.IdleSessions || Servers is { Count: > 0 };
}

/// <summary>An AI app's config file, read so advice can point at the exact file to change.</summary>
public sealed record ConfigFile(string App, string Path, string Content);

/// <summary>Turns a report into fixes that stop the waste at the source, instead of cleaning it up forever.</summary>
public static class Advisor
{
    /// <param name="applied">Servers AI RAM Guard switched off: their copies run on until sessions close, which is not a new problem.</param>
    /// <param name="caps">Apps that have a cap already need no cap advice.</param>
    /// <param name="idle">Claude sessions nobody wrote in for hours, see <see cref="Sessions.FindIdle(Snapshot, GuardSettings)"/>.</param>
    public static IReadOnlyList<Advice> Advise(Report r, IReadOnlyList<ConfigFile> configs, IReadOnlyList<FixRecord>? applied = null, IReadOnlyDictionary<string, int>? caps = null,
        IReadOnlyList<IdleSession>? idle = null, IReadOnlyList<ShareSpec>? shareable = null) // shareable: from the profiles, servers that can run once for all sessions
    {
        var list = new List<Advice>();
        var snap = r.Snapshot;
        var byPid = snap.Processes.GroupBy(p => p.Pid).ToDictionary(g => g.Key, g => g.First());

        // Savings are what closing gives back (RAM + pagefile), not the working set: the guard's own trimming shrinks that to almost nothing.
        if (snap.CommitLimitBytes > 0 && snap.CommitBytes >= snap.CommitLimitBytes * CommitWarn)
            list.Add(new Advice(AdviceKind.Commit,
                $"Windows is close to its memory limit: {Gb(snap.CommitLimitBytes - snap.CommitBytes)} GB left",
                $"Programs have reserved {Gb(snap.CommitBytes)} of the {Gb(snap.CommitLimitBytes)} GB Windows can hand out right now (RAM plus pagefile). When it can't grow the pagefile further, apps crash or won't start, even with free RAM. "
                + $"Freeing RAM doesn't lower it, closing does. AI apps hold {Gb(r.Agents.Sum(a => a.HeldBytes) + r.Orphans.Sum(o => o.HeldBytes))} GB of it.", 0));

        if (idle is { Count: > 0 })
            list.Add(new Advice(AdviceKind.IdleSessions,
                $"{idle.Count} Claude session{(idle.Count > 1 ? "s" : "")} idle for {Math.Max(1, (int)idle.Min(s => s.Idle).TotalHours)}+ hours",
                "Each keeps its own process and MCP servers running. Closing keeps the conversation: Claude starts the session again when you write in it (a background session with 'claude attach').",
                idle.Sum(s => s.Bytes), Frees: idle.SelectMany(s => s.Processes).ToList()));

        if (r.Orphans.Count > 0)
        {
            var mb = r.Orphans.Sum(o => o.HeldBytes);
            list.Add(new Advice(AdviceKind.Orphans,
                $"{r.Orphans.Count} leftover AI processes",
                "Their AI session is closed, but they still run. Closing them is safe.", mb, Frees: r.Orphans));
        }

        foreach (var a in r.Agents)
        {
            var own = ConfigsFor(a.Profile.Id, configs);
            var dups = new List<(HelperGroup H, IReadOnlyList<ServerEntry> Servers)>();
            foreach (var h in a.Helpers.Where(h => h.Count >= 3 && h.HeldBytes - h.HeldBytes / h.Count >= MinSavings))
            {
                var servers = McpServers.Find(h.Key, own);
                if (servers.Count == 0 && (McpServers.IsOff(h.Key, own) || Term(h.Key) is { } term
                        && (applied ?? []).Any(f => f.Entries.Any(e => e.Definition.Contains(term, StringComparison.OrdinalIgnoreCase)))))
                    continue; // switched off already: the copies go as their sessions close
                dups.Add((h, servers));
            }

            // A launcher script that starts the real server (email-mcp-launch.py → @codefuturist/email-mcp): one config entry, so one fix. The server's own card would say "turn it off in the app".
            var children = dups.Where(d => d.Servers.Count == 0).Select(d => (d, parent: dups.FirstOrDefault(f => f.Servers.Count > 0 && d.H.Processes.All(p =>
                Analyzer.Ancestors(p, byPid).Any(x => f.H.Processes.Any(y => y.Pid == x.Pid)))))).Where(x => x.parent.H is not null).ToList();

            foreach (var d in dups.Where(d => !children.Any(c => c.d == d)))
            {
                var (h, servers) = d;
                var frees = h.Processes.Skip(1).ToList();
                var bytes = h.HeldBytes - h.HeldBytes / h.Count;
                foreach (var c in children.Where(c => c.parent == d))
                {
                    var freed = c.d.H.Processes.Where(p => Analyzer.Ancestors(p, byPid).Any(x => frees.Any(y => y.Pid == x.Pid))).ToList();
                    frees.AddRange(freed);
                    bytes += freed.Sum(p => p.HeldBytes);
                }
                var config = FindConfig(h.Key, own);
                var detail = servers.Count > 0 && servers.All(x => x.Section == "plugins")
                    ? $"Every {a.Profile.Name} session starts its own copy. It comes from the plugin \"{servers[0].Name}\", which also has skills: turning the plugin off removes those too. It takes effect when Claude Desktop restarts, and you can turn it back on here."
                    : servers.Count > 0
                    ? $"Every {a.Profile.Name} session starts its own copy. It's switched on {Describe(servers)}. Turn it off if you don't use it; you can turn it back on here."
                    : config is null
                        ? $"Every {a.Profile.Name} session starts its own copy. It comes from the app itself or a plugin, not from a config file, so it can only be turned off in the app."
                        : $"Every {a.Profile.Name} session starts its own copy. It's configured in {Display(config.Path)} in a way AI RAM Guard doesn't change (comments, or an unusual layout): turn it off there.";
                if (children.Any(c => c.parent == d))
                    detail += $" It also starts {string.Join(", ", children.Where(c => c.parent == d).Select(c => c.d.H.Key))}, which goes with it.";
                var canShare = shareable is { Count: > 0 } && servers.Any(s => Shares.Spec(s, own, shareable) is not null);
                if (canShare)
                    detail = $"Every {a.Profile.Name} session starts its own copy. It can run as one copy for all Claude Code sessions instead: you keep the tool, and the sessions stay apart. Or turn it off if you don't use it. Both can be undone here.";
                list.Add(new Advice(AdviceKind.Duplicates, $"{h.Key} runs {h.Count}× under {a.Profile.Name}", detail,
                    bytes, config?.Path, servers, a.Profile.Id, frees, canShare));
            }

            foreach (var g in Analyzer.GroupHelpers(a.Processes.Where(p => p.CommandLine.Contains("npx-cli.js", StringComparison.OrdinalIgnoreCase))))
            {
                if (g.Count < 2 || g.HeldBytes < MinSavings) continue;
                list.Add(new Advice(AdviceKind.NpxWrapper,
                    $"{g.Count} npx wrappers in front of {g.Key}",
                    $"Each copy starts through npx, an extra process. Install it once ('npm i -g {g.Key}') and point the config at the installed server.",
                    g.HeldBytes, FindConfig(g.Key, own)?.Path, AgentId: a.Profile.Id, Frees: g.Processes));
            }

            var share = (double)a.Bytes / r.Snapshot.TotalRamBytes;
            if (share > 0.2 && caps?.ContainsKey(a.Profile.Id) != true)
                list.Add(new Advice(AdviceKind.BigApp,
                    $"{a.Profile.Name} uses {share * 100:0}% of your RAM",
                    $"Give it a cap: above it, AI RAM Guard frees its idle memory even when your PC has RAM to spare.",
                    0, AgentId: a.Profile.Id));

            if (a.Connections >= ManyConnections)
            {
                // Each process owns its sockets: they can't be pooled, only fewer copies hold fewer. Name who holds them, so the user knows which server to turn off.
                var top = a.Helpers.Where(h => h.Processes.Sum(p => p.Connections) >= 10).OrderByDescending(h => h.Processes.Sum(p => p.Connections)).Take(3)
                    .Select(h => $"{h.Key} ({h.Processes.Sum(p => p.Connections)})").ToList();
                list.Add(new Advice(AdviceKind.Connections,
                    $"{a.Profile.Name} holds {a.Connections} internet connections",
                    $"Spread over {a.Processes.Count} processes" + (top.Count > 0 ? $", most in {string.Join(", ", top)}. Every session opens its own, so fewer copies of those means fewer connections" : "") + $". {PoolWhy} Close the sessions you don't use.",
                    0, AgentId: a.Profile.Id));
            }
        }

        var holders = r.Snapshot.Processes.Where(p => p.Connections > 0).ToList();
        var total = holders.Sum(p => p.Connections);
        if (total >= ManyOnPc)
        {
            var top = holders.GroupBy(p => Path.GetFileNameWithoutExtension(p.Name), StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Sum(p => p.Connections)).Take(3).Select(g => $"{g.Key} ({g.Sum(p => p.Connections)})").ToList();
            list.Add(new Advice(AdviceKind.Connections, $"Your PC holds {total} internet connections",
                $"Most are held by {string.Join(", ", top)}. {PoolWhy} Close what you don't use, or pause its syncing and downloads.", 0));
        }
        // The limit warning first: it is why the other fixes matter today.
        return list.OrderByDescending(x => x.Kind == AdviceKind.Commit).ThenByDescending(x => x.SavingsBytes).ToList();
    }

    /// <summary>What all fixes save together: each process once, although several fixes would close it.</summary>
    public static long TotalSavings(IReadOnlyList<Advice> advice) =>
        advice.SelectMany(a => a.Frees ?? []).DistinctBy(p => p.Pid).Sum(p => p.HeldBytes);

    /// <summary>Above this share of the commit limit, Windows is one big allocation away from refusing memory.</summary>
    const double CommitWarn = 0.85;

    static string Gb(long bytes) => (bytes / 1073741824.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>"as "rea" in ~\.claude.json (for all projects, and again in 2 projects)": the names are what Turn off removes.</summary>
    static string Describe(IReadOnlyList<ServerEntry> servers) => "as " + string.Join(", ", servers.Select(s => $"\"{s.Name}\"").Distinct()) + " in " + string.Join(" and ", servers.GroupBy(s => s.Path).Select(g =>
    {
        var global = g.Any(s => s.Project is null);
        var projects = g.Count(s => s.Project is not null);
        return Display(g.Key) + (global && projects > 0 ? $" (for all projects, and again in {projects} project{(projects > 1 ? "s" : "")})" : projects > 0 ? $" ({projects} project{(projects > 1 ? "s" : "")})" : "");
    }));

    /// <summary>The suggested cap: 15 % of the PC's RAM, whole GB, at least 1.</summary>
    public static int SuggestedCapMB(long totalRamBytes) => Math.Max(1, (int)(totalRamBytes * 0.15 / (1L << 30))) * 1024;

    static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>"~\.claude.json": shorter, and screenshots of the app don't show the user name.</summary>
    public static string Display(string path) =>
        Home.Length > 0 && path.StartsWith(Home, StringComparison.OrdinalIgnoreCase) ? "~" + path[Home.Length..] : path;

    /// <summary>Normal use is a few dozen per app; hundreds is a sync tool, a download or many forgotten sessions.</summary>
    const int ManyConnections = 100, ManyOnPc = 300;

    const string PoolWhy = "On mobile broadband the provider shares a limited pool of connections between your devices: when it runs out, new pages hang although speed tests look fine.";

    /// <summary>Below this a fix isn't worth the user's time.</summary>
    const long MinSavings = 50L << 20;

    static readonly Dictionary<string, string[]> AppsByAgent = new()
    {
        ["claude"] = ["Claude Code", "Claude Desktop"],
        ["codex"] = ["Codex"],
        ["cursor"] = ["Cursor"],
        ["windsurf"] = ["Windsurf"],
        ["gemini"] = ["Gemini CLI"],
        ["vscode"] = ["VS Code"],
    };

    /// <summary>Only the agent's own config files: generic names like "server" would otherwise match another app's config.</summary>
    internal static IReadOnlyList<ConfigFile> ConfigsFor(string agentId, IReadOnlyList<ConfigFile> configs) =>
        AppsByAgent.TryGetValue(agentId, out var apps) ? configs.Where(c => apps.Contains(c.App)).ToList() : configs;

    /// <summary>
    /// The config whose MCP server section starts this helper: "@scope/name" is searched as "name", "rea.mjs" as "rea".
    /// Only server sections count; app configs also hold usage stats and history that mention the same names.
    /// </summary>
    internal static ConfigFile? FindConfig(string helperKey, IReadOnlyList<ConfigFile> configs) =>
        Term(helperKey) is not { } term ? null : configs.FirstOrDefault(c => McpSections(c).Any(s => Mentions(s, term)));

    /// <summary>
    /// What a config says for this helper: "@scope/name" → "name", "rea.mjs" → "rea".
    /// Null when too short or too common to tell servers apart ("index.js" is in every locally built server).
    /// </summary>
    internal static string? Term(string helperKey)
    {
        var term = helperKey.Split('/')[^1];
        var dot = term.LastIndexOf('.');
        if (dot > 0) term = term[..dot];
        return term.Length < 3 || Generic.Contains(term) ? null : term;
    }

    static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    {
        "index", "server", "main", "app", "cli", "mcp", "dist", "run", "start", "launch", "bin", "src", "lib",
        "node", "python", "python3", "py", "uv", "uvx", "npx", "npm", "pnpm", "bun", "deno", "docker", "cmd", "bash", "pwsh", "powershell", "java", "dotnet",
    };

    /// <summary>The term as a whole name or path part: "rea" in "rea.mjs" or "tools/rea", not in "already" or "create".</summary>
    internal static bool Mentions(string text, string term) =>
        System.Text.RegularExpressions.Regex.IsMatch(text, $@"(?<![A-Za-z0-9_-]){System.Text.RegularExpressions.Regex.Escape(term)}(?![A-Za-z0-9_-])", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    static readonly System.Text.RegularExpressions.Regex JsonServers = new(@"""(?:mcpServers|servers)""\s*:\s*\{", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// TOML: the [mcp_servers.*] tables. JSON: every "mcpServers"/"servers" object, matched by braces.
    /// Without the section's own name: a helper called "server.mjs" would otherwise match every one.
    /// </summary>
    internal static IEnumerable<string> McpSections(ConfigFile c)
    {
        if (c.Path.EndsWith(".toml", StringComparison.OrdinalIgnoreCase))
        {
            var inServer = false;
            var sb = new System.Text.StringBuilder();
            foreach (var line in c.Content.Split('\n'))
            {
                var t = line.Trim();
                if (t.StartsWith('[')) inServer = t.StartsWith("[mcp_servers", StringComparison.OrdinalIgnoreCase);
                if (inServer) sb.AppendLine(t.StartsWith('[') ? t["[mcp_servers".Length..] : line);
            }
            yield return sb.ToString();
            yield break;
        }

        foreach (System.Text.RegularExpressions.Match m in JsonServers.Matches(c.Content))
        {
            int depth = 0, i = m.Index + m.Length - 1;
            var inString = false;
            for (; i < c.Content.Length; i++)
            {
                var ch = c.Content[i];
                if (inString) { if (ch == '\\') i++; else if (ch == '"') inString = false; continue; }
                if (ch == '"') inString = true;
                else if (ch == '{') depth++;
                else if (ch == '}' && --depth == 0) break;
            }
            yield return c.Content[(m.Index + m.Length - 1)..Math.Min(i + 1, c.Content.Length)];
        }
    }
}

/// <summary>Where AI apps keep their MCP server lists.</summary>
public static class KnownConfigs
{
    public static IReadOnlyList<ConfigFile> Read()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        (string App, string Path)[] candidates =
        [
            ("Claude Code", Path.Combine(home, ".claude.json")),
            ("Claude Code", Path.Combine(home, ".claude", "settings.json")),
            ("Claude Desktop", Path.Combine(appData, "Claude", "claude_desktop_config.json")),
            .. PluginLists(appData),
            ("Codex", Path.Combine(home, ".codex", "config.toml")),
            ("Cursor", Path.Combine(home, ".cursor", "mcp.json")),
            ("Windsurf", Path.Combine(home, ".codeium", "windsurf", "mcp_config.json")),
            ("Gemini CLI", Path.Combine(home, ".gemini", "settings.json")),
            ("VS Code", Path.Combine(appData, "Code", "User", "mcp.json")),
        ];
        var list = new List<ConfigFile>();
        foreach (var (app, path) in candidates)
        {
            try
            {
                if (!File.Exists(path)) continue;
                // ~/.claude.json holds history and can be megabytes: re-read only when it changed.
                var stamp = File.GetLastWriteTimeUtc(path);
                lock (Cache)
                {
                    if (!Cache.TryGetValue(path, out var c) || c.Stamp != stamp) Cache[path] = c = (stamp, File.ReadAllText(path));
                    list.Add(new ConfigFile(app, path, c.Content));
                }
            }
            catch (IOException) { } // locked by the app right now: skip, advice just won't name the file
            catch (UnauthorizedAccessException) { }
        }
        return list;
    }

    /// <summary>Claude Desktop's plugin lists, one per account: %APPDATA%\Claude\plugin-list\{org}\{account}\plugins.json.</summary>
    static IEnumerable<(string, string)> PluginLists(string appData)
    {
        var dir = Path.Combine(appData, "Claude", "plugin-list");
        try { return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "plugins.json", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2 }).Select(f => ("Claude Desktop", f)).ToList() : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    static readonly Dictionary<string, (DateTime Stamp, string Content)> Cache = [];
}
