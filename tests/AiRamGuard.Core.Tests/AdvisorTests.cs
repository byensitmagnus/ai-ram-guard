using AiRamGuard.Core;
using Xunit;

public class AdvisorTests
{
    const long MB = 1024 * 1024;
    static readonly AgentProfile Claude = new() { Id = "claude", Name = "Claude", ProcessNames = ["claude"] };
    const string Npx = "node C:\\nodejs\\node_modules\\npm\\bin\\npx-cli.js -y @wonderwhy-er/desktop-commander@latest";
    const string Server = "node D:\\_npx\\node_modules\\@wonderwhy-er\\desktop-commander\\dist\\index.js";

    static ProcessInfo P(int pid, string cmd, long mb = 100) => new(pid, 1, "node.exe", cmd, mb * MB, DateTime.Now);

    static Report Build(IReadOnlyList<ProcessInfo> claudeProcs, IReadOnlyList<ProcessInfo>? orphans = null, long totalMB = 32_000)
    {
        var helpers = Analyzer.GroupHelpers(claudeProcs.Where(p => p.CommandLine.Length > 0));
        return new Report(new Snapshot(claudeProcs, totalMB * MB, 8_000 * MB), [new AgentUsage(Claude, claudeProcs, helpers)], orphans ?? []);
    }

    [Fact]
    public void Duplicates_point_at_the_config_file_that_loads_them_and_count_savings_beyond_one_copy()
    {
        var procs = Enumerable.Range(10, 4).Select(i => P(i, Server, 100)).ToList();
        var configs = new[]
        {
            new ConfigFile("Codex", "C:\\codex.toml", "[mcp_servers.other]\ncommand = \"x\""),
            new ConfigFile("Claude Code", "C:\\.claude.json", "{\"mcpServers\":{\"dc\":{\"args\":[\"@wonderwhy-er/desktop-commander\"]}}}"),
        };
        var dup = Assert.Single(Advisor.Advise(Build(procs), configs), a => a.Kind == AdviceKind.Duplicates);
        Assert.Equal("C:\\.claude.json", dup.ConfigPath);
        Assert.Equal(300 * MB, dup.SavingsBytes);
        Assert.Contains("4×", dup.Title);
    }

    [Fact]
    public void Npx_wrappers_get_their_own_advice()
    {
        var procs = new[] { P(1, Npx, 90), P(2, Npx, 90), P(3, Server), P(4, Server) };
        var npx = Assert.Single(Advisor.Advise(Build(procs), []), a => a.Kind == AdviceKind.NpxWrapper);
        Assert.Equal(180 * MB, npx.SavingsBytes);
        Assert.Contains("npm i -g @wonderwhy-er/desktop-commander", npx.Detail);
    }

    [Fact]
    public void Orphans_come_first_when_they_save_the_most()
    {
        var advice = Advisor.Advise(Build([P(1, Server, 50)], orphans: [P(9, Server, 900)]), []);
        Assert.Equal(AdviceKind.Orphans, advice[0].Kind);
    }

    [Fact]
    public void A_single_copy_is_not_flagged()
    {
        Assert.Empty(Advisor.Advise(Build([P(1, Server, 50), P(2, Server, 50)]), []));
    }

    [Fact]
    public void An_app_using_a_big_share_suggests_a_cap()
    {
        var advice = Advisor.Advise(Build([P(1, "", 9_000)], totalMB: 32_000), []);
        Assert.Single(advice, a => a.Kind == AdviceKind.BigApp);
    }

    [Theory]
    [InlineData("@wonderwhy-er/desktop-commander", "desktop-commander")]
    [InlineData("rea-agents", "rea-agents")]
    [InlineData("tools/rea.mjs", "rea")]
    public void Config_search_uses_the_package_tail(string key, string inConfig) =>
        Assert.NotNull(Advisor.FindConfig(key, [new ConfigFile("x", "p.json", $"{{\"mcpServers\": {{\"a\": {{\"args\": [\"{inConfig}\"]}}}}}}")]));

    [Fact]
    public void A_generic_helper_name_does_not_match_the_section_name()
    {
        // Codex' own app-tools server runs as "./server.mjs"; "mcp_servers" and "mcpServers" must not count as a match.
        Assert.Null(Advisor.FindConfig("server.mjs", [new ConfigFile("Codex", "C:\\config.toml", "[mcp_servers.node_repl]\ncommand = \"node_repl.exe\"\n")]));
        Assert.Null(Advisor.FindConfig("server.mjs", [new ConfigFile("Claude Code", "C:\\.claude.json", """{ "mcpServers": { "a": { "args": ["x.js"] } } }""")]));
    }

