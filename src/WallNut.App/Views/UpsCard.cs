using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using WallNut.Shared;
using WallNut.Shared.History;

namespace WallNut.App.Views;

/// <summary>One UPS on the wall: status, battery ring, key readings and the last hour at a glance.</summary>
public sealed class UpsCard : Border
{
    private readonly TextBlock _name = new() { FontSize = 16, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _where = new() { FontSize = 12, Opacity = 0.6, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Border _pill = new() { CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 2), VerticalAlignment = VerticalAlignment.Top };
    private readonly TextBlock _pillText = new() { FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White };
    private readonly Border _role = new() { CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 1), BorderThickness = new Thickness(1), Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly BatteryRing _ring = new(112);
    private readonly TextBlock _runtime = Value();
    private readonly TextBlock _load = Value();
    private readonly ProgressBar _loadBar = new() { Minimum = 0, Maximum = 100, Height = 6, MinHeight = 6, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 2, 0, 0) };
    private readonly TextBlock _input = Value();
    private readonly Polyline _loadLine = new() { StrokeThickness = 1.6, Stroke = new SolidColorBrush(Color.Parse("#F5A524")) };
    private readonly Polyline _chargeLine = new() { StrokeThickness = 1.6, Stroke = new SolidColorBrush(Color.Parse("#30A46C")) };
    private readonly TextBlock _updated = new() { FontSize = 11, Opacity = 0.55 };
    // Why it isn't connected: wrapped inside the card, never across it (full text on hover).
    private readonly TextBlock _problem = new()
    {
        FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis,
        Margin = new Thickness(0, 0, 0, 8), IsVisible = false,
    };
    private const double SparkWidth = 300, SparkHeight = 38;

    public string Id { get; }
    public UpsSummary? Summary { get; private set; }
    public event Action<string>? Open;

