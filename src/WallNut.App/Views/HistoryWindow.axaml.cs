using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using ScottPlot.Avalonia;
using WallNut.Charts;
using WallNut.Shared;
using WallNut.Shared.History;

namespace WallNut.App.Views;

/// <summary>History and charts for one UPS over a chosen time range.</summary>
public partial class HistoryWindow : Window
{
    private static readonly (string Label, TimeSpan Span)[] Ranges =
    [
        ("1h", TimeSpan.FromHours(1)), ("6h", TimeSpan.FromHours(6)), ("24h", TimeSpan.FromHours(24)),
        ("7d", TimeSpan.FromDays(7)), ("30d", TimeSpan.FromDays(30)), ("1y", TimeSpan.FromDays(365)),
    ];

    private readonly WallNutServiceClient _client;
    private readonly DispatcherTimer _autoRefresh = new() { Interval = TimeSpan.FromSeconds(60) };
    private readonly List<(AvaPlot Plot, ScottPlot.Plottables.VerticalLine Cursor)> _plots = [];
    private TimeSpan _range = TimeSpan.FromHours(24);
    private HistoryResult? _result;
    private OutageSummary? _outages;
    private CancellationTokenSource? _loading;
    private bool _suppressSelection;

    public HistoryWindow() : this(((App)Application.Current!).Connection.Client) { }

    public HistoryWindow(WallNutServiceClient client)
    {
        _client = client;
        InitializeComponent();

        var buttons = this.FindControl<StackPanel>("RangeButtons")!;
        foreach (var (label, span) in Ranges)
        {
            var b = new Button { Content = label, Classes = { "range" }, Tag = span };
            b.Click += (_, _) => { _range = span; UpdateRangeButtons(); _ = LoadAsync(); };
            buttons.Children.Add(b);
        }
        UpdateRangeButtons();

        UpsBox.SelectionChanged += (_, _) => { if (!_suppressSelection) _ = LoadAsync(); };
        RefreshButton.Click += (_, _) => _ = LoadAsync();
        ExportButton.Click += Export;
        _autoRefresh.Tick += (_, _) => _ = LoadAsync(quiet: true);
        ActualThemeVariantChanged += (_, _) => Redraw();
        Opened += async (_, _) => { await LoadUpsListAsync(); _autoRefresh.Start(); };
        Closed += (_, _) => { _autoRefresh.Stop(); _loading?.Cancel(); };
    }

    private sealed record UpsChoice(string Id, string Name)
    {
        public override string ToString() => Name;
    }

    private string? SelectedUps => (UpsBox.SelectedItem as UpsChoice)?.Id;

    /// <summary>Pre-selects a UPS (e.g. from the tray menu).</summary>
    public string? InitialUps { get; set; }

    /// <summary>Starts on a range: "1h", "6h", "24h", "7d", "30d" or "1y".</summary>
    public string? InitialRange
    {
        set
        {
            var match = Ranges.FirstOrDefault(r => r.Label.Equals(value, StringComparison.OrdinalIgnoreCase));
            if (match.Label is not null) { _range = match.Span; UpdateRangeButtons(); }
        }
    }

    private async Task LoadUpsListAsync()
    {
        try
        {
            var withHistory = await _client.GetHistoryEndpointsAsync();
            var choices = withHistory.Select(e => new UpsChoice(e.EndpointId, e.DisplayName)).ToList();
            _suppressSelection = true;
            UpsBox.ItemsSource = choices;
            UpsBox.SelectedItem = choices.FirstOrDefault(c => c.Id == InitialUps) ?? choices.FirstOrDefault();
            _suppressSelection = false;
            if (choices.Count == 0) Status("No history yet. WALLNut records readings every few seconds once a UPS is connected.");
            else await LoadAsync();
        }
        catch (Exception e) { Status(Explain(e)); }
    }

