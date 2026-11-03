using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using WallNut.Shared;
using WallNut.Shared.Settings;

namespace WallNut.App.Views;

/// <summary>What the Settings tabs need: the service's settings and the live UPS list.</summary>
internal sealed record SettingsData(Shared.Settings.SettingsView View, IReadOnlyList<UpsSummary> Live);

/// <summary>Settings: UPS connections, shutdown rules, notifications and about.</summary>
public sealed class SettingsPage : UserControl, IPage
{
    private readonly App _app;
    private readonly TabControl _tabs = new();
    private readonly ConnectionsView _connections;
    private readonly ShutdownView _shutdown;
    private readonly AboutView _about;
    private readonly NotificationSettingsView _notifications;
    private readonly ActionsView _actions;
    private readonly TextBlock _error = new() { Foreground = UpsLook.Critical, TextWrapping = TextWrapping.Wrap };

    public SettingsPage(App app)
    {
        _app = app;
        _connections = new ConnectionsView(app);
        _shutdown = new ShutdownView(app, this);
        _about = new AboutView(app);
        _notifications = new NotificationSettingsView(app);
        _actions = new ActionsView(app);
        var tabs = new List<TabItem>
        {
            new() { Header = "UPS connections", Content = _connections },
            new() { Header = "Shutdown", Content = _shutdown },
            new() { Header = "Alerts & actions", Content = _actions },
        };
        // Notification choices belong to the logged-in user, not the elevated settings copy.
        if (!app.AdminMode) tabs.Add(new TabItem { Header = "Notifications", Content = _notifications });
        tabs.Add(new TabItem { Header = "About", Content = _about });
        _tabs.ItemsSource = tabs;
        Content = new DockPanel
        {
            Margin = new Thickness(24, 20, 24, 12),
            Children =
            {
                Top(new TextBlock { Text = "Settings", FontSize = 24, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 6) }),
                Top(_error),
                _tabs,
            },
        };
    }

    public string Title => "Settings";

    public void OnShown() => _ = ReloadAsync();
    public void OnHidden() { }

    /// <summary>Opens a tab by header text, e.g. "Notifications".</summary>
    public void SelectTab(string header)
    {
        if (_tabs.ItemsSource is IEnumerable<TabItem> tabs && tabs.FirstOrDefault(t => (string?)t.Header == header) is { } tab)
            _tabs.SelectedItem = tab;
    }

    internal async Task ReloadAsync()
    {
        try
        {
            var view = await _app.Connection.Client.GetSettingsAsync();
            var info = await _app.Connection.Client.GetInfoAsync();
            var data = new SettingsData(view, info.Ups);
            _error.Text = "";
            _notifications.UpdateService(info);
            _connections.Show(this, data);
            _shutdown.Show(data);
            _actions.Show(this, data);
            _about.Show(view);
        }
        catch (Exception e) { _error.Text = "Can't load settings from the WALLNut service: " + e.Message; }
    }

    private static Control Top(Control c) { DockPanel.SetDock(c, Dock.Top); return c; }
}

/// <summary>Shown when settings are read-only, with a way to unlock them.</summary>
public sealed class AdminBanner : Border
{
    private readonly App _app;
    private readonly TextBlock _text = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _unlock = new() { Content = "Unlock with administrator rights" };

    public AdminBanner(App app)
    {
        _app = app;
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(14, 10);
        Margin = new Thickness(0, 0, 0, 12);
        BorderThickness = new Thickness(4, 0, 0, 0);
        BorderBrush = UpsLook.Warning;
        Background = new SolidColorBrush(Color.FromArgb(34, 245, 165, 36));
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(_text);
        Grid.SetColumn(_unlock, 1);
        _unlock.Margin = new Thickness(12, 0, 0, 0);
        grid.Children.Add(_unlock);
        Child = grid;
        _unlock.Click += (_, _) => _text.Text = _app.OpenElevatedSettings();
        IsVisible = false;
    }

    public void Show(Shared.Settings.SettingsView view)
    {
        IsVisible = !view.CanEdit;
        _text.Text = (view.ReadOnlyReason ?? "") + " You can look, but changes need administrator approval.";
        _unlock.IsVisible = view.ConfigFile is not null;
    }
}

