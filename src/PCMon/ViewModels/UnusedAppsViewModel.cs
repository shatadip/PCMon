using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCMon.Core;
using PCMon.Services;

namespace PCMon.ViewModels;

public sealed class AppRow : ObservableObject
{
    public AppRow(InstalledApp app, DateTime now)
    {
        App = app;
        LastUsedText = app.LastUsed is { } d ? Format.DaysAgo(d, now) : Strings.UnknownUsage;
        LastUsedTooltip = app.LastUsed is { } u ? $"{u:yyyy-MM-dd HH:mm} (from {app.LastUsedSource})" : "No Prefetch or UserAssist record";
    }

    public InstalledApp App { get; }
    public string Name => App.Name;
    public string Publisher => App.Publisher;
    public string Version => App.Version;
    public long SizeBytes => App.SizeBytes;
    public string SizeText => App.SizeBytes > 0 ? Format.Bytes(App.SizeBytes) : "—";
    public DateTime InstalledSort => App.InstalledOn ?? DateTime.MinValue;
    public string InstalledText => App.InstalledOn?.ToString("yyyy-MM-dd") ?? "—";
    public DateTime LastUsedSort => App.LastUsed ?? DateTime.MinValue;
    public string LastUsedText { get; }
    public string LastUsedTooltip { get; }

    bool _isSelected;
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
}

/// <summary>Installed apps not opened in N days, removed with their own uninstallers (SRS FR-15..19).</summary>
public sealed class UnusedAppsViewModel : PageViewModel
{
    readonly AppSettings _settings;
    List<InstalledApp> _all = [];
    bool _loaded;

    public UnusedAppsViewModel(AppSettings settings) : base("Unused apps", "")
    {
        _settings = settings;
        _days = settings.UnusedDays;
        _includeUnknown = settings.IncludeUnknownUsage;
        LoadCommand = new AsyncRelayCommand(LoadAsync);
        UninstallSelectedCommand = new AsyncRelayCommand(UninstallSelectedAsync,
            () => !IsBusy && Apps.Any(a => a.IsSelected));
    }

    public ObservableCollection<AppRow> Apps { get; } = [];
    public IAsyncRelayCommand LoadCommand { get; }
    public IAsyncRelayCommand UninstallSelectedCommand { get; }

    int _days;
    public int Days
    {
        get => _days;
        set
        {
            if (!SetProperty(ref _days, value)) return;
            _settings.UnusedDays = value;
            ApplyFilter();
        }
    }

    bool _includeUnknown;
    public bool IncludeUnknown
    {
        get => _includeUnknown;
        set
        {
            if (!SetProperty(ref _includeUnknown, value)) return;
            _settings.IncludeUnknownUsage = value;
            ApplyFilter();
        }
    }

    bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(ShowEmpty));
            UninstallSelectedCommand.NotifyCanExecuteChanged();
        }
    }

    public bool ShowEmpty => !IsBusy && _loaded && Apps.Count == 0;

    string _summary = "";
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

    protected override void OnActivated()
    {
        if (!_loaded && !IsBusy) LoadCommand.Execute(null);
    }

    async Task LoadAsync()
    {
        IsBusy = true;
        Summary = Strings.LoadingApps;
        try
        {
            _all = (await Task.Run(InstalledAppsService.Load)).ToList();
            _loaded = true;
        }
        catch (Exception ex)
        {
            Log.Error("installed apps", ex);
        }
        finally
        {
            IsBusy = false;
        }
        ApplyFilter();
    }

    void ApplyFilter()
    {
        var now = DateTime.Now;
        foreach (var row in Apps) row.PropertyChanged -= OnRowChanged;
        Apps.Clear();

        var rows = _all
            .Where(a => UnusedRule.IsUnused(a.LastUsed, Days, now, IncludeUnknown))
            .OrderBy(a => a.LastUsed is null ? 1 : 0) // known-stale apps first, unknowns after
            .ThenBy(a => a.LastUsed)
            .ThenBy(a => a.Name)
            .Select(a => new AppRow(a, now));
        foreach (var row in rows)
        {
            row.PropertyChanged += OnRowChanged;
            Apps.Add(row);
        }

        var total = Apps.Sum(a => a.SizeBytes);
        Summary = _loaded
            ? $"{Apps.Count} of {_all.Count} installed apps · {Format.Bytes(total)} reclaimable"
            : Summary;
        OnPropertyChanged(nameof(ShowEmpty));
        UninstallSelectedCommand.NotifyCanExecuteChanged();
    }

    void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppRow.IsSelected)) UninstallSelectedCommand.NotifyCanExecuteChanged();
    }

    async Task UninstallSelectedAsync()
    {
        var selected = Apps.Where(a => a.IsSelected).ToList();
        if (selected.Count == 0) return;
        if (!Dialogs.Confirm(Strings.UninstallConfirm(selected.Count, selected.Select(s => s.Name)), "Uninstall apps"))
            return;

        IsBusy = true;
        try
        {
            foreach (var row in selected)
            {
                Summary = $"Running the uninstaller for {row.Name}…";
                try
                {
                    var code = await InstalledAppsService.RunUninstallerAsync(row.App);
                    Log.Info($"Uninstaller for '{row.Name}' exited with {code}");
                }
                catch (Exception ex)
                {
                    Log.Error("uninstall " + row.Name, ex);
                    Dialogs.Error($"Couldn't start the uninstaller for {row.Name}: {ex.Message}", "Uninstall apps");
                }
            }
        }
        finally
        {
            IsBusy = false;
        }
        await LoadAsync(); // FR-19: refresh after the uninstallers finish
    }
}
