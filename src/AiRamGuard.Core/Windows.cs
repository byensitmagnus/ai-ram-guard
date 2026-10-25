using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;

namespace AiRamGuard.Core;

public static class Paths
{
    public static string ConfigDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AiRamGuard");
    public static string DataDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AiRamGuard");
    public static string LogFile { get; } = Path.Combine(DataDir, "guard.log");
    public static string HistoryFile { get; } = Path.Combine(DataDir, "history.json");
    public static string StatsFile { get; } = Path.Combine(DataDir, "stats.json");
    public static string LineageFile { get; } = Path.Combine(DataDir, "lineage.json");
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
        var game = Desktop.ForegroundIsFullscreen() && report.Agents.All(a => a.Processes.All(p => p.Pid != fg));
        return (report, Policy.Decide(report, s, fg, game, force));
    }

    public static CleanResult Run(GuardSettings s, ProfileSet profiles, bool force = false, bool dryRun = false) =>
        Execute(Inspect(s, profiles, force), dryRun, force);

    static readonly Dictionary<string, (DateTime At, long WorkingSet)> Trimmed = [];
    static readonly TimeSpan TrimCooldown = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Trimmed pages that come straight back are being used: trimming them again every round only costs CPU and disk.
    /// Skip a process trimmed in the last 15 minutes unless it grew by a quarter since.
    /// </summary>
    static bool CoolingDown(ProcessInfo p, DateTime now) =>
        Trimmed.TryGetValue($"{p.Pid}:{p.Created.Ticks}", out var t) && now - t.At < TrimCooldown && p.WorkingSetBytes < t.WorkingSet * 5 / 4;

    /// <summary>Carries out a plan. Returns what really happened, not what was planned.</summary>
    /// <param name="force">Manual "Free RAM now": ignores the trim cooldown.</param>
    public static CleanResult Execute((Report Report, Plan Plan) inspected, bool dryRun = false, bool force = false)
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
            Trimmed[$"{p.Pid}:{p.Created.Ticks}"] = (now, after);
        }

        var freeAfter = dryRun ? report.Snapshot.FreeRamBytes : FreeRamBytes();
        var result = new CleanResult(killed, trimmed, report.Snapshot.FreeRamBytes, freeAfter, plan.Reason, freed);
        if (killed + trimmed > 0)
            Log.Write($"{(dryRun ? "[dry-run] " : "")}{plan.Reason}: closed {killed} orphans, trimmed {trimmed} AI processes, freed {freed >> 20} MB, free {Gb(result.FreeBeforeBytes)} -> {Gb(freeAfter)} GB");
        return result;
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

    /// <summary>Moves a process's idle pages to the pagefile. The process keeps running and pages back in on use.</summary>
    internal static bool TrimWorkingSet(int pid)
    {
        var h = OpenProcess(PROCESS_SET_QUOTA | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return false;
        try { return EmptyWorkingSet(h); }
        finally { CloseHandle(h); }
    }
}
