using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PCMon.Services;
using PCMon.ViewModels;

namespace PCMon;

public partial class MainWindow : Window
{
    readonly AppSettings _settings;
    readonly MainViewModel _vm;

    public MainWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        _vm = new MainViewModel(settings, Dispatcher);
        DataContext = _vm;

        Width = Math.Max(MinWidth, settings.Width);
        Height = Math.Max(MinHeight, settings.Height);
        if (settings.Maximized) WindowState = WindowState.Maximized;

        SourceInitialized += (_, _) => UseDarkTitleBar(App.IsDark);
        Loaded += (_, _) => _vm.Start();
        Closing += (_, _) => SaveSettings();
        Closed += (_, _) => _vm.Dispose();
    }

    void SaveSettings()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        _settings.Width = bounds.Width;
        _settings.Height = bounds.Height;
        _settings.Maximized = WindowState == WindowState.Maximized;
        SettingsStore.Save(_settings);
    }

    void UseDarkTitleBar(bool dark)
    {
        try
        {
            int value = dark ? 1 : 0;
            Native.DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
        }
        catch (Exception ex)
        {
            Log.Error("dark title bar", ex);
        }
    }

    /// <summary><c>--screenshot dir</c>: visit every page, save PNGs (used for the README and visual checks), then exit.</summary>
    public async Task CaptureScreenshotsAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        await Task.Delay(6000); // let sparklines collect a few points

        async Task Shot(string name, PageViewModel page, Func<bool>? ready = null, int settleMs = 2500)
        {
            _vm.CurrentPage = page;
            for (int i = 0; i < 120 && ready is not null && !ready(); i++) await Task.Delay(250);
            await Task.Delay(settleMs);
            Save(Path.Combine(dir, name + ".png"));
        }

        await Shot("dashboard", _vm.Dashboard, settleMs: 500);
        await Shot("hardware", _vm.Hardware);
        await Shot("network", _vm.Network);
        await Shot("tasks", _vm.Tasks);
        _vm.Tasks.Tab = "Scheduled";
        await Shot("scheduled", _vm.Tasks, () => !_vm.Tasks.IsLoadingTasks && _vm.Tasks.ScheduledTasks.Count > 0);
        await Shot("unused", _vm.Unused, () => !_vm.Unused.IsBusy && _vm.Unused.Summary != Core.Strings.LoadingApps);
        Close();
    }

    void Save(string path)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var bmp = new RenderTargetBitmap((int)(Root.ActualWidth * dpi.DpiScaleX), (int)(Root.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bmp.Render(Root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }
}