    public UpsCard(string id)
    {
        Id = id;
        Classes.Add("card");
        Width = 352;
        Padding = new Thickness(18, 16);
        Margin = new Thickness(0, 0, 14, 14);
        Cursor = new Cursor(StandardCursorType.Hand);
        _pill.Child = _pillText;
        _role.Child = new TextBlock { Text = "Powers this computer", FontSize = 11 };
        _role.BorderBrush = UpsLook.Accent;

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var titles = new StackPanel { Children = { _name, _where, _role } };
        header.Children.Add(titles);
        Grid.SetColumn(_pill, 1);
        header.Children.Add(_pill);

        var stats = new StackPanel
        {
            Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 0, 0),
            Children =
            {
                Labeled("Runtime left", _runtime),
                new StackPanel { Children = { Labeled("Load", _load), _loadBar } },
                Labeled("Input", _input),
            },
        };
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 14, 0, 10) };
        body.Children.Add(_ring);
        Grid.SetColumn(stats, 1);
        body.Children.Add(stats);

        var spark = new Canvas { Width = SparkWidth, Height = SparkHeight, ClipToBounds = true, Children = { _chargeLine, _loadLine } };
        var legend = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 12,
            Children =
            {
                Key("#30A46C", "battery"), Key("#F5A524", "load"),
                new TextBlock { Text = "last hour", FontSize = 11, Opacity = 0.55 },
            },
        };
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 6, 0, 0) };
        footer.Children.Add(legend);
        Grid.SetColumn(_updated, 1);
        footer.Children.Add(_updated);

        Child = new StackPanel { Children = { header, body, _problem, spark, footer } };
        PointerPressed += (_, e) => { if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) Open?.Invoke(Id); };
    }

    public void Update(UpsSummary u)
    {
        Summary = u;
        _name.Text = u.DisplayName;
        _where.Text = $"{u.Ups}@{u.Host}" + (u.Model is { } m ? $"  ·  {m}" : "");
        _role.IsVisible = u.Role == "PowersThisMachine";
        var (label, color) = UpsLook.State(u);
        _pillText.Text = label;
        _pill.Background = color;
        _ring.Set(u.Connected ? u.ChargePercent : null);
        _runtime.Text = u.Connected ? UpsLook.Runtime(u.RuntimeSeconds) : "-";
        _load.Text = u.Connected ? UpsLook.Percent(u.LoadPercent) : "-";
        _loadBar.Value = u.Connected ? Math.Clamp(u.LoadPercent ?? 0, 0, 100) : 0;
        _loadBar.Foreground = u.LoadPercent > 80 ? UpsLook.Critical : u.LoadPercent > 60 ? UpsLook.Warning : UpsLook.Accent;
        _input.Text = !u.Connected ? "-"
            : u.OnBattery ? "No mains power"
            : u.InputVoltage is { } v ? $"{v.ToString("0", CultureInfo.CurrentCulture)} V" : "On mains";
        ShowProblem(!u.Connected ? Capitalize(u.LastError ?? "Not connected.") : u.Stale ? "The NUT server has lost contact with this UPS." : null);
        _updated.Text = u.Connected ? "updated " + UpsLook.Ago(u.LastUpdate) : "last seen " + UpsLook.Ago(u.LastUpdate);
        Opacity = u.Connected ? 1 : 0.8;
    }

    /// <summary>
    /// The WALLNut service can't be reached, so this UPS's state is unknown. Never keep showing the
    /// last good state as if it were current.
    /// </summary>
    public void MarkUnknown()
    {
        _pillText.Text = "Unknown";
        _pill.Background = UpsLook.Neutral;
        _ring.Set(null);
        _runtime.Text = "-";
        _load.Text = "-";
        _loadBar.Value = 0;
        _input.Text = "-";
        ShowProblem("The WALLNut service isn't reachable, so this UPS's current state is unknown.");
        _updated.Text = "last seen " + UpsLook.Ago(Summary?.LastUpdate);
        Opacity = 0.7;
    }

    private void ShowProblem(string? text)
    {
        _problem.IsVisible = text is not null;
        _problem.Text = text;
        _problem.Foreground = UpsLook.Warning;
        ToolTip.SetTip(_problem, text);
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>Draws the last hour of battery charge and load (both 0-100%).</summary>
    public void SetSparkline(HistoryResult? history)
    {
        _chargeLine.Points = Points(history?.Series.FirstOrDefault(s => s.Metric == "battery.charge"), history);
        _loadLine.Points = Points(history?.Series.FirstOrDefault(s => s.Metric == "ups.load"), history);
    }

    private static List<Point> Points(HistorySeries? s, HistoryResult? r)
    {
        if (s is null || r is null || s.Points.Count == 0) return [];
        double from = r.From.ToUnixTimeSeconds(), span = Math.Max(1, r.To.ToUnixTimeSeconds() - from);
        return s.Points.Select(p => new Point(
            (p.T - from) / span * SparkWidth,
            SparkHeight - 2 - Math.Clamp(p.Avg, 0, 100) / 100 * (SparkHeight - 4))).ToList();
    }

    // Capped width: a long value trims with "..." instead of pushing past the card's edge.
    private static TextBlock Value() => new()
    {
        FontSize = 15, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis,
        MaxWidth = 130, TextAlignment = TextAlignment.Right,
    };

    private static Control Labeled(string label, TextBlock value) => new Grid
    {
        ColumnDefinitions = new ColumnDefinitions("*,Auto"),
        Children =
        {
            new TextBlock { Text = label, FontSize = 12, Opacity = 0.65, VerticalAlignment = VerticalAlignment.Center },
            Column1(value),
        },
    };

    private static Control Column1(Control c) { Grid.SetColumn(c, 1); return c; }

    private static Control Key(string color, string text) => new StackPanel
    {
        Orientation = Orientation.Horizontal, Spacing = 4,
        Children =
        {
            new Border { Width = 10, Height = 3, Background = new SolidColorBrush(Color.Parse(color)), VerticalAlignment = VerticalAlignment.Center },
            new TextBlock { Text = text, FontSize = 11, Opacity = 0.7 },
        },
    };
}
