using LibreHardwareMonitor.Hardware;
using PCMon.Core;

namespace PCMon.Services;

public sealed record Snapshot(
    double? CpuLoad,
    double? CpuTemp,
    bool CpuTempIsAcpi,
    string? CpuName,
    string? GpuName,
    double? GpuLoad,
    double? GpuTemp,
    double? VramUsedMb,
    double? VramTotalMb,
    ulong RamUsed,
    ulong RamTotal,
    IReadOnlyList<(DiskUsage Disk, double? Temp)> Disks,
    IReadOnlyList<SensorReading> Sensors,
    IReadOnlyList<AdapterSample> Adapters,
    bool HasLhmCpuTemp,
    bool HardwareReady,
    string? HardwareError,
    VpnStatus Vpn)
{
    public double RamPercent => RamTotal > 0 ? RamUsed * 100.0 / RamTotal : 0;
    public double TotalDown => Adapters.Sum(a => a.DownPerSec);
    public double TotalUp => Adapters.Sum(a => a.UpPerSec);
}

/// <summary>
/// The one background loop (SRS FR-2): samples everything every second, processes every
/// two seconds, and never touches the UI thread. LibreHardwareMonitor is only ever used from here.
/// </summary>
sealed class Sampler : IDisposable
{
    readonly HardwareService _hardware = new();
    readonly CpuUsage _cpu = new();
    readonly NetworkService _network = new();
    readonly ProcessService _processes = new();
    readonly CancellationTokenSource _cts = new();
    readonly AttackWatch _attackWatch = new();
    Dictionary<string, string> _diskModels = [];
    double? _acpiTemp;
    VpnStatus _vpn = VpnStatus.None;
    int _tick, _securityBusy;

    public event Action<Snapshot>? Sampled;
    public event Action<IReadOnlyList<ProcSample>>? ProcessesSampled;

    /// <summary>Every 2 s: the classified TCP table plus live attack heuristics over it.</summary>
    public event Action<IReadOnlyList<ConnectionInfo>, IReadOnlyList<SecurityFinding>>? ConnectionsSampled;

    /// <summary>At start and every 2 minutes: firewall, antivirus, Defender detections, failed sign-ins.</summary>
    public event Action<SecurityPosture>? SecuritySampled;

    Task? _loop;

    public void Start() => _loop = Task.Run(LoopAsync);

