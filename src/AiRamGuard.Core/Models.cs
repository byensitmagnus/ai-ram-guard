namespace AiRamGuard.Core;

/// <summary>One process at the moment of a snapshot.</summary>
public sealed record ProcessInfo(
    int Pid,
    int ParentPid,
    string Name,
    string CommandLine,
    long WorkingSetBytes,
    DateTime Created,
    string ExecutablePath = "",
    int Connections = 0,
    long PrivateBytes = 0)
{
    /// <summary>
    /// What closing it gives back: its own memory, in RAM or in the pagefile. The working set alone says little
    /// for a process the guard trimmed: an 80 MB server shows as 5 MB and still holds 80.
    /// </summary>
    public long HeldBytes => Math.Max(PrivateBytes, WorkingSetBytes);
}

/// <summary>A point-in-time view of the machine.</summary>
/// <param name="CommitBytes">Memory Windows has promised to programs, RAM and pagefile together. 0 when unknown.</param>
/// <param name="CommitLimitBytes">All it can promise. At the limit programs crash or won't start, however much RAM is free.</param>
public sealed record Snapshot(
    IReadOnlyList<ProcessInfo> Processes,
    long TotalRamBytes,
    long FreeRamBytes,
    long CommitBytes = 0,
    long CommitLimitBytes = 0);

/// <summary>Several copies of the same helper (usually an MCP server).</summary>
public sealed record HelperGroup(string Key, IReadOnlyList<ProcessInfo> Processes)
{
    public int Count => Processes.Count;
    public long Bytes => Processes.Sum(p => p.WorkingSetBytes);
    public long HeldBytes => Processes.Sum(p => p.HeldBytes);
}

public sealed record AgentUsage(AgentProfile Profile, IReadOnlyList<ProcessInfo> Processes, IReadOnlyList<HelperGroup> Helpers)
{
    public long Bytes => Processes.Sum(p => p.WorkingSetBytes);
    public long HeldBytes => Processes.Sum(p => p.HeldBytes);

    /// <summary>Open internet connections, see <see cref="Connections.ByPid"/>.</summary>
    public int Connections => Processes.Sum(p => p.Connections);

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
