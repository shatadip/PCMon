using System.Windows;
using System.Windows.Media;

namespace PCMon.Controls;

/// <summary>
/// A hand-drawn line chart: one or two series, optional fixed ceiling. Far lighter than a
/// charting library for 60–120 points refreshed every second.
/// </summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = Register(nameof(Values), typeof(IReadOnlyList<double>));
    public static readonly DependencyProperty Values2Property = Register(nameof(Values2), typeof(IReadOnlyList<double>));
    public static readonly DependencyProperty MaximumProperty = Register(nameof(Maximum), typeof(double), double.NaN);
    public static readonly DependencyProperty CapacityProperty = Register(nameof(Capacity), typeof(int), 60);
    public static readonly DependencyProperty StrokeProperty = Register(nameof(Stroke), typeof(Brush), Brushes.DodgerBlue);
    public static readonly DependencyProperty Stroke2Property = Register(nameof(Stroke2), typeof(Brush), Brushes.Orange);

    static DependencyProperty Register(string name, Type type, object? defaultValue = null) =>
        DependencyProperty.Register(name, type, typeof(Sparkline),
            new FrameworkPropertyMetadata(defaultValue, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values { get => (IReadOnlyList<double>?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public IReadOnlyList<double>? Values2 { get => (IReadOnlyList<double>?)GetValue(Values2Property); set => SetValue(Values2Property, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public int Capacity { get => (int)GetValue(CapacityProperty); set => SetValue(CapacityProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public Brush Stroke2 { get => (Brush)GetValue(Stroke2Property); set => SetValue(Stroke2Property, value); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h)); // hit-testable, no visual

        double max = Maximum;
        if (double.IsNaN(max))
        {
            max = 0;
            foreach (var v in Values ?? Array.Empty<double>()) max = Math.Max(max, v);
            foreach (var v in Values2 ?? Array.Empty<double>()) max = Math.Max(max, v);
            max = max <= 0 ? 1 : max * 1.15;
        }

        if (Values2 is { Count: > 1 } v2) DrawSeries(dc, v2, max, w, h, Stroke2, fill: false);
        if (Values is { Count: > 1 } v1) DrawSeries(dc, v1, max, w, h, Stroke, fill: true);
    }

    void DrawSeries(DrawingContext dc, IReadOnlyList<double> values, double max, double w, double h, Brush stroke, bool fill)
    {
        int capacity = Math.Max(Capacity, values.Count);
        double step = w / (capacity - 1);
        double x0 = w - step * (values.Count - 1); // newest point is always at the right edge

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            for (int i = 0; i < values.Count; i++)
            {
                var p = new Point(x0 + i * step, h - Math.Clamp(values[i] / max, 0, 1) * (h - 2) - 1);
                if (i == 0) ctx.BeginFigure(p, false, false);
                else ctx.LineTo(p, true, true);
            }
        }
        line.Freeze();

        if (fill && stroke is SolidColorBrush solid)
        {
            var area = new StreamGeometry();
            using (var ctx = area.Open())
            {
                ctx.BeginFigure(new Point(x0, h), true, true);
                for (int i = 0; i < values.Count; i++)
                    ctx.LineTo(new Point(x0 + i * step, h - Math.Clamp(values[i] / max, 0, 1) * (h - 2) - 1), false, true);
                ctx.LineTo(new Point(w, h), false, true);
            }
            area.Freeze();
            var fillBrush = new SolidColorBrush(Color.FromArgb(48, solid.Color.R, solid.Color.G, solid.Color.B));
            fillBrush.Freeze();
            dc.DrawGeometry(fillBrush, null, area);
        }

        var pen = new Pen(stroke, 1.5) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, line);
    }
}
