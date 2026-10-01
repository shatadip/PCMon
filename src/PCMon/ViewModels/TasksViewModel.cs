using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCMon.Core;
using PCMon.Services;

namespace PCMon.ViewModels;

public sealed class ProcessRow(int pid, string name, string path, bool isCritical) : ObservableObject
{
    public int Pid { get; } = pid;
    public string Name { get; } = name;
    public string Path { get; } = path;
    public bool IsCritical { get; } = isCritical;

    double _cpu, _ramMb;
    public double Cpu { get => _cpu; set => SetProperty(ref _cpu, value); }
    public double RamMb { get => _ramMb; set => SetProperty(ref _ramMb, value); }

    ProcStatus _status;
    public ProcStatus Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(StatusText));
        }
    }

    public string StatusText => ProcStatusText.Of(Status);
}

public sealed class TaskRow(ScheduledTaskInfo t)
{
    public string Name { get; } = t.Name;
    public string Folder { get; } = t.Folder;
    public int State { get; } = t.State;
    public string StateText { get; } = TaskText.State(t.State);
    public DateTime? LastRun { get; } = t.LastRun;
    public DateTime? NextRun { get; } = t.NextRun;
    public string LastRunText { get; } = t.LastRun?.ToString("yyyy-MM-dd HH:mm") ?? "Never";
    public string NextRunText { get; } = t.NextRun?.ToString("yyyy-MM-dd HH:mm") ?? "—";
    public string ResultText { get; } = TaskText.Result(t.LastResult);
}

/// <summary>Running vs. not: processes with End Task, plus read-only Scheduled Tasks (SRS FR-12..14).</summary>
public sealed class TasksViewModel : PageViewModel
{
    readonly Dictionary<int, ProcessRow> _byPid = [];
    bool _tasksLoaded;

    public TasksViewModel() : base("Tasks", "")
    {
        var view = (ListCollectionView)CollectionViewSource.GetDefaultView(Processes);
        view.SortDescriptions.Add(new SortDescription(nameof(ProcessRow.Cpu), ListSortDirection.Descending));
        view.IsLiveSorting = true;
        foreach (var p in new[] { nameof(ProcessRow.Cpu), nameof(ProcessRow.RamMb), nameof(ProcessRow.StatusText) })
            view.LiveSortingProperties.Add(p);
        view.Filter = o => o is ProcessRow r && Matches(r);
        ProcessesView = view;

        var tasksView = (ListCollectionView)CollectionViewSource.GetDefaultView(ScheduledTasks);
        tasksView.Filter = o => o is TaskRow t && TaskFilter switch
        {
            "Running" => t.State == 4,
            "NotRunning" => t.State != 4,
            _ => true,
        };
        tasksView.SortDescriptions.Add(new SortDescription(nameof(TaskRow.Name), ListSortDirection.Ascending));
        ScheduledTasksView = tasksView;

        EndTaskCommand = new AsyncRelayCommand<ProcessRow?>(EndTaskAsync);
        LoadTasksCommand = new AsyncRelayCommand(LoadTasksAsync);
    }

    public ObservableCollection<ProcessRow> Processes { get; } = [];
    public ICollectionView ProcessesView { get; }
    public ObservableCollection<TaskRow> ScheduledTasks { get; } = [];
    public ICollectionView ScheduledTasksView { get; }
    public IAsyncRelayCommand<ProcessRow?> EndTaskCommand { get; }
    public IAsyncRelayCommand LoadTasksCommand { get; }

    string _tab = "Processes";
    public string Tab
    {
        get => _tab;
        set
        {
            if (SetProperty(ref _tab, value) && value == "Scheduled" && !_tasksLoaded) LoadTasksCommand.Execute(null);
        }
    }

    string _search = "";
    public string SearchText
    {
        get => _search;
        set
        {
            if (SetProperty(ref _search, value)) ProcessesView.Refresh();
        }
    }

    ProcessRow? _selected;
    public ProcessRow? SelectedProcess { get => _selected; set => SetProperty(ref _selected, value); }