/// <summary>When and how this computer shuts down.</summary>
public sealed class ShutdownView : UserControl
{
    private readonly App _app;
    private readonly SettingsPage _page;
    private readonly AdminBanner _admin;
    private readonly ToggleSwitch _testMode = new() { OnContent = "Test mode: only log what would happen", OffContent = "Armed: really shut down" };
    private readonly TextBlock _testWarning = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly ToggleSwitch _enabled = new() { OnContent = "Automatic shutdown on", OffContent = "Automatic shutdown off" };
    private readonly ComboBox _action = new() { ItemsSource = new[] { "Shut down", "Hibernate" }, SelectedIndex = 0, Width = 180 };
    private readonly NumericUpDown _minSupplies = new() { Minimum = 1, Maximum = 8, Value = 1, FormatString = "0", Width = 120 };
    private readonly CheckBox _fsd = new() { Content = "When the NUT server says to (forced shutdown, e.g. a NAS about to power off)" };
    private readonly CheckBox _lowBattery = new() { Content = "When the UPS reports low battery" };
    private readonly (CheckBox On, NumericUpDown Value) _charge = Optional("When on battery and the charge is below", "%", 5, 95, 30);
    private readonly (CheckBox On, NumericUpDown Value) _runtime = Optional("When on battery with less than", "minutes left", 1, 120, 5);
    private readonly (CheckBox On, NumericUpDown Value) _onFor = Optional("After being on battery for", "minutes", 1, 240, 10);
    private readonly NumericUpDown _lost = new() { Minimum = 5, Maximum = 600, Value = 15, FormatString = "0", Width = 120 };
    private readonly NumericUpDown _delay = new() { Minimum = 0, Maximum = 120, Value = 5, FormatString = "0", Width = 120 };
    private readonly Button _save = new() { Content = "Save", Classes = { "accent" } };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _form;

    public ShutdownView(App app, SettingsPage page)
    {
        _app = app;
        _page = page;
        _admin = new AdminBanner(app);
        _testMode.IsCheckedChanged += (_, _) => Warn();
        _save.Click += async (_, _) => await SaveAsync();

        _form = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new Border { Classes = { "card" }, Padding = new Thickness(16, 12), Child = new StackPanel { Spacing = 6, Children = { _testMode, _testWarning } } },
                _enabled,
                ConnectionsView.Field("Action", _action),
                ConnectionsView.Field("Needs", new StackPanel { Spacing = 3, Children =
                {
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _minSupplies,
                        new TextBlock { Text = "healthy power supplies to keep running", VerticalAlignment = VerticalAlignment.Center } } },
                    new TextBlock { FontSize = 12, Opacity = 0.6, TextWrapping = TextWrapping.Wrap,
                        Text = "Each UPS that powers this computer counts as one supply. Use 1 unless a server has two power supplies on two UPSes and must keep both." },
                } }),
                new TextBlock { Text = "A UPS that powers this computer counts as failing:", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) },
                _fsd, _lowBattery, Row(_charge), Row(_runtime), Row(_onFor),
                ConnectionsView.Field("If contact is lost", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _lost,
                    new TextBlock { Text = "seconds while on battery, assume the worst", VerticalAlignment = VerticalAlignment.Center } } }),
                ConnectionsView.Field("Before acting", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _delay,
                    new TextBlock { Text = "seconds' warning, so alerts go out first", VerticalAlignment = VerticalAlignment.Center } } }),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _save } },
                _message,
            },
        };
        Content = new ScrollViewer { Content = new StackPanel { Margin = new Thickness(0, 12, 12, 12), Children = { _admin, _form } } };
    }

    internal void Show(SettingsData data)
    {
        var s = data.View.Shutdown;
        _admin.Show(data.View);
        _form.IsEnabled = data.View.CanEdit;
        _testMode.IsChecked = s.DryRun;
        _enabled.IsChecked = s.Enabled;
        _action.SelectedIndex = s.Action == "Hibernate" ? 1 : 0;
        _minSupplies.Value = s.MinimumSupplies;
        _fsd.IsChecked = s.RespectForcedShutdown;
        _lowBattery.IsChecked = s.OnLowBattery;
        Set(_charge, s.BatteryChargeBelowPercent);
        Set(_runtime, s.RuntimeBelowSeconds / 60);
        Set(_onFor, s.OnBatteryForSeconds / 60);
        _lost.Value = (decimal)s.LostWhileOnBatterySeconds;
        _delay.Value = (decimal)s.FinalDelaySeconds;
        Warn();
    }

    private void Warn()
    {
        _testWarning.Text = _testMode.IsChecked == true
            ? "WALLNut will only log \"would shut down\". Keep this on until you've rehearsed an outage, and keep your current UPS software in charge until then."
            : "WALLNut WILL shut this computer down when power runs low. Turn off any other UPS software's shutdown (e.g. WinNUT) so they don't both act.";
        if (_testMode.IsChecked == true) _testWarning.ClearValue(TextBlock.ForegroundProperty); else _testWarning.Foreground = UpsLook.Warning;
    }

    private async Task SaveAsync()
    {
        var settings = new ShutdownSettings
        {
            DryRun = _testMode.IsChecked == true,
            Enabled = _enabled.IsChecked == true,
            Action = _action.SelectedIndex == 1 ? "Hibernate" : "Shutdown",
            MinimumSupplies = (int)(_minSupplies.Value ?? 1),
            RespectForcedShutdown = _fsd.IsChecked == true,
            OnLowBattery = _lowBattery.IsChecked == true,
            BatteryChargeBelowPercent = Get(_charge),
            RuntimeBelowSeconds = Get(_runtime) * 60,
            OnBatteryForSeconds = Get(_onFor) * 60,
            LostWhileOnBatterySeconds = (double)(_lost.Value ?? 15),
            FinalDelaySeconds = (double)(_delay.Value ?? 5),
        };
        try
        {
            await _app.Connection.Client.SaveShutdownAsync(settings);
            _message.Text = settings.DryRun ? "Saved. Still in test mode." : "Saved. Automatic shutdown is ARMED.";
            _message.Foreground = UpsLook.Good;
            await _page.ReloadAsync();
        }
        catch (SettingsException e) { _message.Text = string.Join("\n", e.Problems); _message.Foreground = UpsLook.Critical; }
        catch (Exception e) { _message.Text = "Couldn't save: " + e.Message; _message.Foreground = UpsLook.Critical; }
    }

    private static (CheckBox, NumericUpDown) Optional(string text, string unit, int min, int max, int value)
    {
        var box = new CheckBox { Content = text };
        var num = new NumericUpDown { Minimum = min, Maximum = max, Value = value, FormatString = "0", Width = 120, IsEnabled = false };
        box.IsCheckedChanged += (_, _) => num.IsEnabled = box.IsChecked == true;
        num.Tag = unit;
        return (box, num);
    }

    private static Control Row((CheckBox On, NumericUpDown Value) o) => new StackPanel
    {
        Orientation = Orientation.Horizontal, Spacing = 8,
        Children = { o.On, o.Value, new TextBlock { Text = (string)o.Value.Tag!, VerticalAlignment = VerticalAlignment.Center } },
    };

    private static void Set((CheckBox On, NumericUpDown Value) o, double? v)
    {
        o.On.IsChecked = v is not null;
        if (v is { } x) o.Value.Value = (decimal)Math.Round(x);
    }

    private static double? Get((CheckBox On, NumericUpDown Value) o) => o.On.IsChecked == true ? (double)(o.Value.Value ?? 0) : null;
}