    [Fact]
    public void A_common_name_or_part_of_a_word_never_picks_a_server_to_turn_off()
    {
        // "index.js" is in every locally built server; "rea" is inside "already" and "create".
        var cfg = new ConfigFile("Claude Code", "C:\\.claude.json", """{ "mcpServers": { "a": { "args": ["D:\\x\\dist\\index.js"] }, "b": { "args": ["already-create"] } } }""");
        Assert.Empty(McpServers.Find("index.js", [cfg]));
        Assert.Empty(McpServers.Find("rea.mjs", [cfg]));
        Assert.Single(McpServers.Find("rea.mjs", [cfg with { Content = cfg.Content.Replace("already-create", "tools/rea.mjs") }]));
    }

    [Fact]
    public void Advice_only_points_at_the_apps_own_config()
    {
        var claudeCfg = new ConfigFile("Claude Code", "C:\\.claude.json", """{ "mcpServers": { "s": { "args": ["server.mjs"] } } }""");
        var codexCfg = new ConfigFile("Codex", "C:\\config.toml", "[mcp_servers.app]\nargs = [\"./server.mjs\"]\n");
        Assert.Equal([codexCfg], Advisor.ConfigsFor("codex", [claudeCfg, codexCfg]));
        Assert.Equal(2, Advisor.ConfigsFor("unknown-agent", [claudeCfg, codexCfg]).Count);
    }

    [Fact]
    public void Tiny_duplicates_are_not_worth_a_recommendation()
    {
        var procs = Enumerable.Range(10, 5).Select(i => P(i, Server, 5)).ToList();
        Assert.DoesNotContain(Advisor.Advise(Build(procs), []), a => a.Kind == AdviceKind.Duplicates);
    }

    [Fact]
    public void Names_outside_server_sections_do_not_count()
    {
        // .claude.json keeps plugin usage stats that mention server names; that's not where the server is configured.
        var json = """{ "pluginUsage": { "desktop-commander@inline": { "usageCount": 2238 } }, "mcpServers": { "rea": { "args": ["rea-agents/rea.mjs"] } } }""";
        var cfg = new ConfigFile("Claude Code", "C:\\.claude.json", json);
        Assert.Null(Advisor.FindConfig("@wonderwhy-er/desktop-commander", [cfg]));
        Assert.NotNull(Advisor.FindConfig("rea-agents", [cfg]));
    }

    [Fact]
    public void Toml_only_searches_mcp_server_tables()
    {
        var toml = "[projects.'c:\\rea-agents-notes']\ntrust_level = \"trusted\"\n\n[mcp_servers.rea]\nargs = [\"rea-agents\\\\scripts\\\\rea.mjs\"]\n";
        var cfg = new ConfigFile("Codex", "C:\\config.toml", toml);
        Assert.NotNull(Advisor.FindConfig("rea-agents", [cfg]));
        Assert.Null(Advisor.FindConfig("rea-agents", [cfg with { Content = "[projects.'c:\\rea-agents']\n" }]));
    }
}

public class HistoryTests
{
    [Fact]
    public void Keeps_only_the_last_24_hours_and_survives_a_round_trip()
    {
        var t = new DateTime(2026, 10, 25, 0, 0, 0);
        var h = new History();
        for (var i = 0; i < 30; i++) h.Add(new Sample(t.AddHours(i), 1000 + i, 500, 32000));
        Assert.True(h.Samples[0].Time >= t.AddHours(29) - History.Window);

        var file = Path.Combine(Path.GetTempPath(), $"airg-history-{Guid.NewGuid():N}.json");
        try
        {
            h.Save(file);
            Assert.Equal(h.Samples.Count, History.Load(file).Samples.Count);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void A_damaged_file_gives_an_empty_history_instead_of_a_crash()
    {
        var file = Path.Combine(Path.GetTempPath(), $"airg-history-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, "{ not json");
        try { Assert.Empty(History.Load(file).Samples); }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Stats_count_what_the_guard_freed_not_the_system_wide_change()
    {
        // Free RAM fell (another app grew), but the guard itself freed 300 MB.
        var s = new Stats();
        s.Record(new CleanResult(2, 5, 5L << 30, 4L << 30, "x", 300L << 20));
        Assert.Equal(300, s.FreedMB);
        Assert.Equal(2, s.OrphansClosed);
    }
}
