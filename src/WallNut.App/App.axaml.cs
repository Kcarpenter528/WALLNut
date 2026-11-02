using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using WallNut.App.Notifications;
using WallNut.App.Services;
using WallNut.App.Views;
using WallNut.Shared;
using WallNut.Shared.Notifications;

namespace WallNut.App;

/// <summary>
/// The tray app: an icon in the notification area whose color shows UPS health, desktop
/// notifications for UPS events (filtered by this user's preferences), and a settings window.
/// </summary>
public partial class App : Application
{
    private static readonly TimeSpan ServiceMissingAfter = TimeSpan.FromSeconds(30);

    private readonly ServiceConnection _connection = new();
    private NotificationPreferences _prefs = NotificationPreferences.Load();
    private NotificationFilter _filter = null!;
    private INotifier _notifier = null!;
    private TrayIcon _tray = null!;
    private NativeMenuItem _statusItem = null!;
    private SettingsWindow? _settings;
    private readonly Dictionary<TrayState, WindowIcon> _icons = [];
    private DateTimeOffset? _unreachableSince;
    private bool _toldServiceMissing;
    private bool _everConnected;

    public NotificationPreferences Preferences => _prefs;
    public ServiceConnection Connection => _connection;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // No main window: the app lives in the tray until "Quit".
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.Exit += (_, _) => { _connection.Dispose(); _filter.Dispose(); };

            foreach (var state in Enum.GetValues<TrayState>())
                _icons[state] = new WindowIcon(AssetLoader.Open(new Uri($"avares://WALLNut/Assets/tray-{state.ToString().ToLowerInvariant()}.png")));

            _notifier = Notifier.ForThisPlatform();
            _filter = new NotificationFilter(() => _prefs);
            _filter.Show += ShowNotification;

            BuildTray(desktop);

            _connection.EventReceived += e => _filter.Handle(e);
            _connection.InfoChanged += info => Dispatcher.UIThread.Post(() => OnInfo(info));
            _connection.Start();

            if (args(desktop).Contains("--settings")) OpenSettings();
        }
        base.OnFrameworkInitializationCompleted();

        static string[] args(IClassicDesktopStyleApplicationLifetime d) => d.Args ?? [];
    }

    private void BuildTray(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _statusItem = new NativeMenuItem("Connecting to WALLNut service...") { IsEnabled = false };

        var settings = new NativeMenuItem("Notification settings...");
        settings.Click += (_, _) => OpenSettings();

        var test = new NativeMenuItem("Send test notification");
        test.Click += async (_, _) => await SendTestAsync();

        var quit = new NativeMenuItem("Quit WALLNut");
        quit.Click += (_, _) => desktop.Shutdown();

        _tray = new TrayIcon
        {
            Icon = _icons[TrayState.Offline],
            ToolTipText = "WALLNut",
            Menu = new NativeMenu { Items = { _statusItem, new NativeMenuItemSeparator(), settings, test, new NativeMenuItemSeparator(), quit } },
        };
        _tray.Clicked += (_, _) => OpenSettings();
        TrayIcon.SetIcons(this, [_tray]);
    }

    private void OnInfo(ServiceInfo? info)
    {
        var (state, text) = TrayStatus.Describe(info);
        _tray.Icon = _icons[state];
        _tray.ToolTipText = "WALLNut\n" + text;
        _statusItem.Header = text.Split('\n')[0];
        _settings?.UpdateService(info);
        WatchServiceAvailability(info is not null);
    }

    /// <summary>Tell the user (once) if the service stays unreachable: without it there are no UPS alerts.</summary>
    private void WatchServiceAvailability(bool reachable)
    {
        var now = DateTimeOffset.UtcNow;
        if (reachable)
        {
            if (_toldServiceMissing)
                _filter.Handle(Local("CommunicationRestored", "Connected to the WALLNut service again. UPS alerts are back on."));
            _unreachableSince = null;
            _toldServiceMissing = false;
            _everConnected = true;
            return;
        }

        _unreachableSince ??= now;
        if (!_toldServiceMissing && now - _unreachableSince >= ServiceMissingAfter)
        {
            _toldServiceMissing = true;
            _filter.Handle(Local("CommunicationLost", _everConnected
                ? "Lost contact with the WALLNut service. You won't get UPS alerts until it's running again."
                : "Can't reach the WALLNut service, so you won't get UPS alerts. Is it installed and running?"));
        }
    }

    private static ServiceEvent Local(string kind, string message) =>
        new(0, "wallnut-service", "WALLNut service", kind, DateTimeOffset.UtcNow, null, message, kind == "CommunicationLost");

    private void ShowNotification(ServiceEvent e)
    {
        var kind = NotificationCatalog.Find(e.Kind);
        var title = e.EndpointId == "this-machine" || e.EndpointId == "wallnut-service"
            ? kind.Title
            : $"{kind.Title}: {e.DisplayName}";
        try { _notifier.Show(title, e.Message, kind.Urgency, e.EndpointId); }
        catch (Exception ex) { System.Diagnostics.Trace.TraceError($"WALLNut: notification failed: {ex}"); }
    }

    public async Task<string> SendTestAsync()
    {
        try
        {
            await _connection.Client.SendTestAsync();
            return "Test sent through the WALLNut service. It should appear in a moment.";
        }
        catch
        {
            // The service isn't there; at least prove desktop notifications work.
            ShowNotification(Local("Test", "Desktop notifications work, but the WALLNut service isn't running, so UPS alerts won't arrive."));
            return "The WALLNut service isn't running, so the tray app sent the test itself.";
        }
    }

    public void SavePreferences(NotificationPreferences prefs)
    {
        prefs.Save();
        _prefs = prefs; // swapped in one step; the filter always reads the current object
    }

    private void OpenSettings()
    {
        if (_settings is { } open) { open.Activate(); return; }
        _settings = new SettingsWindow(this);
        _settings.Closed += (_, _) => _settings = null;
        _settings.UpdateService(_connection.Info);
        _settings.Show();
        _settings.Activate();
    }
}
