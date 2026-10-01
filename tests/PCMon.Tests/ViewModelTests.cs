using System.Diagnostics;
using System.Windows;
using PCMon.Core;
using PCMon.Services;
using PCMon.ViewModels;
using Xunit;

namespace PCMon.Tests;

/// <summary>Dialogs.Hook is static, so these run one at a time.</summary>
[Collection("Dialogs")]
public sealed class TasksViewModelTests : IDisposable
{
    readonly List<(string Message, MessageBoxImage Kind)> _shown = [];
    bool _answer;

    public TasksViewModelTests() => Dialogs.Hook = (m, _, k) => { _shown.Add((m, k)); return _answer; };
    public void Dispose() => Dialogs.Hook = null;

    static ProcSample P(int pid, string name, double cpu = 0, bool critical = false) =>
        new(pid, name, ProcStatus.Running, cpu, 10, "", critical);

    [Fact]
    public async Task CriticalProcessIsNeverKilled()
    {
        var vm = new TasksViewModel();
        vm.ApplyProcesses([P(700, "lsass", critical: true)]);
        _answer = true;

        await vm.EndTaskCommand.ExecuteAsync(vm.Processes[0]);

        var (message, kind) = Assert.Single(_shown);
        Assert.Equal(Strings.KillBlocked("lsass"), message);
        Assert.Equal(MessageBoxImage.Information, kind);
    }

    [Fact]
    public async Task DecliningConfirmationLeavesProcessRunning()
    {
        using var child = StartSleeper();
        var vm = new TasksViewModel();
        vm.ApplyProcesses([P(child.Id, "cmd")]);
        _answer = false;

        await vm.EndTaskCommand.ExecuteAsync(vm.Processes[0]);

        Assert.Equal(Strings.EndTaskConfirm("cmd"), Assert.Single(_shown).Message);
        Assert.False(child.HasExited);
        child.Kill();
    }

    [Fact]
    public async Task ConfirmingEndsTheProcess()
    {
        using var child = StartSleeper();
        var vm = new TasksViewModel();
        vm.ApplyProcesses([P(child.Id, "cmd")]);
        vm.SelectedProcess = vm.Processes[0];
        _answer = true;

        await vm.EndTaskCommand.ExecuteAsync(null); // keyboard path: acts on the selection

        Assert.True(child.WaitForExit(3000));
    }

    [Fact]
    public void RowsUpdateInPlaceAndPidReuseReplacesRow()
    {
        var vm = new TasksViewModel();
        vm.ApplyProcesses([P(10, "a", 1), P(11, "b", 2)]);
        var rowA = vm.Processes.Single(r => r.Pid == 10);

        vm.ApplyProcesses([P(10, "a", 50), P(11, "c", 2)]);

        Assert.Same(rowA, vm.Processes.Single(r => r.Pid == 10));
        Assert.Equal(50, rowA.Cpu);
        Assert.Equal("c", vm.Processes.Single(r => r.Pid == 11).Name);
        Assert.Equal(2, vm.Processes.Count);
    }

    [Fact]
    public void SearchFiltersByNameOrPid()
    {
        var vm = new TasksViewModel();
        vm.ApplyProcesses([P(10, "chrome"), P(11, "notepad")]);
        vm.SearchText = "note";
        Assert.Equal(["notepad"], vm.ProcessesView.Cast<ProcessRow>().Select(r => r.Name));
        vm.SearchText = "10";
        Assert.Equal(["chrome"], vm.ProcessesView.Cast<ProcessRow>().Select(r => r.Name));
    }

    static Process StartSleeper() =>
        Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 >nul") { CreateNoWindow = true, UseShellExecute = false })!;
}

[Collection("Dialogs")]
public sealed class UnusedAppsViewModelTests : IDisposable
{
    readonly List<string> _shown = [];

    public UnusedAppsViewModelTests() => Dialogs.Hook = (m, _, _) => { _shown.Add(m); return false; };
    public void Dispose() => Dialogs.Hook = null;

    [Fact]
    public async Task UninstallAsksFirstAndDoesNothingWhenDeclined()
    {
        var vm = new UnusedAppsViewModel(new AppSettings { UnusedDays = 0 });
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.NotEmpty(vm.Apps);
        Assert.False(vm.UninstallSelectedCommand.CanExecute(null)); // nothing selected yet

        vm.Apps[0].IsSelected = true;
        Assert.True(vm.UninstallSelectedCommand.CanExecute(null));

        await vm.UninstallSelectedCommand.ExecuteAsync(null);

        var message = Assert.Single(_shown);
        Assert.Contains(vm.Apps.First(a => a.IsSelected).Name, message);
        Assert.StartsWith("Uninstall 1 app(s)?", message);
    }

    [Fact]
    public async Task DaysFilterNarrowsList()
    {
        var settings = new AppSettings { UnusedDays = 0 };
        var vm = new UnusedAppsViewModel(settings);
        await vm.LoadCommand.ExecuteAsync(null);
        int all = vm.Apps.Count;

        vm.IncludeUnknown = false;
        vm.Days = 30;
        Assert.True(vm.Apps.Count <= all);
        Assert.All(vm.Apps, a => Assert.True(a.App.LastUsed is { } d && (DateTime.Now - d).TotalDays >= 30));
        Assert.Equal(30, settings.UnusedDays); // persisted choice
    }
}
