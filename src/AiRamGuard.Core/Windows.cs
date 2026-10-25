using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;

namespace AiRamGuard.Core;

public static class Paths
{
    public static string ConfigDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AiRamGuard");
    public static string DataDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AiRamGuard");
    public static string LogFile { get; internal set; } = Path.Combine(DataDir, "guard.log"); // tests point it elsewhere
    public static string HistoryFile { get; } = Path.Combine(DataDir, "history.json");
    public static string StatsFile { get; } = Path.Combine(DataDir, "stats.json");
    public static string LineageFile { get; } = Path.Combine(DataDir, "lineage.json");
    /// <summary>Undo records for moved caches. In ConfigDir with the settings: losing it would lose the way back.</summary>
    public static string MovesFile { get; } = Path.Combine(ConfigDir, "moves.json");
}

/// <summary>Reads processes and memory from Windows.</summary>
public static class SnapshotReader
{
    public static Snapshot Read()
    {
        var list = new List<ProcessInfo>();
        using (var search = new ManagementObjectSearcher(
            "SELECT ProcessId, ParentProcessId, Name, CommandLine, WorkingSetSize, CreationDate, ExecutablePath FROM Win32_Process"))
        using (var results = search.Get())
        {
            foreach (ManagementObject o in results)
            {
                using (o)
                {
                    var created = DateTime.MinValue; // unknown start time: never killed (TryKill can't match it)
                    try { if (o["CreationDate"] is string c) created = ManagementDateTimeConverter.ToDateTime(c); }
                    catch (Exception e) when (e is ArgumentOutOfRangeException or FormatException) { }
                    list.Add(new ProcessInfo(
                        Convert.ToInt32(o["ProcessId"]),
                        Convert.ToInt32(o["ParentProcessId"]),
                        (string?)o["Name"] ?? "",
                        (string?)o["CommandLine"] ?? "",
                        Convert.ToInt64(o["WorkingSetSize"] ?? 0L),
                        created,
                        (string?)o["ExecutablePath"] ?? ""));
                }
            }
        }
        var mem = new Native.MEMORYSTATUSEX();
        Native.GlobalMemoryStatusEx(mem);
        return new Snapshot(list, (long)mem.ullTotalPhys, (long)mem.ullAvailPhys);
    }
}

public static class Desktop
{
    public static int ForegroundPid()
    {
        Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out var pid);
        return (int)pid;
    }

    /// <summary>The foreground window covers its whole monitor (games, fullscreen video). The desktop itself does not count.</summary>
    public static bool ForegroundIsFullscreen()
    {
        var hwnd = Native.GetForegroundWindow();
        if (hwnd == IntPtr.Zero || hwnd == Native.GetDesktopWindow() || hwnd == Native.GetShellWindow()) return false;
        // A maximised browser or editor also covers the monitor (its frame even overhangs it), but it has a title bar. Games don't.
        const int GWL_STYLE = -16, WS_CAPTION = 0x00C00000;
        if ((Native.GetWindowLong(hwnd, GWL_STYLE) & WS_CAPTION) == WS_CAPTION) return false;
        if (!Native.GetWindowRect(hwnd, out var r)) return false;
        var mon = new Native.MONITORINFO();
        if (!Native.GetMonitorInfo(Native.MonitorFromWindow(hwnd, 2), mon)) return false;
        return r.Left <= mon.rcMonitor.Left && r.Top <= mon.rcMonitor.Top
            && r.Right >= mon.rcMonitor.Right && r.Bottom >= mon.rcMonitor.Bottom;
    }
}

/// <param name="FreedBytes">What this round itself freed: closed processes plus the working set trimmed away. Not the system-wide change in free RAM, which other apps move too.</param>
public sealed record CleanResult(int Killed, int Trimmed, long FreeBeforeBytes, long FreeAfterBytes, string Reason, long FreedBytes = 0);

