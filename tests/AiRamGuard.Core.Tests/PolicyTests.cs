using AiRamGuard.Core;
using Xunit;

public class PolicyTests
{
    const long MB = 1024 * 1024;
    static readonly AgentProfile Claude = new() { Id = "claude", Name = "Claude", ProcessNames = ["claude"] };
    static readonly AgentProfile Codex = new() { Id = "codex", Name = "Codex", ProcessNames = ["codex"] };
    static readonly ProcessInfo A = new(1, 0, "claude.exe", "", 500 * MB, DateTime.Now);
    static readonly ProcessInfo B = new(2, 1, "node.exe", "x mcp", 200 * MB, DateTime.Now);
    static readonly ProcessInfo C = new(3, 0, "codex.exe", "", 4000 * MB, DateTime.Now);
    static readonly ProcessInfo Orphan = new(9, 8, "node.exe", "x mcp", 200 * MB, DateTime.Now);

    static Report R(long freeMB) => new(new Snapshot([A, B, C, Orphan], 32_000 * MB, freeMB * MB),
        [new AgentUsage(Codex, [C], []), new AgentUsage(Claude, [A, B], [])], [Orphan]);

    [Fact]
    public void Plenty_of_RAM_means_no_trim_but_orphans_still_close()
    {
        var plan = Policy.Decide(R(12_000), new GuardSettings(), foregroundPid: 0, fullscreenGame: false);
        Assert.Empty(plan.Trim);
        Assert.Single(plan.Kill);
    }

    [Fact]
    public void Low_RAM_trims_AI_apps_but_never_the_app_you_are_using()
    {
        // The foreground window is Claude's main process: all of Claude is spared, not just that one PID.
        var plan = Policy.Decide(R(3_000), new GuardSettings(), foregroundPid: 1, fullscreenGame: false);
        Assert.Equal([3], plan.Trim.Select(p => p.Pid));
    }

    [Theory]
    [InlineData("WindowsTerminal.exe", 0)]
    [InlineData("explorer.exe", 1)]
    public void A_cli_agent_in_the_terminal_in_front_is_not_trimmed(string host, int expectedTrimmed)
    {
        var t = DateTime.Now;
        var term = new ProcessInfo(20, 0, host, "", 100 * MB, t);
        var cli = new ProcessInfo(21, 20, "claude.exe", "", 300 * MB, t);
        var r = new Report(new Snapshot([term, cli], 32_000 * MB, 1_000 * MB), [new AgentUsage(Claude, [cli], [])], []);
        Assert.Equal(expectedTrimmed, Policy.Decide(r, new GuardSettings(), foregroundPid: 20, fullscreenGame: false).Trim.Count);
    }

    [Fact]
    public void Game_mode_uses_the_stricter_limit()
    {
        var s = new GuardSettings { MinFreeMB = 6144, GameModeMinFreeMB = 10240 };
        Assert.Empty(Policy.Decide(R(8_000), s, 0, fullscreenGame: false).Trim);
        Assert.Equal(3, Policy.Decide(R(8_000), s, 0, fullscreenGame: true).Trim.Count);
        s.GameMode = false;
        Assert.Empty(Policy.Decide(R(8_000), s, 0, fullscreenGame: true).Trim);
    }

    [Fact]
    public void An_app_over_its_cap_is_trimmed_even_with_free_RAM()
    {
        var s = new GuardSettings { AgentCapsMB = { ["codex"] = 3000, ["claude"] = 3000 } };
        var plan = Policy.Decide(R(20_000), s, 0, false);
        Assert.Equal([3], plan.Trim.Select(p => p.Pid));
        Assert.Contains("Codex", plan.Reason);
    }

    [Fact]
    public void Pause_stops_automatic_rounds_but_not_a_manual_clean()
    {
        var now = new DateTime(2026, 10, 25, 12, 0, 0);
        var s = new GuardSettings { PausedUntil = now.AddHours(1) };
        var auto = Policy.Decide(R(1_000), s, 0, false, now: now);
        Assert.Empty(auto.Kill);
        Assert.Empty(auto.Trim);
        Assert.NotEmpty(Policy.Decide(R(1_000), s, 0, false, force: true, now: now).Trim);
        Assert.NotEmpty(Policy.Decide(R(1_000), s, 0, false, now: now.AddHours(2)).Trim);
    }

    [Fact]
    public void Switches_turn_actions_off()
    {
        var s = new GuardSettings { KillOrphans = false, Trim = false };
        var plan = Policy.Decide(R(1_000), s, 0, false, force: true);
        Assert.Empty(plan.Kill);
        Assert.Empty(plan.Trim);
    }

    [Fact]
    public void Background_AI_apps_get_lower_priority_and_less_CPU_only_while_gaming()
    {
        // Plenty of RAM: nothing to trim, but Claude in front keeps its priority while Codex is lowered.
        var plan = Policy.Decide(R(20_000), new GuardSettings(), foregroundPid: 1, fullscreenGame: false);
        Assert.Equal([3], plan.Lower.Select(p => p.Pid));
        Assert.False(plan.LowerCpu);
        Assert.True(Policy.Decide(R(20_000), new GuardSettings(), 0, fullscreenGame: true).LowerCpu);
        Assert.False(Policy.Decide(R(20_000), new GuardSettings { GameMode = false }, 0, fullscreenGame: true).LowerCpu);
    }

    [Fact]
    public void Priority_is_left_alone_when_switched_off_or_paused()
    {
        var now = new DateTime(2026, 10, 25, 12, 0, 0);
        var off = Policy.Decide(R(1_000), new GuardSettings { LowerPriority = false }, 0, fullscreenGame: true);
        Assert.Empty(off.Lower);
        Assert.False(off.LowerCpu);
        // A manual clean while paused frees RAM, but must not start lowering again.
        var paused = Policy.Decide(R(1_000), new GuardSettings { PausedUntil = now.AddHours(1) }, 0, true, force: true, now: now);
        Assert.NotEmpty(paused.Trim);
        Assert.Empty(paused.Lower);
        Assert.False(paused.LowerCpu);
    }

    [Fact]
    public void A_bad_pattern_fails_when_the_file_is_read_not_mid_round() =>
        Assert.ThrowsAny<ArgumentException>(() => ProfileSet.Parse("""{ "helperPatterns": ["(unclosed"] }"""));

    [Fact]
    public void Built_in_profiles_load_and_every_pattern_compiles()
    {
        var p = ProfileSet.Load();
        Assert.Contains(p.Agents, a => a.Id == "claude");
        Assert.All(p.Agents, a => Assert.False(string.IsNullOrWhiteSpace(a.Name)));
        Assert.True(p.IsHelper(B));
        Assert.False(p.IsHelper(A));
    }
}
