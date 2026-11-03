using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using WallNut.Shared;

namespace WallNut.App.Views;

/// <summary>The WALLNut desktop app: a sidebar and the current page.</summary>
public sealed class MainWindow : Window
{
    private readonly App _app;
    private readonly ContentControl _content = new();
    private readonly StackPanel _nav = new() { Spacing = 4 };
    private readonly Border _dot = new() { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _serviceText = new() { FontSize = 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap };
    private readonly OverviewView _overview;
    private readonly UpsDetailView _detail;
    private readonly HistoryView _history;
    private readonly EventsView _events;
    private readonly SettingsPage _settings;
    private readonly Dictionary<IPage, Button> _buttons = [];
    private IPage? _current;

    public MainWindow(App app, bool adminMode = false)
    {
        _app = app;
        Title = adminMode ? "WALLNut settings (administrator)" : "WALLNut";
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://WALLNut/Assets/wallnut.ico")));
        Width = 1280; Height = 860; MinWidth = 900; MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _overview = new OverviewView(app);
        _detail = new UpsDetailView(app);
        _history = new HistoryView(app.Connection.Client);
        _events = new EventsView(app);
        _settings = new SettingsPage(app);
        _overview.OpenUps += ShowUps;
        _detail.Back += ShowOverview;
        _detail.OpenHistory += ShowHistory;

        if (!adminMode)
        {
            AddNav("Overview", Icons.Overview, _overview);
            AddNav("History", Icons.History, _history);
            AddNav("Events", Icons.Events, _events);
        }
        AddNav("Settings", Icons.Settings, _settings);

        var brand = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(6, 4, 0, 22),
            Children =
            {
                new Image { Source = new Avalonia.Media.Imaging.Bitmap(AssetLoader.Open(new Uri("avares://WALLNut/Assets/wallnut.png"))), Width = 30, Height = 30 },
                new TextBlock { Text = "WALLNut", FontSize = 19, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center },
            },
        };
        var status = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(8, 0, 4, 4),
            Children = { _dot, _serviceText },
        };
        var sidebar = new Border
        {
            Width = 220, Padding = new Thickness(12, 18, 12, 14),
            Child = new DockPanel { Children = { Top(brand), Bottom(status), _nav } },
        };
        // Follows light/dark theme changes live.
        sidebar.Bind(Border.BackgroundProperty, sidebar.GetResourceObservable("SidebarBrush"));
        DockPanel.SetDock(sidebar, Dock.Left);
        Content = new DockPanel { Children = { sidebar, _content } };

        app.Connection.InfoChanged += OnInfo;
        Closed += (_, _) =>
        {
            app.Connection.InfoChanged -= OnInfo;
            _current?.OnHidden();
        };
        OnInfo(app.Connection.Info);

        if (adminMode) Navigate(_settings); else Navigate(_overview);
    }

    public void ShowOverview() => Navigate(_overview);

    public void ShowUps(string id)
    {
        _detail.Show(id);
        Navigate(_detail, highlight: _overview);
    }

    public void ShowHistory(string? upsId = null)
    {
        if (upsId is not null) _history.SelectUps(upsId);
        Navigate(_history);
    }

    public void ShowEvents() => Navigate(_events);

    public void ShowSettings(string? tab = null)
    {
        if (tab is not null) _settings.SelectTab(tab);
        Navigate(_settings);
    }

    public HistoryView History => _history;

    private void Navigate(IPage page, IPage? highlight = null)
    {
        if (_current == page) { page.OnShown(); return; }
        _current?.OnHidden();
        _current = page;
        _content.Content = page;
        foreach (var (p, b) in _buttons) b.Classes.Set("selected", p == (highlight ?? page));
        page.OnShown();
    }

    private void AddNav(string text, Geometry icon, IPage page)
    {
        var b = new Button
        {
            Classes = { "nav" },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 12,
                Children =
                {
                    new PathIcon { Data = icon, Width = 18, Height = 18 },
                    new TextBlock { Text = text, FontSize = 14, VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        b.Click += (_, _) => Navigate(page);
        _buttons[page] = b;
        _nav.Children.Add(b);
    }

    private void OnInfo(ServiceInfo? info) => Dispatcher.UIThread.Post(() =>
    {
        _dot.Background = info is null ? UpsLook.Critical : UpsLook.Good;
        _serviceText.Text = info is null ? "Service not reachable" : $"Service running on {info.Machine}";
    });

    private static Control Top(Control c) { DockPanel.SetDock(c, Dock.Top); return c; }
    private static Control Bottom(Control c) { DockPanel.SetDock(c, Dock.Bottom); return c; }
}
