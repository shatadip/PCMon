using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using PCMon.Core;

namespace PCMon.Services;

public sealed record AdapterSample(
    string Id, string Name, string Description, string Kind, string Ipv4,
    double DownPerSec, double UpPerSec, long TotalDown, long TotalUp, bool IsVpn);

sealed class NetworkService
{
    sealed class State
    {
        public long Rx, Tx, BaseRx, BaseTx, Ticks;
        public string Ipv4 = "";
        public long Ipv4Ticks;
    }

    readonly Dictionary<string, State> _state = [];
    NetworkInterface[] _all = [];
    NetworkInterface[] _adapters = [];
    long _adaptersTicks;
    volatile bool _adaptersDirty = true;

    public NetworkService()
    {
        // Enumerating adapters costs ~50 ms; only redo it when Windows says something changed (or every 10 s).
        NetworkChange.NetworkAddressChanged += (_, _) => _adaptersDirty = true;
        NetworkChange.NetworkAvailabilityChanged += (_, _) => _adaptersDirty = true;
    }

    void RefreshAdapters(long now)
    {
        if (!_adaptersDirty && now - _adaptersTicks < Stopwatch.Frequency * 10) return;
        _adaptersDirty = false;
        _adaptersTicks = now;
        _all = NetworkInterface.GetAllNetworkInterfaces();
        _adapters = _all
            .Where(ni => ni.OperationalStatus == OperationalStatus.Up && ni.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                         // Teredo/ISATAP-style tunnels are noise, but VPN tunnels are exactly what people want to see.
                         (ni.NetworkInterfaceType != NetworkInterfaceType.Tunnel || IsVpn(ni)))
            .ToArray();
    }

    static bool IsVpn(NetworkInterface ni) => VpnRules.IsVpnAdapter(ni.Name, ni.Description, ni.NetworkInterfaceType);

    public IReadOnlyList<AdapterSample> Sample()
    {
        var list = new List<AdapterSample>();
        long now = Stopwatch.GetTimestamp();
        try
        {
            RefreshAdapters(now);
        }
        catch (Exception ex)
        {
            Log.Error("network adapters", ex);
            return list;
        }

        foreach (var ni in _adapters)
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

                bool vpn = IsVpn(ni);
                list.Add(new AdapterSample(ni.Id, ni.Name, ni.Description, vpn ? "VPN" : KindOf(ni.NetworkInterfaceType), st.Ipv4,
                    down, up, rx - st.BaseRx, tx - st.BaseTx, vpn));
            }
            catch (Exception ex)
            {
                Log.Error("adapter " + ni.Name, ex);
            }
        }
        return list;
    }

    /// <summary>
    /// Is a VPN installed, connected, and carrying all traffic? "All traffic" means Windows' route to a
    /// public address goes through the VPN adapter; a routing-table lookup, nothing is sent.
    /// </summary>
    public VpnStatus Vpn()
    {
        try
        {
            RefreshAdapters(Stopwatch.GetTimestamp());
            var vpns = _all.Where(IsVpn).ToList();
            if (vpns.Count == 0) return VpnStatus.None;

            var active = vpns.FirstOrDefault(v => v.OperationalStatus == OperationalStatus.Up);
            if (active is null) return new VpnStatus(true, false, vpns[0].Name, null);

            bool? full = null;
            int? index = null;
            try { index = active.GetIPProperties().GetIPv4Properties()?.Index; }
            catch (NetworkInformationException) { }
            var probe = BitConverter.ToUInt32(IPAddress.Parse("1.1.1.1").GetAddressBytes(), 0);
            if (index is not null && Native.GetBestInterface(probe, out var best) == 0)
                full = best == index;
            return new VpnStatus(true, true, active.Name, full);
        }
        catch (Exception ex)
        {
            Log.Error("vpn detect", ex);
            return VpnStatus.None;
        }
    }

    static string KindOf(NetworkInterfaceType t) => t switch
    {
        NetworkInterfaceType.Wireless80211 => "Wi-Fi",
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
            or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit => "Ethernet",
        NetworkInterfaceType.Ppp => "PPP",
        NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 => "Mobile",
        _ => "Virtual",
    };

    /// <summary>IPv4 + IPv6 TCP tables with owning PIDs (SRS FR-11).</summary>
    public static IReadOnlyList<TcpRow> TcpConnections()
    {
        var list = new List<TcpRow>();
        ReadTable(Native.AF_INET, 24, list, (p, rows) =>
        {
            var b = new byte[24];
            Marshal.Copy(p, b, 0, 24);
            rows.Add(new TcpRow("TCP",
                new IPAddress(BitConverter.ToUInt32(b, 4)), TcpText.Port(BitConverter.ToUInt32(b, 8)),
                new IPAddress(BitConverter.ToUInt32(b, 12)), TcpText.Port(BitConverter.ToUInt32(b, 16)),
                TcpText.State(BitConverter.ToUInt32(b, 0)), BitConverter.ToInt32(b, 20)));
        });
        ReadTable(Native.AF_INET6, 56, list, (p, rows) =>
        {
            var b = new byte[56];
            Marshal.Copy(p, b, 0, 56);
            rows.Add(new TcpRow("TCPv6",
                new IPAddress(b.AsSpan(0, 16).ToArray(), BitConverter.ToUInt32(b, 16)), TcpText.Port(BitConverter.ToUInt32(b, 20)),
                new IPAddress(b.AsSpan(24, 16).ToArray(), BitConverter.ToUInt32(b, 40)), TcpText.Port(BitConverter.ToUInt32(b, 44)),
                TcpText.State(BitConverter.ToUInt32(b, 48)), BitConverter.ToInt32(b, 52)));
        });
        return list;
    }

    static void ReadTable(int family, int rowSize, List<TcpRow> rows, Action<IntPtr, List<TcpRow>> parse)
    {
        int size = 0;
        Native.GetExtendedTcpTable(IntPtr.Zero, ref size, true, family, Native.TCP_TABLE_OWNER_PID_ALL, 0);
        if (size == 0) return;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            size += 4096; // the table can grow between the two calls
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (Native.GetExtendedTcpTable(buffer, ref size, true, family, Native.TCP_TABLE_OWNER_PID_ALL, 0) != 0)
                    continue;
                int count = Marshal.ReadInt32(buffer);
                for (int i = 0; i < count; i++)
                    parse(buffer + 4 + i * rowSize, rows);
                return;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }
}
