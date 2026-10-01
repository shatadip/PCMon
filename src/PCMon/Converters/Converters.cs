using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using PCMon.Core;

namespace PCMon.Converters;

/// <summary>Level → theme brush (Ok / Warn / Crit / neutral border).</summary>
public sealed class LevelToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value is Level l
            ? l switch { Level.Ok => "OkBrush", Level.Warn => "WarnBrush", Level.Critical => "CritBrush", _ => "LineBrush" }
            : "LineBrush";
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Colour is never the only signal (NFR-6): amber/red tiles also get an icon.</summary>
public sealed class LevelToGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value switch
        {
            Level.Warn => "",
            Level.Critical => "",
            _ => "",
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>For radio-button "chips": IsChecked = (value == parameter); checking sets value = parameter.</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? System.Convert.ChangeType(parameter, targetType, CultureInfo.InvariantCulture) : Binding.DoNothing;
}

public sealed class EqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is null or "" ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Sensor rows sit two levels deep in the tree; this sizes them so the value columns
/// line up with the header above the tree regardless of window width.
/// </summary>
public sealed class SensorRowWidth : IValueConverter
{
    public static SensorRowWidth Instance { get; } = new();
    const double IndentAndChrome = 85;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double w ? Math.Max(420, w - IndentAndChrome) : 420.0;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
