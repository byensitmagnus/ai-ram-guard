using System.Net;
using System.Runtime.InteropServices;

namespace AiRamGuard.Core;

/// <summary>Open TCP connections to the internet per process. On mobile broadband the provider's NAT (CGNAT) shares a limited pool of them between all devices.</summary>
public static class Connections
{
    /// <summary>
    /// IPv4 only: IPv6 doesn't go through NAT. LAN, loopback and 100.64/10 (Tailscale, CGNAT) peers don't use the pool.
    /// TIME_WAIT rows belong to PID 0, so closed connections aren't counted.
    /// </summary>
    public static Dictionary<int, int> ByPid()
    {
        var counts = new Dictionary<int, int>();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buf, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0) != 0) continue; // table grew in between: ask again
                var n = Marshal.ReadInt32(buf);
                for (var i = 0; i < n; i++)
                {
                    var row = buf + 4 + i * RowSize; // MIB_TCPROW_OWNER_PID: state, local addr, local port, remote addr, remote port, pid
                    var pid = Marshal.ReadInt32(row, 20);
                    if (Marshal.ReadInt32(row) == Listen || pid <= 4) continue;
                    if (IsInternet(new IPAddress(BitConverter.GetBytes(Marshal.ReadInt32(row, 12))))) counts[pid] = counts.GetValueOrDefault(pid) + 1;
                }
                return counts;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        return counts;
    }

    internal static bool IsInternet(IPAddress a)
    {
        var b = a.GetAddressBytes();
        return b.Length == 4 && !(b[0] is 0 or 10 or 127 || b[0] >= 224 || b[0] == 169 && b[1] == 254 || b[0] == 172 && (b[1] & 0xF0) == 16
            || b[0] == 192 && b[1] == 168 || b[0] == 100 && (b[1] & 0xC0) == 64);
    }

    const int AF_INET = 2, TCP_TABLE_OWNER_PID_ALL = 5, Listen = 2, RowSize = 24;

    [DllImport("iphlpapi.dll")] static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);
}
