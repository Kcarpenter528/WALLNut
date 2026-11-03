using System.Globalization;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Media.Imaging;
using QRCoder;
using WallNut.Shared;
using WallNut.Shared.Actions;

namespace WallNut.App.Views;

/// <summary>Settings tab: alerts and actions the service runs (phone alerts, email, webhooks and scripts).</summary>
public sealed class ActionsView : UserControl
{
    private readonly App _app;
    private readonly AdminBanner _admin;
    private readonly StackPanel _list = new() { Spacing = 8 };
    private readonly StackPanel _addButtons = new() { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 12, 0, 0) };
    private readonly Border _editor = new() { Classes = { "card" }, Padding = new Thickness(20, 16), Margin = new Thickness(0, 14, 0, 0), IsVisible = false };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    private SettingsPage? _page;
    private SettingsData? _data;

    // Common editor fields.
    private readonly TextBlock _title = new() { FontSize = 16, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
    private readonly TextBox _name = new() { PlaceholderText = "e.g. My phone" };
    private readonly CheckBox _enabled = new() { Content = "Enabled", IsChecked = true };
    private readonly StackPanel _typePanel = new() { Spacing = 10 };
    private readonly WrapPanel _events = new();
    private readonly CheckBox _allUps = new() { Content = "All UPSes", IsChecked = true };
    private readonly WrapPanel _upsBoxes = new();
    private readonly NumericUpDown _delay = new() { Minimum = 0, Maximum = 3600, Value = 0, FormatString = "0", Width = 130 };
    private readonly TextBlock _testResult = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _save = new() { Content = "Save", Classes = { "accent" } };
    private readonly Button _test = new() { Content = "Send test" };

    // ntfy fields.
    private readonly TextBox _ntfyServer = new() { Text = "https://ntfy.sh" };
    private readonly TextBox _ntfyTopic = new() { PlaceholderText = "a long random name nobody can guess" };
    private readonly TextBox _ntfyToken = new() { PasswordChar = '•', PlaceholderText = "optional, for protected topics" };
    private readonly Image _qr = new() { Width = 180, Height = 180 };
    private readonly SelectableTextBlock _subscribeUrl = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };

    // Webhook fields.
    private readonly ComboBox _preset = new() { ItemsSource = WebhookPresets.All.Select(WebhookPresets.Describe).ToList(), SelectedIndex = 0, MinWidth = 260 };
    private readonly TextBlock _presetHelp = new() { FontSize = 12, Opacity = 0.65, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _url = new() { PlaceholderText = "https://..." };
    private readonly ComboBox _method = new() { ItemsSource = new[] { "POST", "PUT", "GET" }, SelectedIndex = 0, Width = 120 };
    private readonly TextBox _contentType = new() { Text = "application/json" };
    private readonly TextBox _body = new() { AcceptsReturn = true, Height = 120, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, monospace") };
    private readonly TextBox _headers = new() { AcceptsReturn = true, Height = 70, PlaceholderText = "Header-Name: value   (one per line)", FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, monospace") };
    private readonly TextBox _secret = new() { PasswordChar = '•', PlaceholderText = "optional: sign requests (X-WallNut-Signature)" };
    private readonly StackPanel _customFields = new() { Spacing = 10 };

    // Email fields.
    private readonly ComboBox _emailPreset = new() { ItemsSource = EmailPresets.All.Select(p => p.Name).ToList(), SelectedIndex = 0, MinWidth = 260 };
    private readonly TextBlock _emailHelp = new() { FontSize = 12, Opacity = 0.65, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _smtpHost = new() { PlaceholderText = "smtp.example.com" };
    private readonly NumericUpDown _smtpPort = new() { Minimum = 1, Maximum = 65535, Value = 587, FormatString = "0", Width = 130 };
    private readonly ComboBox _smtpSecurity = new() { ItemsSource = EmailPresets.SecurityOptions.Select(EmailPresets.DescribeSecurity).ToList(), SelectedIndex = 0, MinWidth = 260 };
    private readonly TextBox _smtpUser = new() { PlaceholderText = "usually your email address (blank = the From address)" };
    private readonly TextBox _smtpPassword = new() { PasswordChar = '•' };
    private readonly TextBox _emailFrom = new() { PlaceholderText = "you@example.com" };
    private readonly TextBox _emailTo = new() { PlaceholderText = "you@example.com, someone@example.com" };
    private bool _fillingPreset;

    // Script fields.
    private readonly TextBox _scriptPath = new() { PlaceholderText = OperatingSystem.IsWindows() ? @"C:\ProgramData\WALLNut\scripts\on-battery.ps1" : "/etc/wallnut/scripts/on-battery.sh" };
    private readonly TextBox _scriptArgs = new() { PlaceholderText = "optional, e.g. {event} \"{ups}\"", FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, monospace") };
    private readonly NumericUpDown _scriptTimeout = new() { Minimum = 1, Maximum = 3600, Value = 60, FormatString = "0", Width = 130 };
    private readonly CheckBox _beforeShutdown = new() { Content = "Run before this computer shuts down, and wait for it to finish" };
    private readonly CheckBox _inTestMode = new() { Content = "Also run it during shutdown tests (test mode)" };

    private ActionConfig? _editing; // null = new
    private string _type = ActionTypes.Ntfy;

    public ActionsView(App app)
    {
        _app = app;
        _admin = new AdminBanner(app);
        foreach (var type in new[] { ActionTypes.Ntfy, ActionTypes.Email, ActionTypes.Webhook, ActionTypes.Script })
        {
            var b = new Button { Content = "+ " + ActionTypes.Describe(type) };
            b.Click += (_, _) => Edit(null, type);
            _addButtons.Children.Add(b);
        }

        _test.Click += async (_, _) => await TestAsync();
        _save.Click += async (_, _) => await SaveAsync();
        var cancel = new Button { Content = "Cancel" };
        cancel.Click += (_, _) => _editor.IsVisible = false;
        _allUps.IsCheckedChanged += (_, _) => _upsBoxes.IsEnabled = _allUps.IsChecked != true;
        _ntfyServer.TextChanged += (_, _) => UpdateQr();
        _ntfyTopic.TextChanged += (_, _) => UpdateQr();
        _preset.SelectionChanged += (_, _) => PresetChanged();
        _emailPreset.SelectionChanged += (_, _) => EmailPresetChanged();

        _customFields.Children.Add(ConnectionsView.Field("Content type", _contentType));
        _customFields.Children.Add(ConnectionsView.Field("Body", _body,
            "Placeholders: {event} {title} {message} {ups} {ups_id} {host} {status} {charge} {runtime} {load} {machine} {time} {problem}. Values are escaped for JSON automatically."));

        _editor.Child = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                _title,
                ConnectionsView.Field("Name", _name),
                _typePanel,
                new TextBlock { Text = "When", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) },
                _events,
                ConnectionsView.Field("For", new StackPanel { Spacing = 4, Children = { _allUps, _upsBoxes } }),
                ConnectionsView.Field("Only if it lasts", new StackPanel { Spacing = 3, Children =
                {
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _delay,
                        new TextBlock { Text = "seconds", VerticalAlignment = VerticalAlignment.Center } } },
                    new TextBlock { FontSize = 12, Opacity = 0.6, TextWrapping = TextWrapping.Wrap,
                        Text = "Power failures, lost contact and overloads must last this long first; if they clear sooner, nothing is sent. Low battery and shutdown warnings are never delayed." },
                } }),
                _enabled,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 6, 0, 0), Children = { _test, _save, cancel } },
                _testResult,
            },
        };

        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(0, 12, 12, 12),
                Children =
                {
                    new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Margin = new Thickness(0, 0, 0, 6),
                        Text = "The WALLNut service sends these itself, so they work even when nobody is logged in, unlike desktop notifications." },
                    new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.6, FontSize = 12, Margin = new Thickness(0, 0, 0, 12),
                        Text = "Tip: alerts need the internet. If your router and modem aren't on a UPS, a power cut takes the internet down too, and alerts wait until it's back." },
                    _admin, _list, _addButtons, _editor, _message,
                },
            },
        };
    }

    internal void Show(SettingsPage page, SettingsData data)
    {
        _page = page;
        _data = data;
        _admin.Show(data.View);
        _addButtons.IsEnabled = data.View.CanEdit;
        _save.IsEnabled = _test.IsEnabled = data.View.CanEdit;
        _list.Children.Clear();
        foreach (var a in data.View.Actions ?? []) _list.Children.Add(Row(a, data.View.CanEdit));
        if ((data.View.Actions ?? []).Count == 0)
            _list.Children.Add(new TextBlock { Text = "No alerts or actions yet. A phone alert is a good first one.", Opacity = 0.7 });
    }

    private Control Row(ActionConfig a, bool canEdit)
    {
        var edit = new Button { Content = "Edit", IsEnabled = canEdit };
        edit.Click += (_, _) => Edit(a, a.Type);
        var remove = new Button { Content = "Remove", IsEnabled = canEdit };
        remove.Click += async (_, _) =>
        {
            if (remove.Tag is null) { remove.Tag = true; remove.Content = "Click again to remove"; return; }
            await RunAsync(() => _app.Connection.Client.DeleteActionAsync(a.Id), $"Removed {a.Name ?? a.Id}.");
        };
        var where = a.Type switch
        {
            ActionTypes.Ntfy => $"{a.Ntfy?.Server.TrimEnd('/')}/{a.Ntfy?.Topic}",
            ActionTypes.Webhook => $"{WebhookPresets.Describe(a.Webhook?.Preset ?? "")} · {Host(a.Webhook?.Url)}",
            ActionTypes.Script => a.Script?.Path ?? "",
            ActionTypes.Email => string.Join(", ", a.Email?.To ?? []),
            _ => "",
        };
        var when = $"{a.Events.Count} kinds of event" + (a.Ups.Count > 0 ? $" · {a.Ups.Count} UPS" : " · all UPSes") +
                   (a.DelaySeconds > 0 ? $" · if it lasts {a.DelaySeconds:0} s" : "");
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        grid.Children.Add(new StackPanel
        {
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children =
                {
                    new TextBlock { Text = a.Name ?? a.Id, FontWeight = FontWeight.SemiBold, FontSize = 15 },
                    new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 1),
                        Background = a.Enabled ? UpsLook.Accent : UpsLook.Neutral,
                        Child = new TextBlock { Text = a.Enabled ? ActionTypes.Describe(a.Type) : "Disabled", FontSize = 11, Foreground = Brushes.White, FontWeight = FontWeight.SemiBold } },
                } },
                new TextBlock { Text = where, Opacity = 0.65, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = when, Opacity = 0.55, FontSize = 12 },
            },
        });
        Grid.SetColumn(edit, 1); edit.Margin = new Thickness(8, 0); grid.Children.Add(edit);
        Grid.SetColumn(remove, 2); grid.Children.Add(remove);
        return new Border { Classes = { "card" }, Padding = new Thickness(16, 12), Child = grid };
    }

    // ---- Editor ------------------------------------------------------------------------------

    private void Edit(ActionConfig? a, string type)
    {
        _editing = a;
        _type = type;
        _title.Text = (a is null ? "New " : "Edit ") + ActionTypes.Describe(type).ToLowerInvariant();
        _name.Text = a?.Name ?? "";
        _enabled.IsChecked = a?.Enabled ?? true;
        _delay.Value = (decimal)(a?.DelaySeconds ?? (type is ActionTypes.Ntfy or ActionTypes.Email ? 30 : 0));
        _testResult.Text = "";

        // Events, grouped like desktop notifications.
        var chosen = new HashSet<string>(a?.Events ?? ActionDefaults.Events);
        _events.Children.Clear();
        foreach (var k in NotificationCatalog.All.Where(k => !ActionDefaults.Never.Contains(k.Kind)))
            _events.Children.Add(new CheckBox { Content = k.Title, Tag = k.Kind, IsChecked = chosen.Contains(k.Kind), Margin = new Thickness(0, 0, 14, 0), MinWidth = 190 });

        // UPSes.
        _upsBoxes.Children.Clear();
        foreach (var e in _data?.View.Endpoints ?? [])
            _upsBoxes.Children.Add(new CheckBox { Content = e.DisplayName ?? e.Id, Tag = e.Id, IsChecked = a?.Ups.Contains(e.Id, StringComparer.OrdinalIgnoreCase) == true, Margin = new Thickness(0, 0, 14, 0) });
        _allUps.IsChecked = a is null || a.Ups.Count == 0;
        _upsBoxes.IsEnabled = _allUps.IsChecked != true;

        _typePanel.Children.Clear();
        if (type == ActionTypes.Ntfy) BuildNtfy(a?.Ntfy);
        else if (type == ActionTypes.Script) BuildScript(a?.Script);
        else if (type == ActionTypes.Email) BuildEmail(a?.Email);
        else BuildWebhook(a?.Webhook);
        _test.Content = type == ActionTypes.Script ? "Run test" : "Send test";

        _editor.IsVisible = true;
        _editor.BringIntoView();
    }

    private void BuildNtfy(NtfyConfig? n)
    {
        _ntfyServer.Text = n?.Server ?? "https://ntfy.sh";
        _ntfyTopic.Text = n?.Topic ?? NewTopic();
        _ntfyToken.Text = "";
        _ntfyToken.PlaceholderText = n?.TokenSet == true ? "Leave blank to keep the saved token" : "optional, for protected topics";
        var generate = new Button { Content = "New random topic" };
        generate.Click += (_, _) => _ntfyTopic.Text = NewTopic();

        _typePanel.Children.Add(ConnectionsView.Field("Server", _ntfyServer, "https://ntfy.sh is free. You can also run your own ntfy server."));
        _typePanel.Children.Add(ConnectionsView.Field("Topic", new StackPanel { Spacing = 6, Children = { _ntfyTopic, generate } },
            "On a public server the topic name is the password: anyone who knows it can read your alerts. Keep the random one."));
        _typePanel.Children.Add(ConnectionsView.Field("Access token", _ntfyToken));
        _typePanel.Children.Add(ConnectionsView.Field("Your phone", new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 16,
            Children =
            {
                new Border { Background = Brushes.White, Padding = new Thickness(6), CornerRadius = new CornerRadius(6), Child = _qr },
                new StackPanel { Spacing = 6, MaxWidth = 380, VerticalAlignment = VerticalAlignment.Center, Children =
                {
                    new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "1. Install the free ntfy app (Android or iPhone)." },
                    new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "2. Tap + to subscribe, and scan this code or type the topic." },
                    new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "3. Press Send test below." },
                    _subscribeUrl,
                } },
            },
        }));
        UpdateQr();
    }

    private void BuildWebhook(WebhookConfig? w)
    {
        _preset.SelectedIndex = Math.Max(0, WebhookPresets.All.ToList().IndexOf(w?.Preset ?? WebhookPresets.Generic));
        _url.Text = w?.Url ?? "";
        _method.SelectedItem = w?.Method ?? "POST";
        _contentType.Text = w?.ContentType ?? "application/json";
        _body.Text = w?.Body ?? "{\n  \"text\": \"{title}: {message}\"\n}";
        _headers.Text = string.Join("\n", (w?.Headers ?? new Dictionary<string, string>()).Select(h => $"{h.Key}: "));
        _secret.Text = "";
        _secret.PlaceholderText = w?.SecretSet == true ? "Leave blank to keep the saved secret" : "optional: sign requests (X-WallNut-Signature)";

        _typePanel.Children.Add(ConnectionsView.Field("Send to", new StackPanel { Spacing = 4, Children = { _preset, _presetHelp } }));
        _typePanel.Children.Add(ConnectionsView.Field("URL", _url));
        _typePanel.Children.Add(ConnectionsView.Field("Method", _method));
        _typePanel.Children.Add(_customFields);
        _typePanel.Children.Add(ConnectionsView.Field("Headers", _headers,
            w?.Headers.Count > 0 ? "Saved header values are hidden. Leave a value empty to keep it." : "For example: Authorization: Bearer <token>"));
        _typePanel.Children.Add(ConnectionsView.Field("Signing secret", _secret));
        PresetChanged();
    }

    private void BuildEmail(EmailConfig? e)
    {
        _fillingPreset = true;
        var preset = e is null ? EmailPresets.All[0] : EmailPresets.ForHost(e.Host);
        _emailPreset.SelectedIndex = EmailPresets.All.ToList().IndexOf(preset);
        _smtpHost.Text = e?.Host ?? preset.Host;
        _smtpPort.Value = e?.Port ?? preset.Port;
        _smtpSecurity.SelectedIndex = Math.Max(0, EmailPresets.SecurityOptions.ToList().IndexOf(e?.Security ?? preset.Security));
        _smtpUser.Text = e?.Username ?? "";
        _smtpPassword.Text = "";
        _smtpPassword.PlaceholderText = e?.PasswordSet == true ? "Leave blank to keep the saved password" : "app password (see the note above)";
        _emailFrom.Text = e?.From ?? "";
        _emailTo.Text = string.Join(", ", e?.To ?? []);
        _emailHelp.Text = preset.Help;
        _fillingPreset = false;

        var server = new StackPanel { Spacing = 10, Children =
        {
            ConnectionsView.Field("Mail server", _smtpHost),
            ConnectionsView.Field("Port", _smtpPort),
            ConnectionsView.Field("Security", _smtpSecurity),
        } };
        _typePanel.Children.Add(ConnectionsView.Field("Provider", new StackPanel { Spacing = 4, Children = { _emailPreset, _emailHelp } }));
        _typePanel.Children.Add(ConnectionsView.Field("From", _emailFrom));
        _typePanel.Children.Add(ConnectionsView.Field("To", _emailTo, "Separate several addresses with commas."));
        _typePanel.Children.Add(ConnectionsView.Field("User name", _smtpUser));
        _typePanel.Children.Add(ConnectionsView.Field("Password", _smtpPassword,
            "Stored in the service's settings file, which only administrators can read. Sign-in with Google or Microsoft accounts is planned."));
        _typePanel.Children.Add(server);
    }

    private void EmailPresetChanged()
    {
        if (_fillingPreset || _emailPreset.SelectedIndex < 0) return;
        var p = EmailPresets.All[_emailPreset.SelectedIndex];
        _emailHelp.Text = p.Help;
        if (p.Id == EmailPresets.Custom) return; // keep whatever was typed
        _smtpHost.Text = p.Host;
        _smtpPort.Value = p.Port;
        _smtpSecurity.SelectedIndex = EmailPresets.SecurityOptions.ToList().IndexOf(p.Security);
    }

    private void BuildScript(ScriptConfig? sc)
    {
        _scriptPath.Text = sc?.Path ?? "";
        _scriptArgs.Text = sc?.Arguments ?? "";
        _scriptTimeout.Value = (decimal)(sc?.TimeoutSeconds ?? 60);
        _beforeShutdown.IsChecked = sc?.RunBeforeShutdown ?? false;
        _inTestMode.IsChecked = sc?.RunInTestMode ?? false;

        var browse = new Button { Content = "Browse..." };
        browse.Click += async (_, _) => await BrowseAsync();
        var pathRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        pathRow.Children.Add(_scriptPath);
        Grid.SetColumn(browse, 1); browse.Margin = new Thickness(8, 0, 0, 0); pathRow.Children.Add(browse);

        var folder = OperatingSystem.IsWindows() ? @"C:\ProgramData\WALLNut\scripts" : OperatingSystem.IsMacOS() ? "/Library/Application Support/WALLNut/scripts" : "/etc/wallnut/scripts";
        var kinds = OperatingSystem.IsWindows() ? ".ps1, .bat, .cmd or .exe" : ".sh, or any executable file";
        _typePanel.Children.Add(ConnectionsView.Field("Script", pathRow,
            $"A {kinds} file. The service runs it with its own rights (SYSTEM or root), so it must be in a folder only administrators can change, such as {folder}."));
        _typePanel.Children.Add(ConnectionsView.Field("Arguments", _scriptArgs,
            "Placeholders: {event} {title} {message} {ups} {ups_id} {host} {status} {charge} {runtime} {load} {machine} {time}. " +
            "The same values are always in environment variables: WALLNUT_EVENT, WALLNUT_UPS, WALLNUT_CHARGE, WALLNUT_TEST_MODE, ..."));
        _typePanel.Children.Add(ConnectionsView.Field("Time limit", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _scriptTimeout,
            new TextBlock { Text = "seconds (it's stopped after this)", VerticalAlignment = VerticalAlignment.Center } } }));
        _typePanel.Children.Add(new StackPanel { Spacing = 4, Children =
        {
            _beforeShutdown,
            new TextBlock { FontSize = 12, Opacity = 0.6, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(28, 0, 0, 0),
                Text = "For stopping VMs, containers or databases cleanly. The shutdown waits for it, up to the time limit, then goes ahead regardless." },
            _inTestMode,
            new TextBlock { FontSize = 12, Opacity = 0.6, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(28, 0, 0, 0),
                Text = "Off by default, so a rehearsal never stops real things. The \"Run test\" button always runs it." },
        } });
    }

    private async Task BrowseAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Choose a script", AllowMultiple = false });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) _scriptPath.Text = path;
    }

    private string Preset => WebhookPresets.All[Math.Max(0, _preset.SelectedIndex)];

    private void PresetChanged()
    {
        _customFields.IsVisible = Preset == WebhookPresets.Custom;
        _presetHelp.Text = Preset switch
        {
            WebhookPresets.Discord => "In Discord: Server Settings > Integrations > Webhooks > New Webhook > Copy Webhook URL.",
            WebhookPresets.Slack => "In Slack: add the \"Incoming Webhooks\" app to a channel and copy its webhook URL.",
            WebhookPresets.Teams => "In Teams: Workflows > \"Post to a channel when a webhook request is received\" and copy its URL.",
            WebhookPresets.HomeAssistant => "In Home Assistant: an automation with a Webhook trigger; the URL looks like http://homeassistant.local:8123/api/webhook/<id>.",
            WebhookPresets.Custom => "Write your own body below.",
            _ => "WALLNut's standard JSON: event, title, message, problem, urgency, time, machine and the UPS's id, name, status, charge, runtime and load.",
        };
    }

    private void UpdateQr()
    {
        var url = $"{(_ntfyServer.Text ?? "").Trim().TrimEnd('/')}/{(_ntfyTopic.Text ?? "").Trim()}";
        _subscribeUrl.Text = url;
        try
        {
            using var gen = new QRCodeGenerator();
            using var data = gen.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
            var png = new PngByteQRCode(data).GetGraphic(8);
            _qr.Source = new Bitmap(new MemoryStream(png));
        }
        catch { _qr.Source = null; }
    }

    /// <summary>A topic nobody will guess: "wallnut-" and 20 random letters/digits.</summary>
    private static string NewTopic()
    {
        const string chars = "abcdefghijkmnopqrstuvwxyz23456789";
        return "wallnut-" + new string(RandomNumberGenerator.GetItems<char>(chars, 20));
    }

    private ActionConfig FromForm()
    {
        // New actions get an id from their name (e.g. "My phone" -> "my-phone").
        var id = _editing?.Id ?? (Slug(_name.Text) is { Length: > 0 } slug ? slug : _type.ToLowerInvariant());
        var events = _events.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag!).ToList();
        var ups = _allUps.IsChecked == true ? [] : _upsBoxes.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag!).ToList();
        return new ActionConfig
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(_name.Text) ? null : _name.Text.Trim(),
            Type = _type,
            Enabled = _enabled.IsChecked == true,
            Events = events,
            Ups = ups,
            DelaySeconds = (double)(_delay.Value ?? 0),
            Ntfy = _type != ActionTypes.Ntfy ? null : new NtfyConfig
            {
                Server = (_ntfyServer.Text ?? "").Trim(), Topic = (_ntfyTopic.Text ?? "").Trim(),
                Token = string.IsNullOrEmpty(_ntfyToken.Text) ? null : _ntfyToken.Text,
            },
            Webhook = _type != ActionTypes.Webhook ? null : new WebhookConfig
            {
                Url = (_url.Text ?? "").Trim(), Method = (string?)_method.SelectedItem ?? "POST", Preset = Preset,
                Body = Preset == WebhookPresets.Custom ? _body.Text : null,
                ContentType = Preset == WebhookPresets.Custom ? (_contentType.Text ?? "application/json") : "application/json",
                Headers = (_headers.Text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(l => l.Split(':', 2)).Where(p => p[0].Length > 0)
                    .ToDictionary(p => p[0].Trim(), p => p.Length > 1 ? p[1].Trim() : "", StringComparer.OrdinalIgnoreCase),
                Secret = string.IsNullOrEmpty(_secret.Text) ? null : _secret.Text,
            },
            Email = _type != ActionTypes.Email ? null : new EmailConfig
            {
                Host = (_smtpHost.Text ?? "").Trim(),
                Port = (int)(_smtpPort.Value ?? 587),
                Security = EmailPresets.SecurityOptions[Math.Max(0, _smtpSecurity.SelectedIndex)],
                Username = string.IsNullOrWhiteSpace(_smtpUser.Text) ? null : _smtpUser.Text.Trim(),
                Password = string.IsNullOrEmpty(_smtpPassword.Text) ? null : _smtpPassword.Text,
                From = (_emailFrom.Text ?? "").Trim(),
                To = (_emailTo.Text ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            },
            Script = _type != ActionTypes.Script ? null : new ScriptConfig
            {
                Path = (_scriptPath.Text ?? "").Trim().Trim('"'),
                Arguments = string.IsNullOrWhiteSpace(_scriptArgs.Text) ? null : _scriptArgs.Text,
                TimeoutSeconds = (double)(_scriptTimeout.Value ?? 60),
                RunBeforeShutdown = _beforeShutdown.IsChecked == true,
                RunInTestMode = _inTestMode.IsChecked == true,
            },
        };
    }

    private async Task TestAsync()
    {
        _testResult.Text = _type == ActionTypes.Script ? "Running..." : "Sending...";
        _testResult.ClearValue(TextBlock.ForegroundProperty);
        try
        {
            var r = await _app.Connection.Client.TestActionAsync(FromForm());
            _testResult.Text = !r.Ok ? Capitalize(r.Message)
                : _type == ActionTypes.Script ? "The script " + r.Message + ". Its output is in the service log."
                : Capitalize(r.Message).TrimEnd('.') + ". Check that it arrived.";
            _testResult.Foreground = r.Ok ? UpsLook.Good : UpsLook.Critical;
        }
        catch (Exception e) { _testResult.Text = "Couldn't ask the service to test it: " + e.Message; _testResult.Foreground = UpsLook.Critical; }
    }

    private async Task SaveAsync()
    {
        var a = FromForm();
        await RunAsync(() => _app.Connection.Client.SaveActionAsync(a), $"Saved \"{a.Name ?? a.Id}\". It's active now.");
        if (_message.Foreground == UpsLook.Good) _editor.IsVisible = false;
    }

    private async Task RunAsync(Func<Task> change, string done)
    {
        try
        {
            await change();
            _message.Text = done;
            _message.Foreground = UpsLook.Good;
            if (_page is not null) await _page.ReloadAsync();
        }
        catch (SettingsException e) { _message.Text = string.Join("\n", e.Problems); _message.Foreground = UpsLook.Critical; }
        catch (Exception e) { _message.Text = "Couldn't save: " + e.Message; _message.Foreground = UpsLook.Critical; }
    }

    private static string Capitalize(string s) => s.Length > 0 ? char.ToUpperInvariant(s[0]) + s[1..] : s;

    private static string Host(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url ?? "";

    private static string Slug(string? s)
    {
        var chars = (s ?? "").Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var slug = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return slug.Length > 40 ? slug[..40] : slug;
    }
}
