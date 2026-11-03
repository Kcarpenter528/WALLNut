using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using WallNut.Shared;
using WallNut.Shared.History;
using WallNut.Shared.Settings;

namespace WallNut.App.Views;

/// <summary>Everything about one UPS: gauges, identity and every variable it reports, refreshed every 2 s.</summary>
public sealed class UpsDetailView : UserControl, IPage
{
    private readonly App _app;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly TextBlock _name = new() { FontSize = 24, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _pill = new() { CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 3), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _pillText = new() { FontWeight = FontWeight.SemiBold, Foreground = Brushes.White };
    private readonly TextBlock _identity = new() { Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 16) };
    private readonly BatteryRing _battery = new(150, 12);
    private readonly BatteryRing _load = new(150, 12, "load");
    private readonly WrapPanel _stats = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _filter = new() { PlaceholderText = "Filter readings (e.g. battery, voltage, serial)", Width = 340, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly StackPanel _table = new();
    private readonly TextBlock _status = new() { FontSize = 12, Opacity = 0.6 };
    private UpsDetail? _detail;

    public string? UpsId { get; private set; }
    public event Action? Back;
    public event Action<string>? OpenHistory;

    public UpsDetailView(App app)
    {
        _app = app;
        _pill.Child = _pillText;
        var back = new Button { Content = "← Overview", Margin = new Thickness(0, 0, 0, 12) };
        back.Click += (_, _) => Back?.Invoke();
        var history = new Button { Content = "View history", VerticalAlignment = VerticalAlignment.Center };
        history.Click += (_, _) => { if (UpsId is { } id) OpenHistory?.Invoke(id); };

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto") };
        header.Children.Add(_name);
        Grid.SetColumn(_pill, 1); _pill.Margin = new Thickness(14, 0, 0, 0); header.Children.Add(_pill);
        Grid.SetColumn(history, 3); header.Children.Add(history);

        var gauges = new Border
        {
            Classes = { "card" }, Padding = new Thickness(20, 16),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"),
                Children = { _battery, At(1, _load, new Thickness(24, 0, 0, 0)), At(2, _stats, new Thickness(28, 0, 0, 0)) },
            },
        };

        _filter.TextChanged += (_, _) => RenderTable();
        Content = new ScrollViewer
        {
            Padding = new Thickness(24, 20, 24, 20),
            Content = new StackPanel
            {
                Children =
                {
                    back, header, _identity, gauges,
                    new TextBlock { Text = "Everything this UPS reports", FontSize = 16, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 22, 0, 8) },
                    _filter, new Border { Classes = { "card" }, Padding = new Thickness(16, 8), Margin = new Thickness(0, 10, 0, 0), Child = _table },
                    _status,
                },
            },
        };
        _timer.Tick += (_, _) => _ = RefreshAsync();
    }

    public string Title => _detail?.Summary.DisplayName ?? "UPS";

    public void Show(string id)
    {
        if (!string.Equals(UpsId, id, StringComparison.OrdinalIgnoreCase)) { _detail = null; _filter.Text = ""; _table.Children.Clear(); }
        UpsId = id;
        _ = RefreshAsync();
    }

    public void OnShown() { _timer.Start(); _ = RefreshAsync(); }
    public void OnHidden() => _timer.Stop();

    private async Task RefreshAsync()
    {
        if (UpsId is not { } id) return;
        try
        {
            var d = await _app.Connection.Client.GetUpsDetailAsync(id);
            if (!string.Equals(d.Summary.Id, UpsId, StringComparison.OrdinalIgnoreCase)) return;
            var firstLoad = _detail is null;
            _detail = d;
            Render(d, firstLoad);
        }
        catch (HttpRequestException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _status.Text = "This UPS is no longer configured.";
        }
        catch (Exception e) { _status.Text = "Can't reach the WALLNut service: " + e.Message; }
    }

    private void Render(UpsDetail d, bool rebuildTable)
    {
        var u = d.Summary;
        var v = d.Variables;
        _name.Text = u.DisplayName;
        var (label, color) = UpsLook.State(u);
        _pillText.Text = label;
        _pill.Background = color;

        string? V(string name) => v.TryGetValue(name, out var x) && x.Length > 0 ? x : null;
        _identity.Text = string.Join("   ·   ", new[]
        {
            Join(V("device.mfr") ?? V("ups.mfr"), V("device.model") ?? V("ups.model")),
            (V("device.serial") ?? V("ups.serial")) is { } sn ? $"serial {sn}" : null,
            V("ups.firmware") is { } fw ? $"firmware {fw}" : null,
            $"{u.Ups}@{u.Host}:{u.Port}",
            u.Role == "PowersThisMachine" ? "powers this computer" : "monitored",
            d.ConnectedSince is { } since ? $"connected {UpsLook.Ago(since)}" : null,
        }.Where(s => !string.IsNullOrEmpty(s)));

        _battery.Set(u.Connected ? u.ChargePercent : null);
        _load.Set(u.Connected ? u.LoadPercent : null, "load");

        _stats.Children.Clear();
        Stat("Runtime left", UpsLook.Runtime(u.RuntimeSeconds));
        Stat("Input", Unit(V("input.voltage") ?? V("input.L1-N.voltage"), "V") ?? (u.OnBattery ? "no mains" : "-"));
        Stat("Output", Unit(V("output.voltage") ?? V("output.L1-N.voltage"), "V") ?? "-");
        Stat("Frequency", Unit(V("input.frequency"), "Hz") ?? "-");
        Stat("Battery voltage", Unit(V("battery.voltage"), "V") ?? "-");
        Stat("Temperature", Unit(V("ups.temperature") ?? V("battery.temperature"), "°C") ?? "-");
        Stat("Real power", Unit(V("ups.realpower"), "W") ?? "-");
        Stat("Last self-test", V("ups.test.result") ?? "-");

        _status.Text = $"{v.Count} readings · updated {UpsLook.Ago(u.LastUpdate)}" + (u.LastError is { } err && !u.Connected ? $" · {err}" : "");
        if (rebuildTable) RenderTable(); else UpdateTableValues();
    }

    // ---- All readings ----------------------------------------------------------------------

    private readonly Dictionary<string, TextBlock> _valueCells = new(StringComparer.Ordinal);

    private void RenderTable()
    {
        _table.Children.Clear();
        _valueCells.Clear();
        if (_detail is null) return;
        var filter = _filter.Text?.Trim() ?? "";

        var rows = _detail.Variables
            .Where(kv => filter.Length == 0
                         || kv.Key.Contains(filter, StringComparison.OrdinalIgnoreCase)
                         || kv.Value.Contains(filter, StringComparison.OrdinalIgnoreCase)
                         || (MetricCatalog.Describe(kv.Key)?.Label.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false))
            .OrderBy(kv => GroupOrder(kv.Key)).ThenBy(kv => kv.Key, StringComparer.Ordinal);

        string? group = null;
        foreach (var (name, value) in rows)
        {
            var g = name.Split('.')[0];
            if (g != group)
            {
                group = g;
                _table.Children.Add(new TextBlock
                {
                    Text = GroupTitle(g), FontWeight = FontWeight.SemiBold, Opacity = 0.8,
                    Margin = new Thickness(0, _table.Children.Count == 0 ? 4 : 14, 0, 4),
                });
            }
            var cell = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.Medium };
            _valueCells[name] = cell;
            var label = MetricCatalog.Describe(name)?.Label;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("280,*"), Margin = new Thickness(0, 2) };
            row.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8,
                Children =
                {
                    new TextBlock { Text = name, FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, monospace"), FontSize = 12.5 },
                    new TextBlock { Text = label ?? "", FontSize = 12, Opacity = 0.55, VerticalAlignment = VerticalAlignment.Center },
                },
            });
            Grid.SetColumn(cell, 1);
            row.Children.Add(cell);
            _table.Children.Add(row);
        }
        if (_table.Children.Count == 0)
            _table.Children.Add(new TextBlock { Text = filter.Length > 0 ? "Nothing matches." : "No readings yet.", Opacity = 0.6 });
    }

    private void UpdateTableValues()
    {
        if (_detail is null) return;
        // New or vanished variables need a rebuild; otherwise just update the numbers in place (keeps scroll position).
        var shown = _valueCells.Keys.ToHashSet();
        var filter = _filter.Text?.Trim() ?? "";
        if (filter.Length == 0 && !shown.SetEquals(_detail.Variables.Keys)) { RenderTable(); return; }
        foreach (var (name, cell) in _valueCells)
            if (_detail.Variables.TryGetValue(name, out var value)) cell.Text = value;
    }

    private static readonly string[] Groups = ["ups", "battery", "input", "output", "outlet", "ambient", "device", "driver", "server"];
    private static int GroupOrder(string name) { var i = Array.IndexOf(Groups, name.Split('.')[0]); return i < 0 ? 99 : i; }

    private static string GroupTitle(string g) => g switch
    {
        "ups" => "UPS", "battery" => "Battery", "input" => "Input (mains)", "output" => "Output (to your equipment)",
        "outlet" => "Outlets", "ambient" => "Environment sensors", "device" => "Device", "driver" => "NUT driver",
        "server" => "NUT server", _ => char.ToUpperInvariant(g[0]) + g[1..],
    };

    // ---- Helpers -----------------------------------------------------------------------------

    private void Stat(string label, string value) => _stats.Children.Add(new StackPanel
    {
        Width = 160, Margin = new Thickness(0, 6, 12, 6),
        Children =
        {
            new TextBlock { Text = label, FontSize = 12, Opacity = 0.65 },
            new TextBlock
            {
                Text = value, FontSize = value.Length > 12 ? 14 : 18, FontWeight = FontWeight.SemiBold,
                TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis,
            },
        },
    });

    private static string? Unit(string? value, string unit) =>
        value is null ? null
        : double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? $"{d.ToString(Math.Abs(d) >= 100 ? "0" : "0.#", CultureInfo.CurrentCulture)} {unit}"
            : value;

    private static string? Join(string? a, string? b) => a is null ? b : b is null ? a : b.StartsWith(a, StringComparison.OrdinalIgnoreCase) ? b : $"{a} {b}";

    private static Control At(int column, Control c, Thickness margin) { Grid.SetColumn(c, column); c.Margin = margin; return c; }
}
