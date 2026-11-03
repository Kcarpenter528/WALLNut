using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using WallNut.Shared;

namespace WallNut.App.Views;

/// <summary>The wall: one live card per UPS, with the overall state on top.</summary>
public sealed class OverviewView : UserControl, IPage
{
    private readonly App _app;
    private readonly WrapPanel _wall = new();
    private readonly Dictionary<string, UpsCard> _cards = new(StringComparer.OrdinalIgnoreCase);
    private readonly Border _banner = new() { CornerRadius = new CornerRadius(8), Padding = new Thickness(16, 12), Margin = new Thickness(0, 0, 14, 16) };
    private readonly TextBlock _bannerTitle = new() { FontSize = 15, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _bannerText = new() { FontSize = 13, Opacity = 0.85, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _empty = new()
    {
        Text = "No UPS is set up yet. Add one under Settings > UPS connections.",
        Opacity = 0.7, Margin = new Thickness(0, 30), IsVisible = false,
    };
    private readonly DispatcherTimer _sparkTimer = new() { Interval = TimeSpan.FromSeconds(60) };
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(5) };

    public event Action<string>? OpenUps;

    public OverviewView(App app)
    {
        _app = app;
        _banner.Child = new StackPanel { Spacing = 2, Children = { _bannerTitle, _bannerText } };
        Content = new ScrollViewer
        {
            Padding = new Thickness(24, 20, 10, 20),
            Content = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = "Overview", FontSize = 24, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 14) },
                    _banner, _empty, _wall,
                },
            },
        };
        _sparkTimer.Tick += (_, _) => _ = LoadSparklinesAsync();
        _clock.Tick += (_, _) => { foreach (var c in _cards.Values) if (c.Summary is { } s) c.Update(s); };
    }

    public string Title => "Overview";

    private void OnInfo(ServiceInfo? info) => Dispatcher.UIThread.Post(() => Show(info));

    public void OnShown()
    {
        _app.Connection.InfoChanged -= OnInfo;
        _app.Connection.InfoChanged += OnInfo;
        Show(_app.Connection.Info);
        _ = LoadSparklinesAsync();
        _sparkTimer.Start();
        _clock.Start();
    }

    public void OnHidden()
    {
        _app.Connection.InfoChanged -= OnInfo;
        _sparkTimer.Stop();
        _clock.Stop();
    }

    private void Show(ServiceInfo? info)
    {
        ShowBanner(info);
        if (info is null) return;

        foreach (var u in info.Ups)
        {
            if (!_cards.TryGetValue(u.Id, out var card))
            {
                card = new UpsCard(u.Id);
                card.Open += id => OpenUps?.Invoke(id);
                _cards[u.Id] = card;
                _wall.Children.Add(card);
                _ = LoadSparklineAsync(card);
            }
            card.Update(u);
        }
        foreach (var gone in _cards.Keys.Where(id => info.Ups.All(u => !u.Id.Equals(id, StringComparison.OrdinalIgnoreCase))).ToList())
        {
            _wall.Children.Remove(_cards[gone]);
            _cards.Remove(gone);
        }
        _empty.IsVisible = info.Ups.Count == 0;
    }

    private void ShowBanner(ServiceInfo? info)
    {
        if (info is null)
        {
            Banner(UpsLook.Neutral, "Can't reach the WALLNut service",
                "The service watches your UPSes and shuts this computer down safely. Without it there are no alerts. Is it installed and running?");
            return;
        }
        var onBattery = info.Ups.Count(u => u.OnBattery);
        var down = info.Ups.Count(u => !u.Connected);
        var powering = info.Ups.Where(u => u.Role == "PowersThisMachine").ToList();
        var shutdown = !info.ShutdownEnabled ? "Automatic shutdown is off."
            : powering.Count == 0 ? "No UPS is set as powering this computer, so it won't be shut down automatically."
            : info.ShutdownTestMode ? "Automatic shutdown is in TEST MODE: it only logs what it would do."
            : $"Automatic {info.ShutdownAction.ToLowerInvariant()} is armed.";

        if (info.ShutdownInitiated && !info.ShutdownTestMode)
            Banner(UpsLook.Critical, "This computer is shutting down", "Too few power supplies are healthy. Save your work.");
        else if (onBattery > 0)
            Banner(UpsLook.Warning, onBattery == 1 ? "Power failure: a UPS is on battery" : $"Power failure: {onBattery} UPSes are on battery", shutdown);
        else if (down > 0)
            Banner(UpsLook.Warning, down == 1 ? "A UPS isn't connected" : $"{down} UPSes aren't connected", shutdown);
        else
            Banner(UpsLook.Good, info.Ups.Count == 1 ? "All good: on line power" : $"All good: {info.Ups.Count} UPSes on line power", shutdown);
    }

    private void Banner(IBrush color, string title, string text)
    {
        var c = ((ISolidColorBrush)color).Color;
        _banner.Background = new SolidColorBrush(Color.FromArgb(38, c.R, c.G, c.B));
        _banner.BorderBrush = color;
        _banner.BorderThickness = new Thickness(4, 0, 0, 0);
        _bannerTitle.Text = title;
        _bannerText.Text = text;
    }

    private async Task LoadSparklinesAsync()
    {
        foreach (var card in _cards.Values.ToList()) await LoadSparklineAsync(card);
    }

    private async Task LoadSparklineAsync(UpsCard card)
    {
        try
        {
            var to = DateTimeOffset.UtcNow;
            var h = await _app.Connection.Client.GetHistoryAsync(card.Id, to.AddHours(-1), to, ["battery.charge", "ups.load"], points: 120);
            card.SetSparkline(h);
        }
        catch { card.SetSparkline(null); } // no history yet, or the service is older
    }
}
