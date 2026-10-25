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
        var r = Analyze(P(2, 1, "cmd.exe", Wrapper), P(3, 2, "node.exe", Npx), P(4, 3, "node.exe", Server));
        Assert.Equal(3, r.Orphans.Count);
        Assert.Empty(r.Agents);
    }

    [Fact]
    public void A_reused_parent_pid_still_counts_as_dead()
    {
        // PID 1 now belongs to a newer, unrelated process.
        var r = Analyze(P(1, 0, "notepad.exe", minute: 30), P(3, 1, "node.exe", Npx, minute: 5));
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

    [Fact]
    public void Cli_agents_are_recognised_by_command_line()
    {
        var r = Analyze(P(1, 0, "pwsh.exe", minute: 0), P(2, 1, "node.exe", "node C:\\npm\\node_modules\\@google\\gemini-cli\\dist\\index.js"));
        Assert.Equal("gemini", Assert.Single(r.Agents).Profile.Id);
    }

    [Theory]
    [InlineData(Npx, "@wonderwhy-er/desktop-commander")]
    [InlineData(Server, "@wonderwhy-er/desktop-commander")]
    [InlineData("\"C:\\Program Files\\nodejs\\node.exe\" C:\\Users\\u\\AppData\\Roaming\\npm\\node_modules\\rea-agents\\scripts\\rea.mjs mcp", "rea-agents")]
    [InlineData("node ./server.mjs", "server.mjs")]
    [InlineData("\"C:\\Tools\\node_repl.exe\" --flag", "node_repl.exe")]
    [InlineData("uvx mcp-server-fetch", "uvx")]
    public void HelperKey_gives_a_short_readable_name(string cmd, string expected) =>
        Assert.Equal(expected, Analyzer.HelperKey(cmd));
}
