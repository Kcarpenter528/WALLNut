using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using WallNut.Shared;

namespace WallNut.App.Views;

/// <summary>Everything that happened: the last 30 days of events, filterable, with new ones arriving live.</summary>
public sealed class EventsView : UserControl, IPage
{
    private readonly App _app;
    private readonly ComboBox _ups = new() { MinWidth = 220 };
    private readonly CheckBox _problems = new() { Content = "Problems only" };
    private readonly TextBox _search = new() { PlaceholderText = "Search", Width = 260 };
    private readonly StackPanel _list = new();
    private readonly TextBlock _status = new() { FontSize = 12, Opacity = 0.6, Margin = new Thickness(0, 8, 0, 0) };
    private List<ServiceEvent> _events = [];
    private bool _loaded;

    public EventsView(App app)
    {
        _app = app;
        _ups.SelectionChanged += (_, _) => Render();
        _problems.IsCheckedChanged += (_, _) => Render();
        _search.TextChanged += (_, _) => Render();
        var refresh = new Button { Content = "Refresh" };
        refresh.Click += (_, _) => _ = LoadAsync();

        Content = new DockPanel
        {
            Margin = new Thickness(24, 20, 24, 12),
            Children =
            {
                Dock(new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = "Events", FontSize = 24, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 12) },
                        new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 0, 0, 12),
                            Children = { _ups, _problems, _search, refresh } },
                    },
                }, Avalonia.Controls.Dock.Top),
                Dock(_status, Avalonia.Controls.Dock.Bottom),
                new Border { Classes = { "card" }, Padding = new Thickness(14, 8),
                    Child = new ScrollViewer { Content = _list } },
            },
        };
    }

    public string Title => "Events";

    private void OnEvent(ServiceEvent e) => Dispatcher.UIThread.Post(() =>
    {
        if (!_loaded) return;
        _events.Insert(0, e);
        Render();
    });

    public void OnShown()
    {
        _app.Connection.EventReceived -= OnEvent;
        _app.Connection.EventReceived += OnEvent;
        _ = LoadAsync(); // catch up on anything that happened while hidden
    }

    public void OnHidden() => _app.Connection.EventReceived -= OnEvent;

    private async Task LoadAsync()
    {
        try
        {
            var to = DateTimeOffset.UtcNow.AddMinutes(1);
            _events = (await _app.Connection.Client.GetHistoryEventsAsync(null, to.AddDays(-30), to, 2000)).ToList();
            _loaded = true;
            var names = _events.Where(e => e.EndpointId != "this-machine")
                .GroupBy(e => e.EndpointId).Select(g => (g.Key, g.First().DisplayName)).OrderBy(x => x.DisplayName).ToList();
            var current = (_ups.SelectedItem as Choice)?.Id;
            var choices = new List<Choice> { new(null, "All UPSes") };
            choices.AddRange(names.Select(n => new Choice(n.Key, n.DisplayName)));
            _ups.ItemsSource = choices;
            _ups.SelectedItem = choices.FirstOrDefault(c => c.Id == current) ?? choices[0];
            Render();
        }
        catch (Exception e) { _status.Text = "Can't load events: " + e.Message; }
    }

    private sealed record Choice(string? Id, string Name) { public override string ToString() => Name; }

    private void Render()
    {
        var ups = (_ups.SelectedItem as Choice)?.Id;
        var search = _search.Text?.Trim() ?? "";
        var shown = _events
            .Where(e => ups is null || e.EndpointId == ups || e.EndpointId == "this-machine")
            .Where(e => _problems.IsChecked != true || e.IsProblem)
            .Where(e => search.Length == 0 || e.Message.Contains(search, StringComparison.OrdinalIgnoreCase)
                        || e.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase)
                        || NotificationCatalog.Find(e.Kind).Title.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Take(500)
            .ToList();

        _list.Children.Clear();
        string? day = null;
        foreach (var e in shown)
        {
            var local = e.Time.ToLocalTime();
            var d = local.ToString("dddd, MMMM d", CultureInfo.CurrentCulture);
            if (d != day)
            {
                day = d;
                _list.Children.Add(new TextBlock { Text = d, FontWeight = FontWeight.SemiBold, Opacity = 0.75, Margin = new Thickness(0, _list.Children.Count == 0 ? 2 : 14, 0, 4) });
            }
            var kind = NotificationCatalog.Find(e.Kind);
            var color = e.IsProblem ? (kind.Urgency == NotificationUrgency.Critical ? UpsLook.Critical : UpsLook.Warning) : UpsLook.Neutral;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("14,105,200,180,*"), Margin = new Thickness(0, 3) };
            row.Children.Add(new Ellipse(color));
            row.Children.Add(Cell(1, local.ToString("T", CultureInfo.CurrentCulture), 0.7));
            row.Children.Add(Cell(2, e.DisplayName, 1));
            row.Children.Add(Cell(3, kind.Title, 1, bold: e.IsProblem));
            row.Children.Add(Cell(4, e.Message, 0.85));
            _list.Children.Add(row);
        }
        if (shown.Count == 0) _list.Children.Add(new TextBlock { Text = _loaded ? "No events match." : "Loading...", Opacity = 0.6 });
        _status.Text = $"Showing {shown.Count} of {_events.Count} events from the last 30 days. New events appear as they happen.";
    }

    private static TextBlock Cell(int column, string text, double opacity, bool bold = false)
    {
        var t = new TextBlock { Text = text, Opacity = opacity, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 12, 0) };
        if (bold) t.FontWeight = FontWeight.SemiBold;
        Grid.SetColumn(t, column);
        return t;
    }

    private static Control Dock(Control c, Avalonia.Controls.Dock d) { DockPanel.SetDock(c, d); return c; }

    private sealed class Ellipse : Border
    {
        public Ellipse(IBrush color)
        {
            Width = Height = 8; CornerRadius = new CornerRadius(4); Background = color;
            VerticalAlignment = VerticalAlignment.Center;
        }
    }
}