/// <summary>Runs one guard round: read, decide, act, log.</summary>
public static class Guard
{
    /// <summary>Reads and analyzes all processes, remembering which helpers an AI app started (see <see cref="Lineage"/>).</summary>
    public static Report Scan(ProfileSet profiles)
    {
        var lineage = Lineage.Load(Paths.LineageFile);
        var report = Analyzer.Analyze(SnapshotReader.Read(), profiles, lineage);
        lineage.Save(Paths.LineageFile);
        return report;
    }

    public static (Report Report, Plan Plan) Inspect(GuardSettings s, ProfileSet profiles, bool force = false)
    {
        var report = Scan(profiles);
        var fg = Desktop.ForegroundPid();
        var byPid = report.Snapshot.Processes.ToDictionary(p => p.Pid);
        // A fullscreen terminal running Claude Code is no game: count each AI process's parent chain, as Prioritize does.
        var game = Desktop.ForegroundIsFullscreen() && !report.Agents.Any(a => a.Processes.Any(p => p.Pid == fg || Analyzer.HasAncestor(p, fg, byPid)));
        return (report, Policy.Decide(report, s, fg, game, force));
    }

    public static CleanResult Run(GuardSettings s, ProfileSet profiles, bool force = false, bool dryRun = false) =>
        Execute(Inspect(s, profiles, force), dryRun, force);

    static readonly Dictionary<string, (DateTime At, long WorkingSet)> Trimmed = [];
    static readonly TimeSpan TrimCooldown = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Trimmed pages that come straight back are being used: trimming them again every round only costs CPU and disk.
    /// Skip a process trimmed in the last 15 minutes unless it grew a quarter past its size before that trim.
    /// </summary>
    static bool CoolingDown(ProcessInfo p, DateTime now) =>
        Trimmed.TryGetValue($"{p.Pid}:{p.Created.Ticks}", out var t) && now - t.At < TrimCooldown && p.WorkingSetBytes < t.WorkingSet * 5 / 4;

    /// <summary>Carries out a plan. Returns what really happened, not what was planned.</summary>
    /// <param name="force">Manual "Free RAM now": ignores the trim cooldown.</param>
    /// <param name="prioritize">Apply <see cref="Plan.Lower"/>. Only for a long-running guard: it has to be there to restore the priority later.</param>
    public static CleanResult Execute((Report Report, Plan Plan) inspected, bool dryRun = false, bool force = false, bool prioritize = false)
    {
        var (report, plan) = inspected;
        int killed = 0, trimmed = 0;
        long freed = 0;
        foreach (var p in plan.Kill)
        {
            var ok = dryRun || TryKill(p);
            if (ok) { killed++; freed += p.WorkingSetBytes; }
            Log.Write($"{(dryRun ? "would close" : ok ? "closed" : "could not close")} orphan {p.Name} {p.Pid} {p.WorkingSetBytes / 1048576} MB {Analyzer.HelperKey(p.CommandLine)}");
        }
        var now = DateTime.Now;
        foreach (var stale in Trimmed.Where(kv => now - kv.Value.At >= TrimCooldown).Select(kv => kv.Key).ToList()) Trimmed.Remove(stale);
        foreach (var p in plan.Trim)
        {
            if (!force && CoolingDown(p, now)) continue;
            if (dryRun) { trimmed++; continue; }
            var after = TryTrim(p);
            if (after < 0) continue;
            trimmed++;
            freed += Math.Max(0, p.WorkingSetBytes - after);
            Trimmed[$"{p.Pid}:{p.Created.Ticks}"] = (now, p.WorkingSetBytes); // the size before: right after a trim it is near zero, and any regrowth would pass "a quarter more"
        }
        if (prioritize) Prioritize(dryRun ? [] : plan.Lower, !dryRun && plan.LowerCpu, report.Snapshot.Processes.ToDictionary(p => p.Pid));

        var freeAfter = dryRun ? report.Snapshot.FreeRamBytes : FreeRamBytes();
        var result = new CleanResult(killed, trimmed, report.Snapshot.FreeRamBytes, freeAfter, plan.Reason, freed);
        if (killed + trimmed > 0)
            Log.Write($"{(dryRun ? "[dry-run] " : "")}{plan.Reason}: closed {killed} orphans, trimmed {trimmed} AI processes, freed {freed >> 20} MB, free {Gb(result.FreeBeforeBytes)} -> {Gb(freeAfter)} GB");
        return result;
    }

