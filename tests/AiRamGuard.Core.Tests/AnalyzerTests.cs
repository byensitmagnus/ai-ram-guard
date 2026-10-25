using AiRamGuard.Core;
using Xunit;

public class AnalyzerTests
{
    static readonly DateTime T0 = new(2026, 10, 25, 10, 0, 0);
    const long MB = 1024 * 1024;
    static readonly ProfileSet Profiles = ProfileSet.Load();

    static ProcessInfo P(int pid, int parent, string name, string cmd = "", long mb = 100, int minute = 1) =>
        new(pid, parent, name, cmd, mb * MB, T0.AddMinutes(minute));

    static Report Analyze(params ProcessInfo[] ps) => Analyzer.Analyze(new Snapshot(ps, 32_000 * MB, 4_000 * MB), Profiles);

    /// <summary>Two scans: first with the agent alive (so its helpers are remembered), then without the processes in <paramref name="gone"/>.</summary>
    static Report AfterExit(ProcessInfo[] before, params int[] gone)
    {
        var lineage = new Lineage();
        Analyzer.Analyze(new Snapshot(before, 32_000 * MB, 4_000 * MB), Profiles, lineage);
        return Analyzer.Analyze(new Snapshot(before.Where(p => !gone.Contains(p.Pid)).ToList(), 32_000 * MB, 4_000 * MB), Profiles, lineage);
    }

    const string Npx = "\"C:\\Program Files\\nodejs\\node.exe\" \"C:\\Program Files\\nodejs\\node_modules\\npm\\bin\\npx-cli.js\" -y @wonderwhy-er/desktop-commander@latest";
    const string Server = "node D:\\npm\\_npx\\4b4c\\node_modules\\@wonderwhy-er\\desktop-commander\\dist\\index.js";
    const string Wrapper = "C:\\WINDOWS\\system32\\cmd.exe /d /s /c npx -y @wonderwhy-er/desktop-commander@latest";

    [Fact]
    public void Helpers_under_a_live_agent_belong_to_that_agent_and_are_grouped()
    {
        var r = Analyze(
            P(1, 0, "claude.exe", minute: 0),
            P(2, 1, "cmd.exe", Wrapper), P(3, 2, "node.exe", Npx), P(4, 3, "node.exe", Server),
            P(5, 1, "cmd.exe", Wrapper), P(6, 5, "node.exe", Npx), P(7, 6, "node.exe", Server));

        var claude = Assert.Single(r.Agents);
        Assert.Equal("claude", claude.Profile.Id);
        Assert.Equal(7, claude.Processes.Count);
        var dc = Assert.Single(claude.Helpers);
        Assert.Equal("@wonderwhy-er/desktop-commander", dc.Key);
        Assert.Equal(6, dc.Count);
        Assert.Empty(r.Orphans);
    }

    [Fact]
    public void Helpers_whose_agent_died_are_orphans()
    {
        var r = AfterExit([P(1, 0, "claude.exe", minute: 0), P(2, 1, "cmd.exe", Wrapper), P(3, 2, "node.exe", Npx), P(4, 3, "node.exe", Server)], gone: 1);
        Assert.Equal(3, r.Orphans.Count);
        Assert.Empty(r.Agents);
    }

    [Fact]
    public void A_reused_parent_pid_still_counts_as_dead()
    {
        // PID 1 now belongs to a newer, unrelated process.
        var lineage = new Lineage();
        Analyzer.Analyze(new Snapshot([P(1, 0, "claude.exe", minute: 0), P(3, 1, "node.exe", Npx, minute: 5)], 32_000 * MB, 4_000 * MB), Profiles, lineage);
        var r = Analyzer.Analyze(new Snapshot([P(1, 0, "notepad.exe", minute: 30), P(3, 1, "node.exe", Npx, minute: 5)], 32_000 * MB, 4_000 * MB), Profiles, lineage);
        Assert.Single(r.Orphans);
    }

    [Fact]
    public void A_server_started_from_a_live_terminal_is_left_alone()
    {
        var r = Analyze(P(1, 0, "WindowsTerminal.exe", minute: 0), P(2, 1, "pwsh.exe"), P(3, 2, "node.exe", Npx));
        Assert.Empty(r.Orphans);
        Assert.Empty(r.Agents);
    }

