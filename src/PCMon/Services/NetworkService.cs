using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using PCMon.Core;

namespace PCMon.Services;

public sealed record AdapterSample(
    string Id, string Name, string Description, string Kind, string Ipv4,
    double DownPerSec, double UpPerSec, long TotalDown, long TotalUp);

public sealed record TcpConnection(int Pid, string Local, string Remote, string State);

sealed class NetworkService
{
    sealed class State
    {
        public long Rx, Tx, BaseRx, BaseTx, Ticks;
        public string Ipv4 = "";
        public long Ipv4Ticks;
    }

    readonly Dictionary<string, State> _state = [];
    NetworkInterface[] _adapters = [];
    long _adaptersTicks;
    volatile bool _adaptersDirty = true;

    public NetworkService()
    {
        // Enumerating adapters costs ~50 ms; only redo it when Windows says something changed (or every 10 s).
        NetworkChange.NetworkAddressChanged += (_, _) => _adaptersDirty = true;
        NetworkChange.NetworkAvailabilityChanged += (_, _) => _adaptersDirty = true;
    }

    NetworkInterface[] Adapters(long now)
    {
        if (!_adaptersDirty && now - _adaptersTicks < Stopwatch.Frequency * 10) return _adapters;
        _adaptersDirty = false;
        _adaptersTicks = now;
        _adapters = NetworkInterface.GetAllNetworkInterfaces()
            .Where(ni => ni.OperationalStatus == OperationalStatus.Up &&
                         ni.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .ToArray();
        return _adapters;
    }

    public IReadOnlyList<AdapterSample> Sample()
    {
        var list = new List<AdapterSample>();
        long now = Stopwatch.GetTimestamp();
        NetworkInterface[] adapters;
        try
        {
            adapters = Adapters(now);
        }
        catch (Exception ex)
        {
            Log.Error("network adapters", ex);
            return list;
        }

        foreach (var ni in adapters)
        {
            try
            {
                var stats = ni.GetIPStatistics();
                long rx = stats.BytesReceived, tx = stats.BytesSent;
                double down = 0, up = 0;

                if (_state.TryGetValue(ni.Id, out var st))
                {
                    double seconds = (now - st.Ticks) / (double)Stopwatch.Frequency;
                    down = Core.Rate.BytesPerSecond(st.Rx, rx, seconds);
                    up = Core.Rate.BytesPerSecond(st.Tx, tx, seconds);
                    if (rx < st.Rx || tx < st.Tx) { st.BaseRx = rx; st.BaseTx = tx; } // counters reset
                }
                else
                {
                    st = new State { BaseRx = rx, BaseTx = tx };
                    _state[ni.Id] = st;
                }

                st.Rx = rx;
                st.Tx = tx;
                st.Ticks = now;

                // Addresses rarely change; refresh them every 10 s instead of every tick.
                if (now - st.Ipv4Ticks > Stopwatch.Frequency * 10)
                {
                    st.Ipv4 = ni.GetIPProperties().UnicastAddresses
                        .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString() ?? "—";
                    st.Ipv4Ticks = now;
                }

                list.Add(new AdapterSample(ni.Id, ni.Name, ni.Description, KindOf(ni.NetworkInterfaceType), st.Ipv4,
                    down, up, rx - st.BaseRx, tx - st.BaseTx));
            }
            catch (Exception ex)
            {
                Log.Error("adapter " + ni.Name, ex);
            }
        }
        return list;
    }

    static string KindOf(NetworkInterfaceType t) => t switch
    {
        NetworkInterfaceType.Wireless80211 => "Wi-Fi",
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
            or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit => "Ethernet",
        NetworkInterfaceType.Ppp => "PPP",
        NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 => "Mobile",
        _ => t.ToString(),
    };

    /// <summary>IPv4 TCP table with owning PIDs (SRS FR-11).</summary>
    public static IReadOnlyList<TcpConnection> TcpConnections()
    {
        var list = new List<TcpConnection>();
        int size = 0;
        Native.GetExtendedTcpTable(IntPtr.Zero, ref size, true, Native.AF_INET, Native.TCP_TABLE_OWNER_PID_ALL, 0);
        if (size == 0) return list;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            size += 4096; // the table can grow between the two calls
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (Native.GetExtendedTcpTable(buffer, ref size, true, Native.AF_INET, Native.TCP_TABLE_OWNER_PID_ALL, 0) != 0)
                    continue;

                int count = Marshal.ReadInt32(buffer);
                int rowSize = Marshal.SizeOf<Native.MIB_TCPROW_OWNER_PID>();
                var rowPtr = buffer + 4;
                for (int i = 0; i < count; i++, rowPtr += rowSize)
                {
                    var row = Marshal.PtrToStructure<Native.MIB_TCPROW_OWNER_PID>(rowPtr);
                    list.Add(new TcpConnection(
                        row.owningPid,
                        $"{new IPAddress(row.localAddr)}:{TcpText.Port(row.localPort)}",
                        $"{new IPAddress(row.remoteAddr)}:{TcpText.Port(row.remotePort)}",
                        TcpText.State(row.state)));
                }
                return list;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return list;
    }
}
