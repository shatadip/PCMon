using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using PCMon.Services;

namespace PCMon;

public partial class App : Application
{
    public static bool IsDark { get; private set; } = true;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("unhandled", args.ExceptionObject as Exception ?? new Exception("unknown"));

        var args = e.Args;
        string? Arg(string name)
        {
            int i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        // Headless check used by CI and the test suite: sample every service once, write JSON, exit.
        if (Arg("--selftest") is { } reportPath)
        {
            Shutdown(SelfTest.Run(reportPath));
            return;
        }

        var settings = SettingsStore.Load();
        ApplyTheme(Arg("--theme") ?? settings.Theme);

        var window = new MainWindow(settings);
        MainWindow = window;
        window.Show();

        if (Arg("--screenshot") is { } dir)
            _ = window.CaptureScreenshotsAsync(dir);
    }

    void ApplyTheme(string theme)
    {
        IsDark = theme.ToLowerInvariant() switch
        {
            "dark" => true,
            "light" => false,
            _ => WindowsUsesDarkApps(),
        };
        if (!IsDark)
            Resources.MergedDictionaries[0] = new ResourceDictionary { Source = new Uri("Themes/Light.xaml", UriKind.Relative) };
    }

    static bool WindowsUsesDarkApps()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch
        {
            return false;
        }
    }

    // NFR-4: the UI never shows a raw exception. Log it and carry on.
    void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("ui", e.Exception);
        e.Handled = true;
    }
}
