using System.Diagnostics;
using System.Runtime.InteropServices;
using AiRamGuard.Core;
using Xunit;

/// <summary>Runs against a real child process: lowering and restoring needs no admin rights and must put back exactly what it changed.</summary>
public sealed class PriorityTests : IDisposable
{
    readonly Process _child = Process.Start(new ProcessStartInfo("ping", "-n 60 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;

    /// <summary>This test run stands in for the terminal the child runs in.</summary>
    static readonly ProcessInfo Terminal = new(Environment.ProcessId, 0, "WindowsTerminal.exe", "", 0, Process.GetCurrentProcess().StartTime);

    public PriorityTests()
    {
        Guard.ReadForeground = () => (0, true); // a game in front, no AI app
        // CI runners start processes at low memory priority already, which the guard rightly leaves alone: start from Windows' default.
        var h = OpenProcess(0x0200, false, _child.Id);
        try { var normal = 5u; SetProcessInformation(h, 0, ref normal, 4); }
        finally { CloseHandle(h); }
        _child.PriorityClass = ProcessPriorityClass.Normal;
    }

    ProcessInfo Info(DateTime? created = null) => new(_child.Id, Terminal.Pid, "ping.exe", "", 0, created ?? _child.StartTime);

    static (Report, Plan) Round(IReadOnlyList<ProcessInfo> lower, bool cpu) =>
        (new Report(new Snapshot([.. lower, Terminal], 0, 0), [], []), new Plan([], [], "test") { Lower = lower, LowerCpu = cpu });

    uint MemoryPriority()
    {
        var h = OpenProcess(0x1000, false, _child.Id);
        try { return GetProcessInformation(h, 0, out var v, 4) ? v : 0; }
        finally { CloseHandle(h); }
    }

    ProcessPriorityClass CpuPriority() { _child.Refresh(); return _child.PriorityClass; }

    /// <summary>EcoQoS forced on (control and state bit EXECUTION_SPEED).</summary>
    bool EcoQos()
    {
        var h = OpenProcess(0x1000, false, _child.Id);
        try
        {
            var t = new uint[] { 1, 0, 0 };
            return GetProcessInformation(h, 4, t, 12) && (t[1] & t[2] & 1) != 0;
        }
        finally { CloseHandle(h); }
    }

    [Fact]
    public void Lowers_while_gaming_then_restores_step_by_step()
    {
        var p = Info();
        Guard.Execute(Round([p], cpu: true), prioritize: true);
        Assert.Equal(2u, MemoryPriority());
        Assert.Equal(ProcessPriorityClass.BelowNormal, CpuPriority());
        Assert.True(EcoQos());

        Guard.Execute(Round([p], cpu: false), prioritize: true); // game over, app still in the background
        Assert.Equal(2u, MemoryPriority());
        Assert.Equal(ProcessPriorityClass.Normal, CpuPriority());
        Assert.False(EcoQos());

        Guard.Execute(Round([], cpu: false), prioritize: true); // app came to the front
        Assert.Equal(5u, MemoryPriority());
        Assert.Equal(ProcessPriorityClass.Normal, CpuPriority());
    }

    [Fact]
    public void A_priority_someone_else_chose_is_kept()
    {
        _child.PriorityClass = ProcessPriorityClass.Idle;
        Guard.Execute(Round([Info()], cpu: true), prioritize: true);
        Assert.Equal(ProcessPriorityClass.Idle, CpuPriority());
        Guard.RestorePriorities();
        Assert.Equal(ProcessPriorityClass.Idle, CpuPriority());
        Assert.Equal(5u, MemoryPriority());
    }

    [Fact]
    public void A_reused_PID_is_not_touched()
    {
        Guard.Execute(Round([Info(_child.StartTime.AddMinutes(-5))], cpu: true), prioritize: true);
        Assert.Equal(5u, MemoryPriority());
        Assert.Equal(ProcessPriorityClass.Normal, CpuPriority());
        Guard.RestorePriorities();
    }

    [Fact]
    public void What_is_in_front_when_the_plan_is_applied_wins_over_the_plan()
    {
        Guard.ReadForeground = () => (Terminal.Pid, false); // you switched to its terminal during the scan
        Guard.Execute(Round([Info()], cpu: true), prioritize: true);
        Assert.Equal(5u, MemoryPriority());
        Assert.Equal(ProcessPriorityClass.Normal, CpuPriority());

        Guard.ReadForeground = () => (0, false); // you left the game during the scan
        Guard.Execute(Round([Info()], cpu: true), prioritize: true);
        Assert.Equal(2u, MemoryPriority());
        Assert.Equal(ProcessPriorityClass.Normal, CpuPriority());
        Guard.RestorePriorities();
    }

    [Fact]
    public void Switching_to_its_terminal_restores_at_once()
    {
        Guard.Execute(Round([Info()], cpu: true), prioritize: true);
        Assert.Equal(2u, MemoryPriority());
        Guard.ReadForeground = () => (Terminal.Pid, true);
        Guard.ForegroundChanged();
        Assert.Equal(5u, MemoryPriority());
        Assert.Equal(ProcessPriorityClass.Normal, CpuPriority());
    }

    public void Dispose()
    {
        Guard.RestorePriorities();
        try { _child.Kill(); } catch (InvalidOperationException) { }
        _child.Dispose();
    }

    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool GetProcessInformation(IntPtr h, int cls, out uint info, uint size);
    [DllImport("kernel32.dll")] static extern bool SetProcessInformation(IntPtr h, int cls, ref uint info, uint size);
    [DllImport("kernel32.dll")] static extern bool GetProcessInformation(IntPtr h, int cls, [In, Out] uint[] info, uint size);
}