    string _processSummary = "";
    public string ProcessSummary { get => _processSummary; private set => SetProperty(ref _processSummary, value); }

    string _taskFilter = "All";
    public string TaskFilter
    {
        get => _taskFilter;
        set
        {
            if (SetProperty(ref _taskFilter, value)) ScheduledTasksView.Refresh();
        }
    }

    bool _isLoadingTasks;
    public bool IsLoadingTasks { get => _isLoadingTasks; private set => SetProperty(ref _isLoadingTasks, value); }

    string _taskSummary = "";
    public string TaskSummary { get => _taskSummary; private set => SetProperty(ref _taskSummary, value); }

    public IReadOnlyDictionary<int, string> ProcessNames { get; private set; } = new Dictionary<int, string>();

    bool Matches(ProcessRow r) =>
        string.IsNullOrWhiteSpace(SearchText) ||
        r.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
        r.Path.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
        r.Pid.ToString() == SearchText.Trim();

    public void ApplyProcesses(IReadOnlyList<ProcSample> samples)
    {
        var live = new HashSet<int>();
        foreach (var s in samples)
        {
            live.Add(s.Pid);
            if (_byPid.TryGetValue(s.Pid, out var row) && row.Name != s.Name)
            {
                Processes.Remove(row); // PID was reused by a different process
                row = null;
            }
            if (row is null)
            {
                row = new ProcessRow(s.Pid, s.Name, s.Path, s.IsCritical);
                _byPid[s.Pid] = row;
                Processes.Add(row);
            }
            row.Cpu = s.Cpu;
            row.RamMb = s.RamMb;
            row.Status = s.Status;
        }

        foreach (var gone in _byPid.Keys.Where(pid => !live.Contains(pid)).ToList())
        {
            Processes.Remove(_byPid[gone]);
            _byPid.Remove(gone);
        }

        ProcessNames = samples.GroupBy(s => s.Pid).ToDictionary(g => g.Key, g => g.First().Name);
        int hung = samples.Count(s => s.Status == ProcStatus.NotResponding);
        int suspended = samples.Count(s => s.Status == ProcStatus.Suspended);
        ProcessSummary = $"{samples.Count} processes · {hung} not responding · {suspended} suspended";
    }

    public void Select(int pid)
    {
        Tab = "Processes";
        SearchText = "";
        if (_byPid.TryGetValue(pid, out var row)) SelectedProcess = row;
    }

    async Task EndTaskAsync(ProcessRow? row)
    {
        row ??= SelectedProcess;
        if (row is null) return;

        if (row.IsCritical)
        {
            Dialogs.Info(Strings.KillBlocked(row.Name), "End task");
            return;
        }
        if (!Dialogs.Confirm(Strings.EndTaskConfirm(row.Name), "End task")) return;

        try
        {
            await Task.Run(() => ProcessService.Kill(row.Pid));
            Log.Info($"Ended {row.Name} ({row.Pid})");
        }
        catch (Exception ex)
        {
            Log.Error("kill " + row.Name, ex);
            Dialogs.Error(Strings.KillFailed(row.Name, ex.Message), "End task");
        }
    }

    async Task LoadTasksAsync()
    {
        IsLoadingTasks = true;
        TaskSummary = Strings.LoadingTasks;
        try
        {
            var tasks = await Task.Run(ScheduledTaskService.Load);
            ScheduledTasks.Clear();
            foreach (var t in tasks) ScheduledTasks.Add(new TaskRow(t));
            _tasksLoaded = true;
            TaskSummary = $"{tasks.Count} tasks · {tasks.Count(t => t.State == 4)} running · " +
                          $"{tasks.Count(t => t.State == 3)} ready · {tasks.Count(t => t.State == 1)} disabled";
        }
        catch (Exception ex)
        {
            Log.Error("scheduled tasks", ex);
            TaskSummary = "Task Scheduler didn't answer: " + ex.Message;
        }
        finally
        {
            IsLoadingTasks = false;
        }
    }
}
