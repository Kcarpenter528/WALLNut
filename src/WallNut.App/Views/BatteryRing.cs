using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;

namespace WallNut.App.Views;

/// <summary>A ring gauge: a track, an arc filled to the value, and the number in the middle.</summary>
public sealed class BatteryRing : Grid
{
    private readonly Arc _arc;
    private readonly TextBlock _value;
    private readonly TextBlock _caption;

    public BatteryRing(double size, double thickness = 10, string caption = "battery")
    {
        Width = Height = size;
        var track = new Ellipse
        {
            Stroke = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)),
            StrokeThickness = thickness,
            Margin = new Thickness(thickness / 2),
        };
        _arc = new Arc
        {
            StartAngle = -90,
            SweepAngle = 0,
            StrokeThickness = thickness,
            StrokeLineCap = PenLineCap.Round,
            Margin = new Thickness(thickness / 2),
        };
        _value = new TextBlock { FontSize = size * 0.22, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
        _caption = new TextBlock { Text = caption, FontSize = Math.Max(10, size * 0.09), Opacity = 0.65, HorizontalAlignment = HorizontalAlignment.Center };
        Children.Add(track);
        Children.Add(_arc);
        Children.Add(new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _value, _caption },
        });
    }

    /// <summary>0-100, or null for unknown.</summary>
    public void Set(double? percent, string? caption = null)
    {
        var p = Math.Clamp(percent ?? 0, 0, 100);
        _arc.SweepAngle = p >= 99.95 ? 359.99 : p * 3.6;
        _arc.Stroke = UpsLook.ChargeColor(percent);
        _value.Text = UpsLook.Percent(percent);
        if (caption is not null) _caption.Text = caption;
    }
}