    /// <summary>What the guard changed in one process. It restores exactly that, and only while the value is still the one it set.</summary>
    /// <param name="front">Switching to any of these PIDs brings the process to the front: itself, its app, the terminal it runs in.</param>
    internal sealed class Lowered(ProcessInfo p, HashSet<int> front)
    {
        public readonly ProcessInfo P = p;
        public readonly HashSet<int> Front = front;
        public bool Memory, Cpu, Qos;
    }

    static readonly object PriorityGate = new(); // rounds run on the thread pool, foreground changes on the UI thread
    static readonly Dictionary<string, Lowered> LoweredNow = [];

    /// <summary>The foreground window's PID and whether it is fullscreen. Tests replace it: their desktop has no game.</summary>
    internal static Func<(int Pid, bool Fullscreen)> ReadForeground = () => (Desktop.ForegroundPid(), Desktop.ForegroundIsFullscreen());

    /// <summary>Lowers background AI processes and restores the ones that should not be lowered any more: in front now, gone, or the game is over.</summary>
    static void Prioritize(IReadOnlyList<ProcessInfo> lower, bool cpu, IReadOnlyDictionary<int, ProcessInfo> byPid)
    {
        lock (PriorityGate)
        {
            // The plan is seconds old (a scan takes that long) and the hook may already have fired: what is in front now wins.
            var (fg, fullscreen) = ReadForeground();
            cpu &= fullscreen;
            var want = new Dictionary<string, (ProcessInfo P, HashSet<int> Front)>();
            foreach (var p in lower)
            {
                // Explorer starts everything, so being in front of it says nothing (as in Policy).
                HashSet<int> front = [p.Pid, .. Analyzer.Ancestors(p, byPid).Where(a => !string.Equals(a.Name, "explorer.exe", StringComparison.OrdinalIgnoreCase)).Select(a => a.Pid)];
                if (front.Contains(fg)) { cpu = false; continue; } // an AI app is in front, so no game is
                want[$"{p.Pid}:{p.Created.Ticks}"] = (p, front);
            }
            var restored = 0;
            foreach (var key in LoweredNow.Keys.Where(k => !want.ContainsKey(k)).ToList())
            {
                if (LoweredNow[key].Cpu) restored++;
                Native.Restore(LoweredNow[key], memory: true);
                LoweredNow.Remove(key);
            }
            var cpuBefore = LoweredNow.Values.Count(l => l.Cpu);
            foreach (var (key, (p, front)) in want)
            {
                if (!LoweredNow.TryGetValue(key, out var l)) LoweredNow[key] = l = new Lowered(p, front);
                if (!cpu && (l.Cpu || l.Qos)) { if (l.Cpu) restored++; Native.Restore(l, memory: false); }
                Native.Lower(l, cpu);
            }
            var cpuNow = LoweredNow.Values.Count(l => l.Cpu);
            if (cpuNow > cpuBefore) Log.Write($"game in front: lowered CPU priority of {cpuNow - cpuBefore} AI processes");
            if (restored > 0) Log.Write($"restored CPU priority of {restored} AI processes");
        }
    }

    /// <summary>Puts back every priority the guard lowered. On exit, and when an app you switch to must not wait a round for it.</summary>
    public static void RestorePriorities()
    {
        lock (PriorityGate)
        {
            foreach (var l in LoweredNow.Values) Native.Restore(l, memory: true);
            LoweredNow.Clear();
        }
    }

    /// <summary>The foreground window changed: you switched to a lowered app, or left the game. Cheap, no scan.</summary>
    public static void ForegroundChanged()
    {
        lock (PriorityGate)
        {
            if (LoweredNow.Count == 0) return;
            var (fg, fullscreen) = ReadForeground();
            var cpuLowered = LoweredNow.Values.Any(l => l.Cpu || l.Qos);
            if (!LoweredNow.Values.Any(l => l.Front.Contains(fg)) && !(cpuLowered && !fullscreen)) return;
        }
        RestorePriorities(); // the next round lowers what is still in the background
    }