/// <summary>Versions and where things are.</summary>
public sealed class AboutView : UserControl
{
    private readonly App _app;
    private readonly StackPanel _lines = new() { Spacing = 6 };

    public AboutView(App app)
    {
        _app = app;
        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(0, 12, 12, 12), Spacing = 14,
                Children =
                {
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Children =
                    {
                        new Image { Source = new Avalonia.Media.Imaging.Bitmap(Avalonia.Platform.AssetLoader.Open(new Uri("avares://WALLNut/Assets/wallnut.png"))), Width = 56, Height = 56 },
                        new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children =
                        {
                            new TextBlock { Text = "WALLNut", FontSize = 22, FontWeight = FontWeight.SemiBold },
                            new TextBlock { Text = "A wall of data for all of your NUTs.", Opacity = 0.7 },
                        } },
                    } },
                    new Border { Classes = { "card" }, Padding = new Thickness(16, 12), Child = _lines },
                    new TextBlock { Opacity = 0.65, FontSize = 12, TextWrapping = TextWrapping.Wrap,
                        Text = "WALLNut talks to Network UPS Tools (NUT) servers such as a Synology or QNAP NAS. It is not affiliated with the NUT project." },
                },
            },
        };
    }

    public void Show(Shared.Settings.SettingsView view)
    {
        var info = _app.Connection.Info;
        _lines.Children.Clear();
        Line("App version", typeof(App).Assembly.GetName().Version?.ToString() ?? "-");
        Line("Service version", info?.Version ?? "not reachable");
        Line("Computer", info?.Machine ?? Environment.MachineName);
        Line("Settings file", view.ConfigFile ?? "none");
        Line("Local API", _app.Connection.Client.Endpoint);
        Line("Your notification settings", Shared.Notifications.NotificationPreferences.DefaultPath);
    }

    private void Line(string label, string value)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("200,*") };
        g.Children.Add(new TextBlock { Text = label, Opacity = 0.65 });
        var v = new SelectableTextBlock { Text = value, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(v, 1);
        g.Children.Add(v);
        _lines.Children.Add(g);
    }
}
