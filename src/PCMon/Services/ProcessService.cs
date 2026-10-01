using System.Diagnostics;
using System.Runtime.InteropServices;
using PCMon.Core;

namespace PCMon.Services;

public sealed record ProcSample(int Pid, string Name, ProcStatus Status, double Cpu, double RamMb, string Path, bool IsCritical);

/// <summary>
/// Process list with CPU %, memory and status (SRS FR-12).
/// Reads everything from one NtQuerySystemInformation snapshot: Process.GetProcesses() plus
/// TotalProcessorTime costs ~40 ms per refresh (a handle per process, an object per thread); this costs a few.
/// </summary>
sealed class ProcessService
{
    const int SystemProcessInformation = 5;
    const uint StatusInfoLengthMismatch = 0xC0000004;

    // SYSTEM_PROCESS_INFORMATION / SYSTEM_THREAD_INFORMATION offsets on x64 (same layout .NET's own ProcessManager reads).
    const int OffNext = 0x00, OffThreadCount = 0x04, OffUserTime = 0x28, OffKernelTime = 0x30,
              OffNameLength = 0x38, OffNameBuffer = 0x40, OffPid = 0x50, OffWorkingSet = 0x90, OffThreads = 0x100;
    const int ThreadSize = 0x50, OffThreadState = 0x44, OffWaitReason = 0x48;
    const int StateWaiting = 5, WaitReasonSuspended = 5;

    [DllImport("ntdll.dll")]
    static extern uint NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returnLength);

    readonly Dictionary<int, long> _prevCpu = [];
    readonly Dictionary<int, string> _paths = [];
    readonly int _cores = Environment.ProcessorCount;
    IntPtr _buffer;
    int _bufferSize;
    long _prevTicks;

    public IReadOnlyList<ProcSample> Sample()
    {
        long now = Stopwatch.GetTimestamp();
        double elapsed100ns = _prevTicks == 0 ? 0 : (now - _prevTicks) * 10_000_000.0 / Stopwatch.Frequency;
        _prevTicks = now;

        var windows = ScanWindows();
        var raw = IntPtr.Size == 8 ? QuerySnapshot() : null;
        var list = new List<ProcSample>(raw?.Count ?? 300);
        var nextCpu = new Dictionary<int, long>(list.Capacity);

        foreach (var p in raw ?? FallbackSnapshot())
        {
            if (p.Pid == 0) continue; // "Idle" isn't a real process
            double cpu = 0;
            if (elapsed100ns > 0 && _prevCpu.TryGetValue(p.Pid, out var prev))
                cpu = Math.Clamp((p.CpuTime - prev) / (elapsed100ns * _cores) * 100, 0, 100);
            nextCpu[p.Pid] = p.CpuTime;

            var status = windows.TryGetValue(p.Pid, out var hung) && hung ? ProcStatus.NotResponding
                : p.Suspended ? ProcStatus.Suspended
                : ProcStatus.Running;

            if (!_paths.TryGetValue(p.Pid, out var path))
            {
                path = Native.ProcessPath(p.Pid) ?? "";
                _paths[p.Pid] = path;
            }

            list.Add(new ProcSample(p.Pid, p.Name, status, cpu, p.WorkingSet / 1048576.0, path,
                CriticalProcesses.IsCritical(p.Name, p.Pid)));
        }

        _prevCpu.Clear();
        foreach (var kv in nextCpu) _prevCpu[kv.Key] = kv.Value;
        foreach (var pid in _paths.Keys.Where(k => !nextCpu.ContainsKey(k)).ToList())
            _paths.Remove(pid);

        return list;
    }

    readonly record struct RawProcess(int Pid, string Name, long CpuTime, long WorkingSet, bool Suspended);

    List<RawProcess>? QuerySnapshot()
    {
        try
        {
            if (_buffer == IntPtr.Zero) Grow(1 << 20);
            uint status;
            while ((status = NtQuerySystemInformation(SystemProcessInformation, _buffer, _bufferSize, out var needed)) == StatusInfoLengthMismatch)
                Grow(Math.Max(_bufferSize * 2, needed + 65536));
            if (status != 0) return null;

            var list = new List<RawProcess>(400);
            long offset = 0;
            while (true)
            {
                var entry = _buffer + (nint)offset;
                int pid = (int)Marshal.ReadIntPtr(entry, OffPid);
                int threads = Marshal.ReadInt32(entry, OffThreadCount);
                long cpu = Marshal.ReadInt64(entry, OffUserTime) + Marshal.ReadInt64(entry, OffKernelTime);
                long ws = (long)Marshal.ReadIntPtr(entry, OffWorkingSet);
                ushort nameBytes = (ushort)Marshal.ReadInt16(entry, OffNameLength);
                var namePtr = Marshal.ReadIntPtr(entry, OffNameBuffer);
                string name = pid switch
                {
                    0 => "Idle",
                    _ when namePtr == IntPtr.Zero || nameBytes == 0 => "System",
                    _ => StripExe(Marshal.PtrToStringUni(namePtr, nameBytes / 2)),
                };

                // Suspended = every thread is waiting with reason Suspended (UWP apps parked by Windows, debuggee).
                bool suspended = threads > 0;
                for (int t = 0; t < threads && suspended; t++)
                {
                    var thread = entry + OffThreads + t * ThreadSize;
                    suspended = Marshal.ReadInt32(thread, OffThreadState) == StateWaiting &&
                                Marshal.ReadInt32(thread, OffWaitReason) == WaitReasonSuspended;
                }

                list.Add(new RawProcess(pid, name, cpu, ws, suspended));
                int next = Marshal.ReadInt32(entry, OffNext);
                if (next == 0) break;
                offset += next;
            }
            return list;
        }
        catch (Exception ex)
        {
            Log.Error("NtQuerySystemInformation", ex);
            return null;
        }
    }

    void Grow(int size)
    {
        if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
        _buffer = Marshal.AllocHGlobal(size);
        _bufferSize = size;
    }

    static string StripExe(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    /// <summary>Slow path for non-x64 builds: the managed Process API.</summary>
    static IEnumerable<RawProcess> FallbackSnapshot()
    {
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                long cpu = 0;
                try { cpu = p.TotalProcessorTime.Ticks; } catch { }
                yield return new RawProcess(p.Id, p.ProcessName, cpu, p.WorkingSet64, false);
            }
        }
    }

    /// <summary>One EnumWindows pass: pid → "has a hung top-level window".</summary>
    static Dictionary<int, bool> ScanWindows()
    {
        var map = new Dictionary<int, bool>();
        Native.EnumWindows((h, _) =>
        {
            if (Native.IsWindowVisible(h) && Native.GetWindow(h, Native.GW_OWNER) == IntPtr.Zero)
            {
                Native.GetWindowThreadProcessId(h, out var pid);
                bool hungNow = Native.IsHungAppWindow(h) && !Native.IsGhostWindow(h);
                map[pid] = (map.TryGetValue(pid, out var hung) && hung) || hungNow;
            }
            return true;
        }, IntPtr.Zero);
        return map;
    }

    public static void Kill(int pid)
    {
        using var p = Process.GetProcessById(pid);
        p.Kill();
        p.WaitForExit(3000);
    }
}