    private async Task LoadAsync(bool quiet = false)
    {
        if (SelectedUps is not { } ups) return;
        _loading?.Cancel();
        var cts = _loading = new CancellationTokenSource();
        var to = DateTimeOffset.UtcNow;
        var from = to - _range;
        if (!quiet) Status("Loading...");

        try
        {
            // Roughly one point per 1.5 pixels of chart width.
            var points = (int)Math.Clamp(Bounds.Width / 1.5, 300, 1500);
            var history = _client.GetHistoryAsync(ups, from, to, points: points, ct: cts.Token);
            var outages = _client.GetOutagesAsync(ups, from, to, cts.Token);
            var events = _client.GetHistoryEventsAsync(ups, from, to, 300, cts.Token);
            await Task.WhenAll(history, outages, events);
            if (cts.IsCancellationRequested) return;

            _result = history.Result;
            _outages = outages.Result;
            Redraw();
            ShowSummary(HistorySummary.From(_result, _outages));
            ShowOutages(_outages);
            ShowEvents(events.Result);
            Status($"{Describe(_range)} to {DateTime.Now:t}. One point per {Describe(TimeSpan.FromSeconds(_result.ResolutionSeconds))}" +
                   $"{(_result.ResolutionSeconds > 15 ? "; shaded bands show the lowest and highest values in each" : "")}. Updates every minute.");
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Status(Explain(e)); }
    }

    // ---- Charts --------------------------------------------------------------------------

    private void Redraw()
    {
        var charts = this.FindControl<StackPanel>("Charts")!;
        charts.Children.Clear();
        _plots.Clear();
        if (_result is null) return;

        var kinds = HistoryCharts.ChartsWithData(_result);
        this.FindControl<TextBlock>("EmptyText")!.IsVisible = kinds.Count == 0;
        var theme = new ChartTheme(ActualThemeVariant == ThemeVariant.Dark);
        var outages = _outages?.Outages ?? [];

        foreach (var kind in kinds)
        {
            var view = new AvaPlot { Height = kind == MetricChart.Battery ? 300 : 240 };
            HistoryCharts.Build(view.Plot, _result, kind, outages, theme);
            var cursor = view.Plot.Add.VerticalLine(HistoryCharts.X(_result.From));
            cursor.Color = ScottPlot.Color.FromHex(theme.Dark ? "#FFFFFF" : "#000000").WithAlpha(0.35);
            cursor.LineWidth = 1;
            cursor.IsVisible = false;
            var chartKind = kind;
            view.PointerMoved += (_, e) => Hover(view, chartKind, e.GetPosition(view));
            view.PointerExited += (_, _) => HideCursor();
            _plots.Add((view, cursor));
            charts.Children.Add(new Border { Classes = { "card" }, Padding = new Thickness(6), Child = view });
            view.Refresh();
        }

        // Pan or zoom one chart and the others follow (time axis only).
        foreach (var (a, _) in _plots)
            foreach (var (b, _) in _plots)
                if (a != b) a.Plot.Axes.Link(b, x: true, y: false);
    }

    private void Hover(AvaPlot view, MetricChart chart, Point position)
    {
        if (_result is null) return;
        var scale = view.DisplayScale; // screen pixels per Avalonia unit, as the control uses for its own mouse handling
        var at = view.Plot.GetCoordinates(new ScottPlot.Pixel((float)position.X * scale, (float)position.Y * scale));
        var local = DateTime.FromOADate(at.X);
        foreach (var (p, cursor) in _plots)
        {
            cursor.X = at.X;
            cursor.IsVisible = true;
            p.Refresh();
        }

        var when = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
        // The readings on the chart being pointed at.
        var all = HistoryCharts.ValuesAt(_result, when).ToList();
        var values = all
            .Where(v => v.Series.Chart == chart.ToString())
            .Select(v => $"{v.Series.Label} {Format(v.Value)} {HistoryCharts.DisplayUnit(v.Series)}".TrimEnd())
            .ToList();
        var onBattery = all.FirstOrDefault(v => v.Series.Metric == MetricCatalog.OnBattery).Value >= 0.5;
        Readout.Text = $"{local:ddd MMM d, h:mm tt}  ·  {MetricCatalog.ChartTitle(chart)}{(onBattery ? "  ·  ON BATTERY" : "")}\n" +
                       (values.Count > 0 ? string.Join("   ·   ", values) : "No readings at this moment.");
    }

    private void HideCursor()
    {
        foreach (var (p, cursor) in _plots) { cursor.IsVisible = false; p.Refresh(); }
    }

    private static string Format(double v) =>
        Math.Abs(v) >= 100 ? v.ToString("0", CultureInfo.CurrentCulture)
        : Math.Abs(v) >= 10 ? v.ToString("0.#", CultureInfo.CurrentCulture)
        : v.ToString("0.##", CultureInfo.CurrentCulture);

    // ---- Summary and lists ---------------------------------------------------------------