    [Fact]
    public void Non_helper_processes_are_never_orphans()
    {
        var r = Analyze(P(3, 999, "node.exe", "node my-own-app.js"));
        Assert.Empty(r.Orphans);
    }

    [Theory]
    [InlineData("node server.mjs")]
    [InlineData("\"C:\\Program Files\\nodejs\\node.exe\" \"C:\\Program Files\\nodejs\\node_modules\\npm\\bin\\npx-cli.js\" vite")]
    [InlineData("node C:\\dev\\my-mcp\\index.js --mcp-config x")]
    public void A_dead_parent_alone_never_makes_an_orphan(string cmd)
    {
        // Started through a launcher, `start` or Task Scheduler: parent gone, but never seen under an AI app.
        Assert.Empty(Analyze(P(3, 999, "node.exe", cmd)).Orphans);
    }

    [Fact]
    public void A_dev_server_an_agent_ran_in_a_shell_survives_the_agent()
    {
        var r = AfterExit([P(1, 0, "claude.exe", minute: 0), P(2, 1, "pwsh.exe"), P(3, 2, "node.exe", "node server.mjs")], gone: [1, 2]);
        Assert.Empty(r.Orphans);
    }

    [Fact]
    public void An_interactive_cli_agent_is_never_an_orphan()
    {
        var gemini = "node C:\\npm\\node_modules\\@google\\gemini-cli\\dist\\index.js";
        var r = AfterExit([P(1, 0, "pwsh.exe", minute: 0), P(2, 1, "node.exe", gemini)], gone: 1);
        Assert.Empty(r.Orphans);
        Assert.Equal("gemini", Assert.Single(r.Agents).Profile.Id);
    }

    [Fact]
    public void An_editor_open_on_an_agent_path_is_not_that_agent()
    {
        Assert.Empty(Analyze(P(1, 0, "notepad.exe", "notepad C:\\npm\\node_modules\\@google\\gemini-cli\\README.md")).Agents);
    }

    [Fact]
    public void Cli_agents_are_recognised_by_command_line()
    {
        var r = Analyze(P(1, 0, "pwsh.exe", minute: 0), P(2, 1, "node.exe", "node C:\\npm\\node_modules\\@google\\gemini-cli\\dist\\index.js"));
        Assert.Equal("gemini", Assert.Single(r.Agents).Profile.Id);
    }

    [Fact]
    public void A_bin_shim_and_its_scoped_package_count_as_one_server()
    {
        var groups = Analyzer.GroupHelpers([P(1, 0, "node.exe", Npx), P(2, 0, "cmd.exe", "cmd.exe /d /s /c desktop-commander"), P(3, 0, "node.exe", Server)]);
        var g = Assert.Single(groups);
        Assert.Equal("@wonderwhy-er/desktop-commander", g.Key);
        Assert.Equal(3, g.Count);
    }

    [Theory]
    [InlineData(Npx, "@wonderwhy-er/desktop-commander")]
    [InlineData(Server, "@wonderwhy-er/desktop-commander")]
    [InlineData("\"C:\\Program Files\\nodejs\\node.exe\" C:\\Users\\u\\AppData\\Roaming\\npm\\node_modules\\rea-agents\\scripts\\rea.mjs mcp", "rea-agents")]
    [InlineData("node ./server.mjs", "server.mjs")]
    [InlineData("\"C:\\Tools\\node_repl.exe\" --flag", "node_repl.exe")]
    [InlineData("uvx mcp-server-fetch", "uvx")]
    [InlineData("C:\\WINDOWS\\system32\\cmd.exe /d /s /c desktop-commander", "desktop-commander")]
    [InlineData("C:\\WINDOWS\\system32\\cmd.exe /d /s /c \"npx ^\"-y^\" ^\"@wonderwhy-er/desktop-commander@latest^\"\"", "@wonderwhy-er/desktop-commander")]
    [InlineData("\"c:\\windows\\system32\\cmd.exe\" /d /s /c call ./scripts/launch_codex_app_tools_mcp.cmd ./server.mjs", "server.mjs")]
    public void HelperKey_gives_a_short_readable_name(string cmd, string expected) =>
        Assert.Equal(expected, Analyzer.HelperKey(cmd));
}
