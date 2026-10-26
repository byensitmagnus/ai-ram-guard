namespace AiRamGuard.Core;

public enum AdviceKind { Orphans, Duplicates, NpxWrapper, BigApp }

/// <summary>A concrete, permanent fix, with an estimate of what it saves.</summary>
/// <param name="Servers">The config entries that start a duplicated server: when there are any, the app can switch them off itself.</param>
/// <param name="AgentId">The AI app it is about (for a cap).</param>
public sealed record Advice(AdviceKind Kind, string Title, string Detail, long SavingsBytes, string? ConfigPath = null,
    IReadOnlyList<ServerEntry>? Servers = null, string? AgentId = null)
{
    /// <summary>The app can do this one itself; the others need the user (a plugin setting, an npm install).</summary>
    public bool CanFix => Kind is AdviceKind.Orphans or AdviceKind.BigApp || Servers is { Count: > 0 };
}

/// <summary>An AI app's config file, read so advice can point at the exact file to change.</summary>
public sealed record ConfigFile(string App, string Path, string Content);

/// <summary>Turns a report into fixes that stop the waste at the source, instead of cleaning it up forever.</summary>
public static class Advisor
{
    /// <param name="applied">Servers AI RAM Guard switched off: their copies run on until sessions close, which is not a new problem.</param>
    /// <param name="caps">Apps that have a cap already need no cap advice.</param>
    public static IReadOnlyList<Advice> Advise(Report r, IReadOnlyList<ConfigFile> configs, IReadOnlyList<FixRecord>? applied = null, IReadOnlyDictionary<string, int>? caps = null)
    {
        var list = new List<Advice>();

        if (r.Orphans.Count > 0)
        {
            var mb = r.Orphans.Sum(o => o.WorkingSetBytes);
            list.Add(new Advice(AdviceKind.Orphans,
                $"{r.Orphans.Count} leftover AI processes",
                "Their AI session is closed, but they still run. Closing them is safe.", mb));
        }

        foreach (var a in r.Agents)
        {
            var own = ConfigsFor(a.Profile.Id, configs);
            foreach (var h in a.Helpers.Where(h => h.Count >= 3 && h.Bytes - h.Bytes / h.Count >= MinSavings))
            {
                var servers = McpServers.Find(h.Key, own);
                if (servers.Count == 0 && (McpServers.IsOff(h.Key, own) || Term(h.Key) is { } term
                        && (applied ?? []).Any(f => f.Entries.Any(e => e.Definition.Contains(term, StringComparison.OrdinalIgnoreCase)))))
                    continue; // switched off already: the copies go as their sessions close
                var config = FindConfig(h.Key, own);
                var detail = servers.Count > 0
                    ? $"Every {a.Profile.Name} session starts its own copy. It's switched on {Describe(servers)}. Turn it off if you don't use it; you can turn it back on here."
                    : config is null
                        ? $"Every {a.Profile.Name} session starts its own copy. It comes from the app itself or a plugin, not from a config file, so it can only be turned off in the app."
                        : $"Every {a.Profile.Name} session starts its own copy. It's configured in {Display(config.Path)} in a way AI RAM Guard doesn't change (comments, or an unusual layout): turn it off there.";
                list.Add(new Advice(AdviceKind.Duplicates, $"{h.Key} runs {h.Count}× under {a.Profile.Name}", detail,
                    h.Bytes - h.Bytes / h.Count, config?.Path, servers, a.Profile.Id));
            }

            foreach (var g in Analyzer.GroupHelpers(a.Processes.Where(p => p.CommandLine.Contains("npx-cli.js", StringComparison.OrdinalIgnoreCase))))
            {
                if (g.Count < 2 || g.Bytes < MinSavings) continue;
                list.Add(new Advice(AdviceKind.NpxWrapper,
                    $"{g.Count} npx wrappers in front of {g.Key}",
                    $"Each copy starts through npx, an extra process. Install it once ('npm i -g {g.Key}') and point the config at the installed server.",
                    g.Bytes, FindConfig(g.Key, own)?.Path, AgentId: a.Profile.Id));
            }

            var share = (double)a.Bytes / r.Snapshot.TotalRamBytes;
            if (share > 0.2 && caps?.ContainsKey(a.Profile.Id) != true)
                list.Add(new Advice(AdviceKind.BigApp,
                    $"{a.Profile.Name} uses {share * 100:0}% of your RAM",
                    $"Give it a cap: above it, AI RAM Guard frees its idle memory even when your PC has RAM to spare.",
                    0, AgentId: a.Profile.Id));
        }
        return list.OrderByDescending(x => x.SavingsBytes).ToList();
    }

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

    static readonly Dictionary<string, (DateTime Stamp, string Content)> Cache = [];
}
