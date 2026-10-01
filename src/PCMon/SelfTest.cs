using System.Diagnostics;
using System.Text.Json;
using PCMon.Services;

namespace PCMon;

/// <summary>
/// <c>PCMon.exe --selftest report.json</c>: exercises every data source once, without a window,
/// and writes what it found. Exit code 0 means every required source returned data.
/// </summary>
static class SelfTest
{
    public static int Run(string reportPath)
    {
        var report = new Dictionary<string, object?>();
        var failures = new List<string>();

        T? Step<T>(string name, Func<T> action, bool required = true)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var result = action();
                report[name + "Ms"] = sw.ElapsedMilliseconds;
                return result;
            }
            catch (Exception ex)
            {
                report[name + "Error"] = ex.Message;
                if (required) failures.Add($"{name}: {ex.Message}");
                return default;
            }
        }

        report["version"] = ViewModels.MainViewModel.Version;
        report["elevated"] = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

        using var sampler = new Sampler();
        Step("hardwareOpen", () => { sampler.OpenForSelfTest(); return true; });
        Step("processesPrime", sampler.SampleProcesses);
        Step("snapshotPrime", sampler.SampleOnce);
        Thread.Sleep(1000);

        var snap = Step("snapshot", sampler.SampleOnce);
        if (snap is not null)
        {
            report["hardwareError"] = snap.HardwareError;
            report["devices"] = snap.Sensors.Select(s => s.DeviceName).Distinct().ToList();
            report["sensorCount"] = snap.Sensors.Count;
            report["cpuName"] = snap.CpuName;
            report["cpuLoad"] = snap.CpuLoad;
            report["cpuTemp"] = snap.CpuTemp;
            report["cpuTempSource"] = snap.HasLhmCpuTemp ? "LibreHardwareMonitor" : snap.CpuTempIsAcpi ? "ACPI" : "none";
            report["gpuName"] = snap.GpuName;
            report["gpuLoad"] = snap.GpuLoad;
            report["gpuTemp"] = snap.GpuTemp;
            report["vramUsedMb"] = snap.VramUsedMb;
            report["vramTotalMb"] = snap.VramTotalMb;
            report["ramUsedGb"] = Math.Round(snap.RamUsed / 1073741824.0, 2);
            report["ramTotalGb"] = Math.Round(snap.RamTotal / 1073741824.0, 2);
            report["disks"] = snap.Disks.Select(d => new { d.Disk.Letter, used = Math.Round(d.Disk.UsedPercent, 1), d.Disk.Model, d.Temp }).ToList();
            report["adapters"] = snap.Adapters.Select(a => new { a.Name, a.Kind, a.Ipv4, a.DownPerSec, a.UpPerSec }).ToList();
            if (snap.RamTotal == 0) failures.Add("memory: total RAM is 0");
            if (snap.CpuLoad is null) failures.Add("cpu: no load reading");
        }

        var procs = Step("processes", sampler.SampleProcesses);
        if (procs is not null)
        {
            report["processCount"] = procs.Count;
            report["processesWithCpu"] = procs.Count(p => p.Cpu > 0);
            report["notResponding"] = procs.Where(p => p.Status == Core.ProcStatus.NotResponding).Select(p => p.Name).ToList();
            report["suspendedCount"] = procs.Count(p => p.Status == Core.ProcStatus.Suspended);
            if (!procs.Any(p => p.Pid == Environment.ProcessId)) failures.Add("processes: own process missing");
        }

        report["profile"] = Step("profile", () => sampler.Profile(5), required: false);
        report["profileDevices"] = Step("profileDevices", sampler.ProfileDevices, required: false);

        var conns = Step("connections", sampler.SampleConnections);
        if (conns.Connections is not null)
        {
            var c = conns.Connections;
            report["tcpConnections"] = c.Count;
            report["tcpV6"] = c.Count(x => x.Row.Protocol == "TCPv6");
            report["incoming"] = c.Count(x => x.Direction == Core.Direction.Incoming);
            report["outgoing"] = c.Count(x => x.Direction == Core.Direction.Outgoing);
            report["listening"] = c.Count(x => x.Direction == Core.Direction.Listening);
            report["toInternet"] = c.Count(x => x.RemoteScope == "Internet");
            report["liveFindings"] = conns.Findings.Select(f => $"{f.Level}: {f.Title}").ToList();
        }

        report["vpn"] = Step("vpn", () => new NetworkService().Vpn());

        var posture = Step("security", SecurityService.Collect);
        if (posture is not null)
        {
            report["firewall"] = posture.Firewall;
            report["antivirus"] = posture.Antivirus;
            report["detections"] = posture.Detections.Count;
            report["failedSignIns24h"] = posture.FailedSignIns.Count;
            report["failedRemoteSignIns24h"] = posture.FailedSignIns.Count(f => f.IsRemote);
            report["signInLogError"] = posture.SignInLogError;
            report["postureFindings"] = Core.SecurityAnalysis.Posture(posture, DateTime.Now)
                .Select(f => $"{f.Level}: {f.Title}").ToList();
        }

        var tasks = Step("scheduledTasks", ScheduledTaskService.Load);
        if (tasks is not null)
        {
            report["scheduledTaskCount"] = tasks.Count;
            report["scheduledTaskStates"] = tasks.GroupBy(t => Core.TaskText.State(t.State)).ToDictionary(g => g.Key, g => g.Count());
            if (tasks.Count == 0) failures.Add("scheduledTasks: none found");
        }

        var apps = Step("installedApps", InstalledAppsService.Load);
        if (apps is not null)
        {
            report["installedAppCount"] = apps.Count;
            report["appsWithLastUsed"] = apps.Count(a => a.LastUsed is not null);
            report["appsUnused90"] = apps.Count(a => Core.UnusedRule.IsUnused(a.LastUsed, 90, DateTime.Now, includeUnknown: false));
            report["appSample"] = apps.Take(8).Select(a => new { a.Name, a.LastUsed, a.LastUsedSource }).ToList();
            if (apps.Count == 0) failures.Add("installedApps: none found");
        }

        report["failures"] = failures;
        report["ok"] = failures.Count == 0;

        try
        {
            File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Error("selftest report", ex);
            return 2;
        }
        return failures.Count == 0 ? 0 : 1;
    }
}