    async Task LoopAsync()
    {
        _cpu.Sample(); // prime the CPU delta
        try { ProcessesSampled?.Invoke(_processes.Sample()); }
        catch (Exception ex) { Log.Error("processes", ex); }

        // LHM takes several seconds to enumerate devices; CPU/RAM/network tiles shouldn't wait for it.
        // Update() returns nothing until Open() has finished, so the loop can start right away.
        _ = Task.Run(() =>
        {
            _diskModels = SystemStats.DiskModelsByLetter();
            _hardware.Open();
        });

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            do
            {
                try { Sampled?.Invoke(SampleOnce()); }
                catch (Exception ex) { Log.Error("sample", ex); }

                if (_tick % 2 == 0)
                {
                    try { ProcessesSampled?.Invoke(_processes.Sample()); }
                    catch (Exception ex) { Log.Error("processes", ex); }
                }
                else
                {
                    try { SampleConnections(); }
                    catch (Exception ex) { Log.Error("connections", ex); }
                }

                // Event-log and WMI queries take ~1 s; run them off the loop so tiles keep ticking.
                if (_tick % 120 == 0 && Interlocked.Exchange(ref _securityBusy, 1) == 0)
                {
                    _ = Task.Run(() =>
                    {
                        try { SecuritySampled?.Invoke(SecurityService.Collect()); }
                        catch (Exception ex) { Log.Error("security", ex); }
                        finally { Interlocked.Exchange(ref _securityBusy, 0); }
                    });
                }
                _tick++;
            } while (await timer.WaitForNextTickAsync(_cts.Token));
        }
        catch (OperationCanceledException)
        {
        }
    }

    public Snapshot SampleOnce()
    {
        var sensors = _hardware.Update();
        var cpuLoad = _cpu.Sample();
        var mem = SystemStats.Memory() ?? (0UL, 0UL);

        double? cpuTemp = SensorPicker.CpuTemperature(sensors);
        bool hasLhmCpuTemp = cpuTemp is not null;
        bool acpi = false;
        if (cpuTemp is null)
        {
            if (_tick % 5 == 0) _acpiTemp = SystemStats.AcpiTemperature(); // WMI is slow; every 5 s is plenty
            cpuTemp = _acpiTemp;
            acpi = cpuTemp is not null;
        }

        string? cpuName = sensors.FirstOrDefault(s => s.DeviceType == HardwareType.Cpu)?.DeviceName;
        string? gpuName = null;
        double? gpuLoad = null, gpuTemp = null, vramUsed = null, vramTotal = null;
        if (SensorPicker.PrimaryGpuId(sensors) is { } gpuId)
        {
            gpuName = SensorPicker.GpuName(sensors, gpuId);
            gpuLoad = SensorPicker.GpuLoad(sensors, gpuId);
            gpuTemp = SensorPicker.GpuTemperature(sensors, gpuId);
            var (used, total) = SensorPicker.GpuMemory(sensors, gpuId);
            vramUsed = used;
            vramTotal = total;
        }

        var disks = SystemStats.Disks(_diskModels)
            .Select(d => (d, DiskTemperature(sensors, d.Model)))
            .ToList();

        var adapters = _network.Sample();
        if (_tick % 5 == 0) _vpn = _network.Vpn();

        return new Snapshot(cpuLoad, cpuTemp, acpi, cpuName, gpuName, gpuLoad, gpuTemp, vramUsed, vramTotal,
            mem.Item1, mem.Item2, disks, sensors, adapters, hasLhmCpuTemp,
            _hardware.IsOpen || _hardware.InitError is not null, _hardware.InitError, _vpn);
    }

    public (IReadOnlyList<ConnectionInfo> Connections, IReadOnlyList<SecurityFinding> Findings) SampleConnections()
    {
        var connections = ConnectionClassifier.Classify(NetworkService.TcpConnections());
        var findings = _attackWatch.Analyze(connections, DateTime.Now);
        ConnectionsSampled?.Invoke(connections, findings);
        return (connections, findings);
    }

    static double? DiskTemperature(IReadOnlyList<SensorReading> sensors, string? model)
    {
        if (string.IsNullOrEmpty(model)) return null;
        var device = sensors.FirstOrDefault(s => s.DeviceType == HardwareType.Storage &&
                                                 (s.DeviceName.Equals(model, StringComparison.OrdinalIgnoreCase) ||
                                                  model.Contains(s.DeviceName, StringComparison.OrdinalIgnoreCase) ||
                                                  s.DeviceName.Contains(model, StringComparison.OrdinalIgnoreCase)));
        return device is null ? null : SensorPicker.StorageTemperature(sensors, device.DeviceId);
    }

    /// <summary>For --selftest: open hardware without starting the loop.</summary>
    public void OpenForSelfTest()
    {
        _cpu.Sample();
        _hardware.Open();
        _diskModels = SystemStats.DiskModelsByLetter();
    }

    public IReadOnlyList<ProcSample> SampleProcesses() => _processes.Sample();

    public Dictionary<string, double> ProfileDevices() => _hardware.ProfileDevices();

    /// <summary>For --selftest: average cost of each data source, to keep NFR-1 (≤ 2 % CPU) honest.</summary>
    public Dictionary<string, double> Profile(int rounds)
    {
        var totals = new Dictionary<string, double>();
        void Time(string name, Action a)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            a();
            totals[name] = totals.GetValueOrDefault(name) + sw.Elapsed.TotalMilliseconds / rounds;
        }

        for (int i = 0; i < rounds; i++)
        {
            Time("lhmUpdateMs", () => _hardware.Update());
            Time("cpuMs", () => _cpu.Sample());
            Time("memoryMs", () => SystemStats.Memory());
            Time("disksMs", () => SystemStats.Disks(_diskModels));
            Time("networkMs", () => _network.Sample());
            Time("processesMs", () => _processes.Sample());
            Time("acpiMs", () => SystemStats.AcpiTemperature());
            Thread.Sleep(200);
        }
        return totals.ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value, 2));
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(2000); } // let an in-flight sample finish before closing LHM
        catch { }
        _hardware.Dispose();
    }
}
