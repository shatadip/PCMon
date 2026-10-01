using System.Diagnostics;
using PCMon.Services;
using Xunit;

namespace PCMon.Tests;

/// <summary>Talks to the real machine: processes, network, registry, Task Scheduler, LHM.</summary>
[Trait("Category", "Integration")]
public class IntegrationTests
{
    [Fact]
    public void ProcessSampleIncludesThisProcessWithPath()
    {
        var svc = new ProcessService();
        svc.Sample();
        Thread.Sleep(300);
        var list = svc.Sample();
        var me = Assert.Single(list, p => p.Pid == Environment.ProcessId);
        Assert.False(string.IsNullOrEmpty(me.Path));
        Assert.True(me.RamMb > 0);
        Assert.Contains(list, p => p.IsCritical);
    }

    [Fact]
    public void RawSnapshotAgreesWithManagedProcessApi()
    {
        var svc = new ProcessService();
        svc.Sample();

        // burn some CPU on this process so the delta is measurable
        var until = DateTime.UtcNow.AddMilliseconds(400);
        while (DateTime.UtcNow < until) { }

        var ours = svc.Sample().ToDictionary(p => p.Pid);
        var managed = Process.GetProcesses();
        int compared = 0, sameName = 0;
        foreach (var p in managed)
        {
            using (p)
            {
                if (!ours.TryGetValue(p.Id, out var o)) continue;
                compared++;
                if (string.Equals(o.Name, p.ProcessName, StringComparison.OrdinalIgnoreCase)) sameName++;
            }
        }

        Assert.True(compared > 20, $"only {compared} processes in common");
        Assert.True(sameName >= compared * 0.95, $"{sameName}/{compared} names match");

        var me = ours[Environment.ProcessId];
        using var self = Process.GetCurrentProcess();
        Assert.InRange(me.RamMb, self.WorkingSet64 / 1048576.0 * 0.5, self.WorkingSet64 / 1048576.0 * 1.5);
        Assert.True(me.Cpu > 0, "busy loop should register CPU time");
        Assert.Equal(Core.ProcStatus.Running, me.Status);
        Assert.True(ours[4].IsCritical);
    }

    [Fact]
    public void KillEndsAChildProcess()
    {
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
        ProcessService.Kill(p.Id);
        Assert.True(p.HasExited);
    }

    [Fact]
    public void CpuUsageIsAPercentageOnSecondSample()
    {
        var cpu = new CpuUsage();
        Assert.Null(cpu.Sample());
        Thread.Sleep(250);
        var v = cpu.Sample();
        Assert.NotNull(v);
        Assert.InRange(v!.Value, 0, 100);
    }

    [Fact]
    public void MemoryIsReported()
    {
        var m = SystemStats.Memory();
        Assert.NotNull(m);
        Assert.True(m!.Value.Total > 0 && m.Value.Used <= m.Value.Total);
    }

    [Fact]
    public void FixedDisksAreListed()
    {
        var disks = SystemStats.Disks(new Dictionary<string, string>());
        Assert.NotEmpty(disks);
        Assert.All(disks, d => Assert.InRange(d.UsedPercent, 0, 100));
    }

    [Fact]
    public void NetworkSamplingDoesNotThrowAndRatesAreNonNegative()
    {
        var net = new NetworkService();
        net.Sample();
        Thread.Sleep(250);
        Assert.All(net.Sample(), a => Assert.True(a.DownPerSec >= 0 && a.UpPerSec >= 0));
    }

    [Fact]
    public void TcpTableParses()
    {
        var rows = NetworkService.TcpConnections();
        Assert.All(rows, r => Assert.Contains(":", r.Local));
    }

    [Fact]
    public void ScheduledTasksAreFound()
    {
        var tasks = ScheduledTaskService.Load();
        Assert.NotEmpty(tasks);
        Assert.All(tasks, t => Assert.False(string.IsNullOrEmpty(t.Name)));
    }

    [Fact]
    public void InstalledAppsAreFoundAndFiltered()
    {
        var apps = InstalledAppsService.Load();
        Assert.NotEmpty(apps);
        Assert.All(apps, a => Assert.False(string.IsNullOrWhiteSpace(a.UninstallString)));
        Assert.Equal(apps.Count, apps.Select(a => a.Name + "|" + a.Version).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void HardwareOpensAndUpdatesWithoutThrowing()
    {
        using var hw = new HardwareService();
        hw.Open();
        var sensors = hw.Update();
        Assert.All(sensors, s => Assert.False(string.IsNullOrEmpty(s.Id)));
    }

    [Fact]
    public void SamplerSnapshotHasCoreReadings()
    {
        using var sampler = new Sampler();
        sampler.OpenForSelfTest();
        sampler.SampleOnce();
        Thread.Sleep(300);
        var s = sampler.SampleOnce();
        Assert.NotNull(s.CpuLoad);
        Assert.True(s.RamTotal > 0);
        Assert.True(s.HardwareReady);
    }
}
