using System.ComponentModel;
using System.Diagnostics;
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
/// The WALLNut app. Normally it lives in the tray: an icon whose color shows UPS health, desktop
/// notifications (filtered by this user's preferences), and the main window on demand. Started with
/// --admin-settings (by "Unlock", with administrator rights) it shows only Settings and exits when closed.
/// </summary>
public partial class App : Application
{
    private static readonly TimeSpan ServiceMissingAfter = TimeSpan.FromSeconds(30);

    private readonly ServiceConnection _connection = new();
    private NotificationPreferences _prefs = NotificationPreferences.Load();
    private NotificationFilter? _filter;
    private INotifier _notifier = null!;
    private TrayIcon? _tray;
    private NativeMenuItem? _statusItem;
    private MainWindow? _main;
    private readonly Dictionary<TrayState, WindowIcon> _icons = [];
    private DateTimeOffset? _unreachableSince;
    private bool _toldServiceMissing;
    private bool _everConnected;

    public NotificationPreferences Preferences => _prefs;
    public ServiceConnection Connection => _connection;

    /// <summary>True in the elevated settings-only instance.</summary>
    public bool AdminMode { get; private set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var args = desktop.Args ?? [];
            AdminMode = args.Contains("--admin-settings");
            desktop.Exit += (_, _) => { _connection.Dispose(); _filter?.Dispose(); };
            _connection.Start();

            if (AdminMode)
            {
                // Settings only; closing the window ends this elevated copy.
                desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
                desktop.MainWindow = _main = new MainWindow(this, adminMode: true);
            }
            else
            {
                // Lives in the tray until "Quit"; closing the window just hides the app.
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                foreach (var state in Enum.GetValues<TrayState>())
                    _icons[state] = new WindowIcon(AssetLoader.Open(new Uri($"avares://WALLNut/Assets/tray-{state.ToString().ToLowerInvariant()}.png")));

                _notifier = Notifier.ForThisPlatform();
                _filter = new NotificationFilter(() => _prefs);
                _filter.Show += ShowNotification;
                BuildTray(desktop);

                _connection.EventReceived += e => _filter.Handle(e);
                _connection.InfoChanged += info => Dispatcher.UIThread.Post(() => OnInfo(info));

                if (args.Contains("--settings")) OpenMain(w => w.ShowSettings("Notifications"));
                else if (args.Contains("--history"))
                {
                    var range = args.FirstOrDefault(a => a.StartsWith("--range=", StringComparison.Ordinal))?["--range=".Length..];
                    OpenMain(w => { w.History.InitialRange = range; w.ShowHistory(); });
                }
                else if (args.FirstOrDefault(a => a.StartsWith("--ups=", StringComparison.Ordinal)) is { } ups)
                    OpenMain(w => w.ShowUps(ups["--ups=".Length..]));
                else if (args.FirstOrDefault(a => a.StartsWith("--page=", StringComparison.Ordinal)) is { } page)
                    OpenMain(w =>
                    {
                        switch (page["--page=".Length..].ToLowerInvariant())
                        {
                            case "events": w.ShowEvents(); break;
                            case "history": w.ShowHistory(); break;
                            case "settings": w.ShowSettings(); break;
                            case "shutdown": w.ShowSettings("Shutdown"); break;
                            default: w.ShowOverview(); break;
                        }
                    });
                else if (args.Contains("--open")) OpenMain(w => w.ShowOverview());
            }
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void BuildTray(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _statusItem = new NativeMenuItem("Connecting to WALLNut service...") { IsEnabled = false };

        NativeMenuItem Item(string text, Action action)
        {
            var item = new NativeMenuItem(text);
            item.Click += (_, _) => action();
            return item;
        }

        var test = new NativeMenuItem("Send test notification");
        test.Click += async (_, _) => await SendTestAsync();

        _tray = new TrayIcon
        {
            Icon = _icons[TrayState.Offline],
            ToolTipText = "WALLNut",
            Menu = new NativeMenu
            {
                Items =
                {
                    _statusItem, new NativeMenuItemSeparator(),
                    Item("Open WALLNut", () => OpenMain(w => w.ShowOverview())),
                    Item("History and charts", () => OpenMain(w => w.ShowHistory())),
                    Item("Settings", () => OpenMain(w => w.ShowSettings())),
                    test,
                    new NativeMenuItemSeparator(),
                    Item("Quit WALLNut", () => desktop.Shutdown()),
                },
            },
        };
        _tray.Clicked += (_, _) => OpenMain(w => w.ShowOverview());
        TrayIcon.SetIcons(this, [_tray]);
    }

    /// <summary>Shows the main window (creating it if needed) and runs <paramref name="then"/> on it.</summary>
    public void OpenMain(Action<MainWindow>? then = null)
    {
        if (_main is null)
        {
            _main = new MainWindow(this);
            _main.Closed += (_, _) => _main = null;
            _main.Show();
        }
        then?.Invoke(_main);
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    /// <summary>
    /// Starts a copy of WALLNut with administrator rights showing only Settings (Windows asks for
    /// approval). Returns a message for the user.
    /// </summary>
    public string OpenElevatedSettings()
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return "Can't find the WALLNut program to restart it.";
        if (!OperatingSystem.IsWindows())
            return $"Run this in a terminal to change settings as an administrator:  sudo \"{exe}\" --admin-settings";
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" };
            psi.ArgumentList.Add("--admin-settings");
            psi.ArgumentList.Add("--endpoint=" + _connection.Client.Endpoint);
            Process.Start(psi);
            return "A WALLNut settings window with administrator rights is opening. Make your changes there.";
        }
        catch (Win32Exception) { return "Administrator approval was cancelled."; }
    }

    private void OnInfo(ServiceInfo? info)
    {
        var (state, text) = TrayStatus.Describe(info);
        if (_tray is not null)
        {
            _tray.Icon = _icons[state];
            _tray.ToolTipText = "WALLNut\n" + text;
        }
        if (_statusItem is not null) _statusItem.Header = text.Split('\n')[0];
        WatchServiceAvailability(info is not null);
    }

    /// <summary>Tell the user (once) if the service stays unreachable: without it there are no UPS alerts.</summary>
    private void WatchServiceAvailability(bool reachable)
    {
        if (_filter is null) return;
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
        catch (Exception ex) { Trace.TraceError($"WALLNut: notification failed: {ex}"); }
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
            if (_notifier is not null)
                ShowNotification(Local("Test", "Desktop notifications work, but the WALLNut service isn't running, so UPS alerts won't arrive."));
            return "The WALLNut service isn't running, so the tray app sent the test itself.";
        }
    }

    public void SavePreferences(NotificationPreferences prefs)
    {
        prefs.Save();
        _prefs = prefs; // swapped in one step; the filter always reads the current object
    }
}
