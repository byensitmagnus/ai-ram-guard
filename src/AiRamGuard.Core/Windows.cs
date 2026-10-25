using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;

namespace AiRamGuard.Core;

public static class Paths
{
    public static string ConfigDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AiRamGuard");
    public static string LogFile { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AiRamGuard", "guard.log");
}

/// <summary>Reads processes and memory from Windows.</summary>
public static class SnapshotReader
{
    public static Snapshot Read()
    {
        var list = new List<ProcessInfo>();
        using (var search = new ManagementObjectSearcher(
            "SELECT ProcessId, ParentProcessId, Name, CommandLine, WorkingSetSize, CreationDate FROM Win32_Process"))
        using (var results = search.Get())
        {
            foreach (ManagementObject o in results)
            {
                using (o)
                {
                    var created = o["CreationDate"] is string c ? ManagementDateTimeConverter.ToDateTime(c) : DateTime.MinValue;
                    list.Add(new ProcessInfo(
                        Convert.ToInt32(o["ProcessId"]),
                        Convert.ToInt32(o["ParentProcessId"]),
                        (string?)o["Name"] ?? "",
                        (string?)o["CommandLine"] ?? "",
                        Convert.ToInt64(o["WorkingSetSize"] ?? 0L),
                        created));
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
        if (!Native.GetWindowRect(hwnd, out var r)) return false;
        var mon = new Native.MONITORINFO();
        if (!Native.GetMonitorInfo(Native.MonitorFromWindow(hwnd, 2), mon)) return false;
        return r.Left <= mon.rcMonitor.Left && r.Top <= mon.rcMonitor.Top
            && r.Right >= mon.rcMonitor.Right && r.Bottom >= mon.rcMonitor.Bottom;
    }
}

public sealed record CleanResult(int Killed, int Trimmed, long FreeBeforeBytes, long FreeAfterBytes, string Reason);

/// <summary>Runs one guard round: read, decide, act, log.</summary>
public static class Guard
{
    public static (Report Report, Plan Plan) Inspect(GuardSettings s, ProfileSet profiles, bool force = false)
    {
        var report = Analyzer.Analyze(SnapshotReader.Read(), profiles);
        var fg = Desktop.ForegroundPid();
        var game = Desktop.ForegroundIsFullscreen() && report.Agents.All(a => a.Processes.All(p => p.Pid != fg));
        return (report, Policy.Decide(report, s, fg, game, force));
    }

    public static CleanResult Run(GuardSettings s, ProfileSet profiles, bool force = false, bool dryRun = false)
    {
        var (report, plan) = Inspect(s, profiles, force);
        int killed = 0, trimmed = 0;
        foreach (var p in plan.Kill)
        {
            if (dryRun || TryKill(p)) killed++;
            Log.Write($"{(dryRun ? "would close" : "closed")} orphan {p.Name} {p.Pid} {p.WorkingSetBytes / 1048576} MB {Analyzer.HelperKey(p.CommandLine)}");
        }
        foreach (var p in plan.Trim)
            if (dryRun || Native.TrimWorkingSet(p.Pid)) trimmed++;

        var after = dryRun ? report.Snapshot.FreeRamBytes : SnapshotFree();
        var result = new CleanResult(killed, trimmed, report.Snapshot.FreeRamBytes, after, plan.Reason);
        if (killed + trimmed > 0)
            Log.Write($"{(dryRun ? "[dry-run] " : "")}{plan.Reason}: closed {killed} orphans, trimmed {trimmed} AI processes, free {Gb(result.FreeBeforeBytes)} -> {Gb(after)} GB");
        return result;
    }

    /// <summary>A RAM tool should practise what it preaches: drop our own idle pages between rounds.</summary>
    public static void TrimSelf() => Native.TrimWorkingSet(Environment.ProcessId);

    static bool TryKill(ProcessInfo p)
    {
        try
        {
            using var proc = Process.GetProcessById(p.Pid);
            // Re-check identity: the PID could have been reused since the snapshot.
            if (Math.Abs((proc.StartTime - p.Created).TotalSeconds) > 2) return false;
            proc.Kill();
            return true;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    static long SnapshotFree()
    {
        var mem = new Native.MEMORYSTATUSEX();
        Native.GlobalMemoryStatusEx(mem);
        return (long)mem.ullAvailPhys;
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
            catch (IOException) { } // logging must never stop the guard
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
