using AiRamGuard.Core;
using Xunit;

public class SessionsTests
{
    static readonly DateTime Now = new(2026, 11, 4, 12, 0, 0);
    const string Old = "24e2bb86-a433-45ea-8664-1d3d3a58c451", Fresh = "44fbcd70-61ab-456a-877e-3db80d78e2c2";

    static ProcessInfo Session(int pid, string id, double startedHoursAgo) =>
        new(pid, 1, "claude.exe", $"C:\\Claude\\claude-code\\2.1.309\\claude.exe --output-format stream-json --verbose --input-format stream-json --model=x --resume={id}", 100 << 20, Now.AddHours(-startedHoursAgo));

    static ProcessInfo Child(int pid, int parent, double startedHoursAgo) => new(pid, parent, "node.exe", "node server.js", 50 << 20, Now.AddHours(-startedHoursAgo));

    static IReadOnlyList<IdleSession> Find(IReadOnlyList<ProcessInfo> procs, Dictionary<string, DateTime> writes) =>
        Sessions.FindIdle(new Snapshot(procs, 0, 0), TimeSpan.FromHours(2), id => writes.TryGetValue(id, out var w) ? w : null, Now);

    [Fact]
    public void A_session_nobody_wrote_in_for_hours_is_closed_with_the_servers_it_started_and_nothing_else()
    {
        var procs = new[]
        {
            Session(10, Old, 25), Child(11, 10, 25), Child(12, 11, 25),
            Session(20, Fresh, 25), Child(21, 20, 25),
            Child(30, 1, 25),
            Child(13, 10, 30), // PID 10's old number was reused: started before it, so not its child
        };
        var idle = Assert.Single(Find(procs, new() { [Old] = Now.AddHours(-13), [Fresh] = Now.AddMinutes(-5) }));
        Assert.Equal(Old, idle.Id);
        Assert.Equal([10, 11, 12], idle.Processes.Select(p => p.Pid));
        Assert.Equal(13, Math.Round(idle.Idle.TotalHours));
    }

    [Fact]
    public void A_session_reopened_after_its_last_message_counts_from_its_start()
    {
        Assert.Empty(Find([Session(10, Old, 1)], new() { [Old] = Now.AddHours(-20) }));
    }

    [Fact]
    public void Without_a_transcript_or_outside_the_desktop_app_nothing_is_closed()
    {
        var terminal = new ProcessInfo(40, 1, "claude.exe", $"claude --resume {Old}", 100 << 20, Now.AddHours(-30));
        Assert.Empty(Find([Session(10, Old, 30), terminal], []));
        Assert.Empty(Find([terminal], new() { [Old] = Now.AddHours(-20) }));
    }
}
