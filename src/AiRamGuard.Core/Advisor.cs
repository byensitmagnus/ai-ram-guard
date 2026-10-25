namespace AiRamGuard.Core;

public enum AdviceKind { Orphans, Duplicates, NpxWrapper, BigApp }

/// <summary>A concrete, permanent fix the user can make, with an estimate of what it saves.</summary>
public sealed record Advice(AdviceKind Kind, string Title, string Detail, long SavingsBytes, string? ConfigPath = null);

/// <summary>An AI app's config file, read so advice can point at the exact file to change.</summary>
public sealed record ConfigFile(string App, string Path, string Content);

/// <summary>Turns a report into fixes that stop the waste at the source, instead of cleaning it up forever.</summary>
public static class Advisor
{
    public static IReadOnlyList<Advice> Advise(Report r, IReadOnlyList<ConfigFile> configs)
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
                var config = FindConfig(h.Key, own);
                list.Add(new Advice(AdviceKind.Duplicates,
                    $"{h.Key} runs {h.Count}× under {a.Profile.Name}",
                    $"Every {a.Profile.Name} session starts its own copy. Load it only in the projects that need it, or switch it off when you don't use it. Closing sessions you don't use also helps."
                        + (config is null
                            ? $" It isn't in a config file AI RAM Guard knows, so it probably comes from a plugin or extension: check the plugin list in {a.Profile.Name}."
                            : $" It's configured in {config.App}: {Display(config.Path)}"),
                    h.Bytes - h.Bytes / h.Count,
                    config?.Path));
            }

            foreach (var g in Analyzer.GroupHelpers(a.Processes.Where(p => p.CommandLine.Contains("npx-cli.js", StringComparison.OrdinalIgnoreCase))))
            {
                if (g.Count < 2 || g.Bytes < MinSavings) continue;
                list.Add(new Advice(AdviceKind.NpxWrapper,
                    $"{g.Count} npx wrappers in front of {g.Key}",
                    $"Started as 'npx -y {g.Key}@latest', so every copy runs an extra npx process. Install it once ('npm i -g {g.Key}') and point the config at the installed server to skip the wrapper.",
                    g.Bytes, FindConfig(g.Key, own)?.Path));
            }

            var share = (double)a.Bytes / r.Snapshot.TotalRamBytes;
            if (share > 0.2)
                list.Add(new Advice(AdviceKind.BigApp,
                    $"{a.Profile.Name} uses {share:P0} of your RAM",
                    $"Set a cap for {a.Profile.Name} in Settings. Above it, AI RAM Guard frees its idle memory even when your PC has RAM to spare.",
                    0));
        }
        return list.OrderByDescending(x => x.SavingsBytes).ToList();
    }

    static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>"~\.claude.json": shorter, and screenshots of the app don't show the user name.</summary>
    internal static string Display(string path) =>
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
    internal static ConfigFile? FindConfig(string helperKey, IReadOnlyList<ConfigFile> configs)
    {
        var term = helperKey.Split('/')[^1];
        var dot = term.LastIndexOf('.');
        if (dot > 0) term = term[..dot];
        if (term.Length < 3) return null;
        return configs.FirstOrDefault(c => McpSections(c).Any(s => s.Contains(term, StringComparison.OrdinalIgnoreCase)));
    }

    static readonly System.Text.RegularExpressions.Regex JsonServers = new(@"""(?:mcpServers|servers)""\s*:\s*\{", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>TOML: the [mcp_servers.*] tables. JSON: every "mcpServers"/"servers" object, matched by braces.</summary>
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
                if (inServer) sb.AppendLine(line);
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
            yield return c.Content[m.Index..Math.Min(i + 1, c.Content.Length)];
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