    /// <summary>A RAM tool should practise what it preaches: drop our own idle pages between rounds.</summary>
    public static void TrimSelf() => Native.TrimWorkingSet(Environment.ProcessId);

    static bool TryKill(ProcessInfo p)
    {
        try
        {
            using var proc = Process.GetProcessById(p.Pid);
            if (!IsSame(proc, p)) return false;
            proc.Kill();
            return true;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Trims after re-checking identity (the PID could have been reused). Returns the working set after, or -1.</summary>
    static long TryTrim(ProcessInfo p)
    {
        try
        {
            using var proc = Process.GetProcessById(p.Pid);
            if (!IsSame(proc, p) || !Native.TrimWorkingSet(p.Pid)) return -1;
            proc.Refresh();
            return proc.WorkingSet64;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return -1;
        }
    }

    static bool IsSame(Process proc, ProcessInfo p) => Math.Abs((proc.StartTime - p.Created).TotalSeconds) <= 2;

    public static long FreeRamBytes() => Memory().Free;

    /// <summary>Cheap memory reading (no process scan), for the live graph.</summary>
    public static (long Total, long Free) Memory()
    {
        var mem = new Native.MEMORYSTATUSEX();
        Native.GlobalMemoryStatusEx(mem);
        return ((long)mem.ullTotalPhys, (long)mem.ullAvailPhys);
    }

    public static string Gb(long bytes) => (bytes / 1073741824.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
}

public static class Log
{
    static readonly object Gate = new();

    public static void Write(string line)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Paths.LogFile)!);
                var fi = new FileInfo(Paths.LogFile);
                if (fi.Exists && fi.Length > 1_000_000) File.Move(Paths.LogFile, Paths.LogFile + ".old", true);
                File.AppendAllText(Paths.LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
            }
            catch (Exception) { } // logging must never stop the guard
        }
    }
}

