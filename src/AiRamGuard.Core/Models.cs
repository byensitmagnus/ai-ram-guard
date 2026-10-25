namespace AiRamGuard.Core;

/// <summary>One process at the moment of a snapshot.</summary>
public sealed record ProcessInfo(
    int Pid,
    int ParentPid,
    string Name,
    string CommandLine,
    long WorkingSetBytes,
    DateTime Created,
    string ExecutablePath = "");

/// <summary>A point-in-time view of the machine.</summary>
public sealed record Snapshot(
    IReadOnlyList<ProcessInfo> Processes,
    long TotalRamBytes,
    long FreeRamBytes);

/// <summary>Several copies of the same helper (usually an MCP server).</summary>
public sealed record HelperGroup(string Key, IReadOnlyList<ProcessInfo> Processes)
{
    public int Count => Processes.Count;
    public long Bytes => Processes.Sum(p => p.WorkingSetBytes);
}

public sealed record AgentUsage(AgentProfile Profile, IReadOnlyList<ProcessInfo> Processes, IReadOnlyList<HelperGroup> Helpers)
{
    public long Bytes => Processes.Sum(p => p.WorkingSetBytes);

    /// <summary>Path of the app's own executable, for its icon. Empty for CLI agents.</summary>
    public string MainExecutable => Processes.FirstOrDefault(p => Profile.OwnsProcessName(p.Name))?.ExecutablePath ?? "";
}

public sealed record Report(
    Snapshot Snapshot,
    IReadOnlyList<AgentUsage> Agents,
    IReadOnlyList<ProcessInfo> Orphans)
{
    public long AiBytes => Agents.Sum(a => a.Bytes) + Orphans.Sum(o => o.WorkingSetBytes);
}
