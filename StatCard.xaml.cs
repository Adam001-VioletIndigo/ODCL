using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace ODCL;

public sealed partial class StatCard : UserControl
{
    public static readonly DependencyProperty VerticalProperty = DependencyProperty.Register(
        nameof(Vertical), typeof(bool), typeof(StatCard), new PropertyMetadata(false, (d, _) => ((StatCard)d).ApplyLayout()));

    /// <summary>true 时强制纵向布局；false 时按卡片自身宽高判断。</summary>
    public bool Vertical
    {
        get => (bool)GetValue(VerticalProperty);
        set => SetValue(VerticalProperty, value);
    }

    public static readonly DependencyProperty RingSizeProperty = DependencyProperty.Register(
        nameof(RingSize), typeof(double), typeof(StatCard), new PropertyMetadata(80.0, (d, _) => ((StatCard)d).ApplyRingSize()));

    /// <summary>环图直径（纵向模式参考值）；横向模式取 0.66 倍。</summary>
    public double RingSize
    {
        get => (double)GetValue(RingSizeProperty);
        set => SetValue(RingSizeProperty, value);
    }

    private void ApplyRingSize()
    {
        HRing.ChartSize = RingSize * 0.66;
        VRing.ChartSize = RingSize;
    }

    public StatCard()
    {
        InitializeComponent();
        SizeChanged += (_, _) => ApplyLayout();
        Loaded += (_, _) => ApplyLayout();
        ApplyLayout();
    }

    private void ApplyLayout()
    {
        if (!IsLoaded) return;
        bool vert = Vertical || ActualHeight > ActualWidth;
        HorzBox.Visibility = vert ? Visibility.Collapsed : Visibility.Visible;
        VertBox.Visibility = vert ? Visibility.Visible : Visibility.Collapsed;
        (vert ? VertBox : HorzBox).VerticalAlignment = VerticalAlignment.Center;
        ApplyRingSize();
    }

    public void SetData(string valueText, string labelText, params (double value, Color color)[] segments)
    {
        HRing.SetData(valueText, labelText, segments);
        VRing.SetData(valueText, labelText, segments);
    }

    public void SetPercent(string pct)
    {
        HRing.SetData(pct, "");
        VRing.SetData(pct, "");
    }

    public string Value
    {
        get => HValue.Text;
        set { HValue.Text = VValue.Text = value; }
    }

    public string Caption
    {
        get => HCaption.Text;
        set { HCaption.Text = VCaption.Text = value; }
    }

    public string Extra
    {
        get => HExtra.Text;
        set
        {
            HExtra.Text = VExtra.Text = value;
            HExtra.Visibility = VExtra.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    public void SetLegend(IEnumerable<(string label, long val, Color color)> items)
    {
        var arr = items.ToArray();
        HLegend.Children.Clear();
        VLegend.Children.Clear();
        HLegend.Children.Add(BuildLegendGrid(arr, HorizontalAlignment.Stretch));
        VLegend.Children.Add(BuildLegendGrid(arr, HorizontalAlignment.Center));
    }

    /// <summary>2 列 × (n/2) 行的紧凑图例，避免横向排不下被裁切。</summary>
    private static Grid BuildLegendGrid((string label, long val, Color color)[] arr, HorizontalAlignment align)
    {
        int rows = (arr.Length + 1) / 2;
        var g = new Grid { HorizontalAlignment = align };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int r = 0; r < rows; r++) g.RowDefinitions.Add(new RowDefinition());
        for (int i = 0; i < arr.Length; i++)
        {
            var cell = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 1, i % 2 == 0 ? 12 : 0, 1) };
            cell.Children.Add(Line(arr[i], 11));
            Grid.SetRow(cell, i / 2);
            Grid.SetColumn(cell, i % 2);
            g.Children.Add(cell);
        }
        return g;
    }

    private static TextBlock Line((string label, long val, Color color) item, double size)
    {
        var tb = new TextBlock { FontSize = size, TextWrapping = TextWrapping.Wrap };
        tb.Inlines.Add(new Run { Text = "● ", Foreground = new SolidColorBrush(item.color) });
        tb.Inlines.Add(new Run { Text = item.label });
        tb.Inlines.Add(new Run { Text = "  " + Fmt(item.val), Foreground = new SolidColorBrush(Color.FromArgb(180, 110, 110, 120)) });
        return tb;
    }

    private static string Fmt(long b)
        => b >= 1073741824 ? $"{b / 1073741824.0:F1} GB"
         : b >= 1048576 ? $"{b / 1048576.0:F1} MB"
         : b >= 1024 ? $"{b / 1024.0:F1} KB"
         : $"{b} B";
}