using System.Reflection;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCMon.Core;
using PCMon.Services;

namespace PCMon.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    readonly Sampler _sampler = new();
    readonly Dispatcher _dispatcher;
    int _snapshotPending, _processesPending;
    double _netIdleSeconds;

    public MainViewModel(AppSettings settings, Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        Dashboard = new DashboardViewModel(OpenProcess);
        Hardware = new HardwareViewModel();
        Tasks = new TasksViewModel();
        Network = new NetworkViewModel(() => Tasks.ProcessNames);
        Unused = new UnusedAppsViewModel(settings);
        Pages = [Dashboard, Hardware, Network, Tasks, Unused];
        _current = Dashboard;
        Dashboard.IsActive = true;

        AboutCommand = new RelayCommand(() =>
            Dialogs.Info(string.Format(Strings.About, Version), "About " + Strings.AppName));
        GoToPageCommand = new RelayCommand<string>(i =>
        {
            if (int.TryParse(i, out var n) && n >= 1 && n <= Pages.Count) CurrentPage = Pages[n - 1];
        });

        _sampler.Sampled += OnSampled;
        _sampler.ProcessesSampled += OnProcessesSampled;
        _sampler.ConnectionsSampled += (connections, findings) => _dispatcher.BeginInvoke(() =>
        {
            try { Network.ApplyConnections(connections, findings); }
            catch (Exception ex) { Log.Error("apply connections", ex); }
        });
        _sampler.SecuritySampled += posture => _dispatcher.BeginInvoke(() =>
        {
            try { Network.ApplySecurity(posture); }
            catch (Exception ex) { Log.Error("apply security", ex); }
        });
    }

    public static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    public DashboardViewModel Dashboard { get; }
    public HardwareViewModel Hardware { get; }
    public NetworkViewModel Network { get; }
    public TasksViewModel Tasks { get; }
    public UnusedAppsViewModel Unused { get; }
    public IReadOnlyList<PageViewModel> Pages { get; }
    public IRelayCommand AboutCommand { get; }
    public IRelayCommand<string> GoToPageCommand { get; }

    PageViewModel _current;
    public PageViewModel CurrentPage
    {
        get => _current;
        set
        {
            if (value is null || _current == value) return;
            _current.IsActive = false;
            SetProperty(ref _current, value);
            value.IsActive = true;
        }
    }

    string _status = Strings.Starting;
    public string StatusText { get => _status; private set => SetProperty(ref _status, value); }

    public string AdminText =>
        new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator)
            ? "Admin ✓"
            : "Not elevated: some sensors hidden";

    public void Start() => _sampler.Start();

    void OpenProcess(int pid)
    {
        CurrentPage = Tasks;
        Tasks.Select(pid);
    }

    // Drop a sample if the UI hasn't drawn the previous one yet, rather than queueing up a backlog.
    void OnSampled(Snapshot s)
    {
        if (Interlocked.Exchange(ref _snapshotPending, 1) == 1) return;
        _dispatcher.BeginInvoke(() =>
        {
            try { Apply(s); }
            catch (Exception ex) { Log.Error("apply snapshot", ex); }
            finally { Interlocked.Exchange(ref _snapshotPending, 0); }
        });
    }

    void OnProcessesSampled(IReadOnlyList<ProcSample> list)
    {
        if (Interlocked.Exchange(ref _processesPending, 1) == 1) return;
        _dispatcher.BeginInvoke(() =>
        {
            try
            {
                Tasks.ApplyProcesses(list);
                Dashboard.ApplyProcesses(list);
            }
            catch (Exception ex) { Log.Error("apply processes", ex); }
            finally { Interlocked.Exchange(ref _processesPending, 0); }
        });
    }

    void Apply(Snapshot s)
    {
        Network.Apply(s);
        Dashboard.Apply(s, Network.SecurityLevel, Network.AlertCount);
        Hardware.Apply(s);

        _netIdleSeconds = s.TotalDown + s.TotalUp < 2048 ? _netIdleSeconds + 1 : 0;
        var disks = s.Disks.Select(d => Thresholds.ForDisk(d.Disk.UsedPercent)).ToArray();
        StatusText = StatusMessenger.Pick(new StatusInput(
            Thresholds.ForTemp(s.CpuTemp), Thresholds.ForTemp(s.GpuTemp), Thresholds.ForRam(s.RamPercent),
            Thresholds.Worst(disks), _netIdleSeconds,
            Network.SecurityLevel, Network.SecurityHeadline, Network.AlertCount));
    }

    public void Dispose() => _sampler.Dispose();
}