internal static class Native
{
    const uint PROCESS_SET_QUOTA = 0x0100, PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    internal sealed class MEMORYSTATUSEX
    {
        public uint dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
        public uint dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)] internal struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    internal sealed class MONITORINFO
    {
        public int cbSize = Marshal.SizeOf<MONITORINFO>();
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
    }

    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX m);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("psapi.dll")] static extern bool EmptyWorkingSet(IntPtr h);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] internal static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] internal static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] internal static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] internal static extern bool GetMonitorInfo(IntPtr m, [In, Out] MONITORINFO info);

    const uint PROCESS_SET_INFORMATION = 0x0200, NORMAL_PRIORITY_CLASS = 0x20, BELOW_NORMAL_PRIORITY_CLASS = 0x4000;
    const int ProcessMemoryPriority = 0, ProcessPowerThrottling = 4;
    const uint MEMORY_PRIORITY_LOW = 2, MEMORY_PRIORITY_NORMAL = 5, EXECUTION_SPEED = 1;

    [StructLayout(LayoutKind.Sequential)] struct POWER_THROTTLING { public uint Version, ControlMask, StateMask; }

    [DllImport("kernel32.dll")] static extern bool GetProcessTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll")] static extern bool GetProcessInformation(IntPtr h, int cls, out uint info, uint size);
    [DllImport("kernel32.dll")] static extern bool SetProcessInformation(IntPtr h, int cls, ref uint info, uint size);
    [DllImport("kernel32.dll")] static extern bool GetProcessInformation(IntPtr h, int cls, ref POWER_THROTTLING info, uint size);
    [DllImport("kernel32.dll")] static extern bool SetProcessInformation(IntPtr h, int cls, ref POWER_THROTTLING info, uint size);
    [DllImport("kernel32.dll")] static extern uint GetPriorityClass(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool SetPriorityClass(IntPtr h, uint c);

    /// <summary>Opens a process for priority changes, only while it is still the one that was scanned (PIDs get reused). Zero otherwise.</summary>
    static IntPtr OpenSame(ProcessInfo p)
    {
        var h = OpenProcess(PROCESS_SET_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION, false, p.Pid);
        if (h == IntPtr.Zero) return h;
        if (GetProcessTimes(h, out var created, out _, out _, out _) && Math.Abs((DateTime.FromFileTime(created) - p.Created).TotalSeconds) <= 2) return h;
        CloseHandle(h);
        return IntPtr.Zero;
    }

    /// <summary>EcoQoS state, or null when Windows can't tell (older builds): then it is left alone.</summary>
    static POWER_THROTTLING? Throttling(IntPtr h)
    {
        var t = new POWER_THROTTLING { Version = 1 };
        return GetProcessInformation(h, ProcessPowerThrottling, ref t, (uint)Marshal.SizeOf<POWER_THROTTLING>()) ? t : null;
    }

    static bool SetThrottling(IntPtr h, uint mask)
    {
        var t = new POWER_THROTTLING { Version = 1, ControlMask = mask, StateMask = mask }; // mask 0: the system decides again
        return SetProcessInformation(h, ProcessPowerThrottling, ref t, (uint)Marshal.SizeOf<POWER_THROTTLING>());
    }

    /// <summary>
    /// Low memory priority: Windows repurposes these pages first when RAM runs out. With <paramref name="cpu"/> also below-normal
    /// priority and EcoQoS (efficiency cores, lower clocks). Only values still at Windows' default are changed: an app's or the user's own choice is kept.
    /// </summary>
    internal static void Lower(Guard.Lowered l, bool cpu)
    {
        if (l.Memory && (!cpu || l.Cpu && l.Qos)) return;
        var h = OpenSame(l.P);
        if (h == IntPtr.Zero) return;
        try
        {
            if (!l.Memory && GetProcessInformation(h, ProcessMemoryPriority, out var mem, 4) && mem == MEMORY_PRIORITY_NORMAL)
            {
                var low = MEMORY_PRIORITY_LOW;
                l.Memory = SetProcessInformation(h, ProcessMemoryPriority, ref low, 4);
            }
            if (!cpu) return;
            if (!l.Cpu && GetPriorityClass(h) == NORMAL_PRIORITY_CLASS) l.Cpu = SetPriorityClass(h, BELOW_NORMAL_PRIORITY_CLASS);
            if (!l.Qos && Throttling(h) is { } t && (t.ControlMask & EXECUTION_SPEED) == 0) l.Qos = SetThrottling(h, EXECUTION_SPEED);
        }
        finally { CloseHandle(h); }
    }

    /// <summary>Undoes <see cref="Lower"/>: the CPU part, and with <paramref name="memory"/> the memory priority too.</summary>
    internal static void Restore(Guard.Lowered l, bool memory)
    {
        var h = OpenSame(l.P);
        if (h != IntPtr.Zero)
            try
            {
                if (memory && l.Memory && GetProcessInformation(h, ProcessMemoryPriority, out var mem, 4) && mem == MEMORY_PRIORITY_LOW)
                {
                    var normal = MEMORY_PRIORITY_NORMAL;
                    SetProcessInformation(h, ProcessMemoryPriority, ref normal, 4);
                }
                if (l.Cpu && GetPriorityClass(h) == BELOW_NORMAL_PRIORITY_CLASS) SetPriorityClass(h, NORMAL_PRIORITY_CLASS);
                if (l.Qos && Throttling(h) is { } t && (t.ControlMask & t.StateMask & EXECUTION_SPEED) != 0) SetThrottling(h, 0);
            }
            finally { CloseHandle(h); }
        if (memory) l.Memory = false; // a process that is gone has nothing left to restore
        l.Cpu = l.Qos = false;
    }

    /// <summary>Moves a process's idle pages to the pagefile. The process keeps running and pages back in on use.</summary>
    internal static bool TrimWorkingSet(int pid)
    {
        var h = OpenProcess(PROCESS_SET_QUOTA | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return false;
        try { return EmptyWorkingSet(h); }
        finally { CloseHandle(h); }
    }
}
