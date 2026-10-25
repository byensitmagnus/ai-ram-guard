using AiRamGuard.Core;
using Xunit;

public class PolicyTests
{
    const long MB = 1024 * 1024;
    static readonly AgentProfile Claude = new() { Id = "claude", Name = "Claude", ProcessNames = ["claude"] };
    static readonly ProcessInfo A = new(1, 0, "claude.exe", "", 500 * MB, DateTime.Now);
    static readonly ProcessInfo B = new(2, 1, "node.exe", "x mcp", 200 * MB, DateTime.Now);
    static readonly ProcessInfo Orphan = new(9, 8, "node.exe", "x mcp", 200 * MB, DateTime.Now);

    static Report R(long freeMB) => new(new Snapshot([A, B, Orphan], 32_000 * MB, freeMB * MB),
        [new AgentUsage(Claude, [A, B], [])], [Orphan]);

    [Fact]
    public void Plenty_of_RAM_means_no_trim_but_orphans_still_close()
    {
        var plan = Policy.Decide(R(12_000), new GuardSettings(), foregroundPid: 0, fullscreenGame: false);
        Assert.Empty(plan.Trim);
        Assert.Single(plan.Kill);
    }

    [Fact]
    public void Low_RAM_trims_every_AI_process_except_the_foreground_window()
    {
        var plan = Policy.Decide(R(3_000), new GuardSettings(), foregroundPid: 1, fullscreenGame: false);
        Assert.Equal([2], plan.Trim.Select(p => p.Pid));
    }

    [Fact]
    public void Game_mode_uses_the_stricter_limit()
    {
        var s = new GuardSettings { MinFreeMB = 6144, GameModeMinFreeMB = 10240 };
        Assert.Empty(Policy.Decide(R(8_000), s, 0, fullscreenGame: false).Trim);
        Assert.Equal(2, Policy.Decide(R(8_000), s, 0, fullscreenGame: true).Trim.Count);
        s.GameMode = false;
        Assert.Empty(Policy.Decide(R(8_000), s, 0, fullscreenGame: true).Trim);
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
    public void Built_in_profiles_load_and_every_pattern_compiles()
    {
        var p = ProfileSet.Load();
        Assert.Contains(p.Agents, a => a.Id == "claude");
        Assert.All(p.Agents, a => Assert.False(string.IsNullOrWhiteSpace(a.Name)));
        Assert.True(p.IsHelper(B));
        Assert.False(p.IsHelper(A));
    }
}
