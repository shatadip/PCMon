using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PCMon.ViewModels;

public abstract class PageViewModel(string title, string glyph) : ObservableObject
{
    public string Title { get; } = title;

    /// <summary>Segoe MDL2 Assets glyph for the nav rail.</summary>
    public string Glyph { get; } = glyph;

    bool _isActive;
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (SetProperty(ref _isActive, value) && value) OnActivated();
        }
    }

    protected virtual void OnActivated() { }
}

/// <summary>Message boxes, kept in one place so confirmations are consistent.</summary>
static class Dialogs
{
    /// <summary>Test seam: when set, replaces the message box (message, title, kind) → "Yes" for confirmations.</summary>
    internal static Func<string, string, MessageBoxImage, bool>? Hook;

    static Window? Owner => Application.Current?.MainWindow;

    static MessageBoxResult Show(string message, string title, MessageBoxButton buttons, MessageBoxImage kind)
    {
        if (Hook is { } hook) return hook(message, title, kind) ? MessageBoxResult.Yes : MessageBoxResult.No;
        return Owner is { } o
            ? MessageBox.Show(o, message, title, buttons, kind, MessageBoxResult.No)
            : MessageBox.Show(message, title, buttons, kind, MessageBoxResult.No);
    }

    public static bool Confirm(string message, string title) =>
        Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    public static void Info(string message, string title = "PCMon") =>
        Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public static void Error(string message, string title = "PCMon") =>
        Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
}
