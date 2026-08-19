using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace ODCL;

public sealed partial class RingChart : UserControl
{
    private const double GapDeg = 6;

    public static readonly DependencyProperty ChartSizeProperty = DependencyProperty.Register(
        nameof(ChartSize), typeof(double), typeof(RingChart), new PropertyMetadata(160.0));

    public double ChartSize
    {
        get => (double)GetValue(ChartSizeProperty);
        set => SetValue(ChartSizeProperty, value);
    }

    public RingChart()
    {
        InitializeComponent();
        ApplySize();
        SizeChanged += (_, _) => ApplySize();
    }

    private void ApplySize()
    {
        RootGrid.Width = RootGrid.Height = ChartSize;
        Draw.Width = Draw.Height = ChartSize;
        ValueText.FontSize = ChartSize * 0.11;
        LabelText.FontSize = ChartSize * 0.07;
    }

    public void SetData(string valueText, string labelText, params (double value, Color color)[] segments)
    {
        ValueText.Text = valueText;
        LabelText.Text = labelText;
        Draw.Children.Clear();
        double cx = ChartSize / 2, cy = ChartSize / 2;
        double R = ChartSize / 2 - ChartSize * 0.08;
        double T = ChartSize * 0.15;
        double usable = 360 - GapDeg * 2;
        double total = 0;
        foreach (var (v, _) in segments) total += v;
        double start = -90 + GapDeg;
        double acc = 0;
        foreach (var (v, col) in segments)
        {
            if (v <= 0) continue;
            double f = total > 0 ? v / total : 0;
            if (f <= 0) continue;
            acc += f;
            AddArc(cx, cy, R, T, start, start + f * usable, col);
            start += f * usable;
        }
        if (acc < 1)
            AddArc(cx, cy, R, T, start, -90 + 360 - GapDeg, Color.FromArgb(255, 118, 118, 132));
    }

    private void AddArc(double cx, double cy, double R, double T, double a0, double a1, Color col)
    {
        double sweep = a1 - a0;
        if (sweep <= 0) return;
        if (sweep >= 359.999)
        {
            AddArc(cx, cy, R, T, a0, a0 + 180, col);
            AddArc(cx, cy, R, T, a0 + 180, a1, col);
            return;
        }
        var fig = new PathFigure { StartPoint = Pt(cx, cy, R, a0), IsFilled = false };
        fig.Segments.Add(new ArcSegment
        {
            Point = Pt(cx, cy, R, a1),
            Size = new Size(R, R),
            IsLargeArc = sweep > 180,
            SweepDirection = SweepDirection.Clockwise,
        });
        var geom = new PathGeometry();
        geom.Figures.Add(fig);
        Draw.Children.Add(new Path
        {
            Data = geom,
            Stroke = new SolidColorBrush(col),
            StrokeThickness = T,
            StrokeStartLineCap = PenLineCap.Flat,
            StrokeEndLineCap = PenLineCap.Flat,
        });
    }

    private static Point Pt(double cx, double cy, double R, double deg)
    {
        double r = deg * Math.PI / 180;
        return new Point(cx + R * Math.Cos(r), cy + R * Math.Sin(r));
    }
}