    private void ShowSummary(HistorySummary s)
    {
        OutagesValue.Text = s.Outages.ToString(CultureInfo.CurrentCulture);
        OnBatteryValue.Text = s.Outages == 0 ? "None" : HistorySummary.Duration(s.TimeOnBattery);
        LongestValue.Text = s.Outages == 0 ? "-" : HistorySummary.Duration(s.LongestOutage);
        LowestValue.Text = s.LowestCharge is { } c ? $"{c:0}%" : "-";
        LoadValue.Text = s.AverageLoad is { } a ? $"{a:0}% ({s.PeakLoad:0}%)" : "-";
    }

    private void ShowOutages(OutageSummary outages)
    {
        NoOutages.IsVisible = outages.Count == 0;
        OutageList.ItemsSource = outages.Outages.Select(o => Row(
            o.Start.ToLocalTime().ToString("ddd MMM d, h:mm tt", CultureInfo.CurrentCulture),
            HistorySummary.Duration(TimeSpan.FromSeconds(o.DurationSeconds)) + (o.End is null ? " so far" : ""),
            o.LowestCharge is { } low ? $"battery {o.StartCharge:0}% → {low:0}%" : "",
            o.ShutdownTriggered ? "shutdown triggered" : o.EndReason,
            o.End is null || o.ShutdownTriggered)).ToList();
    }

    private void ShowEvents(IReadOnlyList<ServiceEvent> events)
    {
        EventList.ItemsSource = events.Select(e => Row(
            e.Time.ToLocalTime().ToString("ddd MMM d, h:mm:ss tt", CultureInfo.CurrentCulture),
            NotificationCatalog.Find(e.Kind).Title,
            e.Message, "", e.IsProblem)).ToList();
    }

    private static Control Row(string when, string what, string detail, string outcome, bool highlight)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("190,170,*,Auto"), Margin = new Thickness(0, 3) };
        void Add(int col, string text, bool strong = false)
        {
            var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
            if (strong) t.FontWeight = FontWeight.SemiBold;
            Grid.SetColumn(t, col);
            grid.Children.Add(t);
        }
        Add(0, when);
        Add(1, what, highlight);
        Add(2, detail);
        Add(3, outcome);
        if (highlight) grid.Children.Insert(0, new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(28, 229, 72, 77)),
            CornerRadius = new CornerRadius(4),
            [Grid.ColumnSpanProperty] = 4,
            Margin = new Thickness(-6, -2),
        });
        return grid;
    }

    // ---- Export --------------------------------------------------------------------------

    private async void Export(object? sender, RoutedEventArgs e)
    {
        if (SelectedUps is not { } ups) return;
        var to = DateTimeOffset.UtcNow;
        var from = to - _range;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export history as CSV",
            SuggestedFileName = $"wallnut-{ups}-{DateTime.Now:yyyyMMdd-HHmm}.csv",
            DefaultExtension = "csv",
            FileTypeChoices = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }],
        });
        if (file is null) return;
        try
        {
            await using var stream = await file.OpenWriteAsync();
            await _client.ExportCsvAsync(ups, from, to, stream);
            Status($"Exported to {file.Name}.");
        }
        catch (Exception ex) { Status($"Export failed: {ex.Message}"); }
    }

    // ---- Helpers -------------------------------------------------------------------------

    private void UpdateRangeButtons()
    {
        foreach (var b in this.FindControl<StackPanel>("RangeButtons")!.Children.OfType<Button>())
        {
            var selected = b.Tag is TimeSpan s && s == _range;
            b.Classes.Set("accent", selected);
        }
    }

    private void Status(string text) => StatusText.Text = text;

    private static string Describe(TimeSpan t) =>
        t.TotalDays >= 365 ? "Last year" : t.TotalDays >= 1 ? (t.TotalDays == 1 ? "1 day" : $"{t.TotalDays:0} days")
        : t.TotalHours >= 1 ? (t.TotalHours == 1 ? "1 hour" : $"{t.TotalHours:0} hours")
        : t.TotalMinutes >= 1 ? $"{t.TotalMinutes:0} min" : $"{t.TotalSeconds:0} s";

    private static string Explain(Exception e) => e switch
    {
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.ServiceUnavailable } => "History isn't available: the service couldn't open its history database.",
        HttpRequestException or IOException or TimeoutException => "Can't reach the WALLNut service. Is it running?",
        _ => e.Message,
    };
}
