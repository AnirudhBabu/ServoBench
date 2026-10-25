using System.Globalization;
using System.Windows;
using System.Windows.Media;
using ServoBench.Presentation;

namespace ServoBench.App.Controls;

/// <summary>
/// Lightweight live trend: speed and setpoint on the left axis, motor current on the right.
/// Drawn directly in OnRender with frozen pens and StreamGeometry, which stays smooth at
/// 10 Hz without pulling in a charting library.
/// </summary>
public sealed class TrendChart : FrameworkElement
{
    public static readonly DependencyProperty TrendProperty = DependencyProperty.Register(
        nameof(Trend),
        typeof(TrendViewModel),
        typeof(TrendChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnTrendChanged));

    private const double LeftMargin = 56;
    private const double RightMargin = 52;
    private const double TopMargin = 26;
    private const double BottomMargin = 24;
    private const int GridLines = 4;

    private static readonly Brush BackgroundBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)));
    private static readonly Brush AxisText = Frozen(new SolidColorBrush(Color.FromRgb(0x61, 0x6E, 0x7C)));
    private static readonly Pen GridPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0xE4, 0xE7, 0xEB))), 1));
    private static readonly Pen ZeroPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0xC5, 0xCB, 0xD3))), 1));
    private static readonly Pen SpeedPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x1F, 0x6F, 0xEB))), 2));
    private static readonly Pen CurrentPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0xD9, 0x82, 0x1E))), 1.5));
    private static readonly Pen SetpointPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x52, 0x60, 0x6D))), 1.5)
    {
        DashStyle = DashStyles.Dash,
    });

    private static readonly Typeface Font = new("Segoe UI");

    public TrendViewModel? Trend
    {
        get => (TrendViewModel?)GetValue(TrendProperty);
        set => SetValue(TrendProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        dc.DrawRectangle(BackgroundBrush, null, new Rect(0, 0, width, height));

        var plot = new Rect(
            LeftMargin,
            TopMargin,
            Math.Max(0, width - LeftMargin - RightMargin),
            Math.Max(0, height - TopMargin - BottomMargin));
        if (plot.Width < 20 || plot.Height < 20)
        {
            return;
        }

        var samples = Trend?.Snapshot() ?? Array.Empty<TrendSample>();
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        var speedRange = NiceRange(samples.Count == 0 ? 0 : samples.Max(s => Math.Max(Math.Abs(s.Speed), Math.Abs(s.Setpoint))), minimum: 500);
        var currentRange = NiceRange(samples.Count == 0 ? 0 : samples.Max(s => Math.Abs(s.Current)), minimum: 2);

        DrawGrid(dc, plot, speedRange, currentRange, dpi);
        DrawLegend(dc, plot, dpi);

        if (samples.Count < 2)
        {
            DrawText(dc, "Waiting for data…", new Point(plot.Left + 8, plot.Top + 8), dpi);
            return;
        }

        var t0 = samples[0].Time.TotalSeconds;
        var span = Math.Max(1, samples[^1].Time.TotalSeconds - t0);

        double X(TrendSample s) => plot.Left + ((s.Time.TotalSeconds - t0) / span * plot.Width);
        double YSpeed(double v) => plot.Top + ((1 - ((v + speedRange) / (2 * speedRange))) * plot.Height);
        double YCurrent(double v) => plot.Top + ((1 - ((v + currentRange) / (2 * currentRange))) * plot.Height);

        DrawSeries(dc, samples, SetpointPen, s => new Point(X(s), YSpeed(s.Setpoint)));
        DrawSeries(dc, samples, CurrentPen, s => new Point(X(s), YCurrent(s.Current)));
        DrawSeries(dc, samples, SpeedPen, s => new Point(X(s), YSpeed(s.Speed)));

        DrawText(dc, $"{span:0} s window", new Point(plot.Right - 70, plot.Bottom + 4), dpi);
    }

    private static void OnTrendChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (TrendChart)d;
        if (e.OldValue is TrendViewModel oldTrend)
        {
            oldTrend.Updated -= chart.OnTrendUpdated;
        }

        if (e.NewValue is TrendViewModel newTrend)
        {
            newTrend.Updated += chart.OnTrendUpdated;
        }
    }

    private void OnTrendUpdated(object? sender, EventArgs e) => InvalidateVisual();

    private static void DrawGrid(DrawingContext dc, Rect plot, double speedRange, double currentRange, double dpi)
    {
        for (var i = 0; i <= GridLines; i++)
        {
            var fraction = (double)i / GridLines;
            var y = plot.Top + (fraction * plot.Height);
            dc.DrawLine(i == GridLines / 2 ? ZeroPen : GridPen, new Point(plot.Left, y), new Point(plot.Right, y));

            var speed = speedRange - (fraction * 2 * speedRange);
            var current = currentRange - (fraction * 2 * currentRange);
            DrawText(dc, speed.ToString("0", CultureInfo.CurrentCulture), new Point(4, y - 8), dpi);
            DrawText(dc, current.ToString("0.#", CultureInfo.CurrentCulture), new Point(plot.Right + 6, y - 8), dpi);
        }

        DrawText(dc, "rpm", new Point(4, plot.Top - 22), dpi);
        DrawText(dc, "A", new Point(plot.Right + 6, plot.Top - 22), dpi);
    }

    private static void DrawLegend(DrawingContext dc, Rect plot, double dpi)
    {
        var x = plot.Left + 40;
        foreach (var (label, pen) in new[] { ("Speed", SpeedPen), ("Setpoint", SetpointPen), ("Current", CurrentPen) })
        {
            dc.DrawLine(pen, new Point(x, plot.Top - 14), new Point(x + 18, plot.Top - 14));
            var text = Text(label, dpi);
            dc.DrawText(text, new Point(x + 22, plot.Top - 22));
            x += 34 + text.Width;
        }
    }

    private static void DrawSeries(DrawingContext dc, IReadOnlyList<TrendSample> samples, Pen pen, Func<TrendSample, Point> map)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(map(samples[0]), isFilled: false, isClosed: false);
            for (var i = 1; i < samples.Count; i++)
            {
                ctx.LineTo(map(samples[i]), isStroked: true, isSmoothJoin: true);
            }
        }

        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    private static void DrawText(DrawingContext dc, string text, Point origin, double dpi) =>
        dc.DrawText(Text(text, dpi), origin);

    private static FormattedText Text(string text, double dpi) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Font, 11, AxisText, dpi);

    /// <summary>Symmetric axis limit rounded up to a 1/2/5 step, so labels stay readable.</summary>
    private static double NiceRange(double maxAbs, double minimum)
    {
        var target = Math.Max(maxAbs * 1.1, minimum);
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(target)));
        foreach (var step in new[] { 1, 2, 5, 10 })
        {
            if (step * magnitude >= target)
            {
                return step * magnitude;
            }
        }

        return 10 * magnitude;
    }

    private static T Frozen<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
