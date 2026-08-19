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
    private const double Size = 160;
    private const double Radius = Size / 2 - 14;
    private const double Thickness = 20;

    public RingChart()
    {
        InitializeComponent();
    }

    public void SetData(string valueText, string labelText, params (double value, Color color)[] segments)
    {
        ValueText.Text = valueText;
        LabelText.Text = labelText;
        Draw.Children.Clear();
        double cx = Size / 2, cy = Size / 2;
        double start = -90;
        double acc = 0;
        double total = 0;
        foreach (var (v, _) in segments) total += v;
        foreach (var (v, col) in segments)
        {
            if (v <= 0) continue;
            double f = total > 0 ? v / total : 0;
            if (f <= 0) continue;
            acc += f;
            AddArc(cx, cy, start, start + f * 360, col);
            start = start + f * 360;
        }
        if (acc < 1)
            AddArc(cx, cy, start, start + (1 - acc) * 360, Color.FromArgb(255, 112, 112, 128));
    }

    private void AddArc(double cx, double cy, double a0, double a1, Color col)
    {
        double sweep = a1 - a0;
        if (sweep <= 0) return;
        if (sweep >= 359.999)
        {
            AddArc(cx, cy, a0, a0 + 180, col);
            AddArc(cx, cy, a0 + 180, a1, col);
            return;
        }
        var fig = new PathFigure { StartPoint = Pt(cx, cy, a0), IsFilled = false };
        fig.Segments.Add(new ArcSegment
        {
            Point = Pt(cx, cy, a1),
            Size = new Size(Radius, Radius),
            IsLargeArc = sweep > 180,
            SweepDirection = SweepDirection.Clockwise,
        });
        var geom = new PathGeometry();
        geom.Figures.Add(fig);
        Draw.Children.Add(new Path
        {
            Data = geom,
            Stroke = new SolidColorBrush(col),
            StrokeThickness = Thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        });
    }

    private static Point Pt(double cx, double cy, double deg)
    {
        double r = deg * Math.PI / 180;
        return new Point(cx + Radius * Math.Cos(r), cy + Radius * Math.Sin(r));
    }
}