using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using WallNut.Shared;
using WallNut.Shared.Settings;

namespace WallNut.App.Views;

/// <summary>Add, edit, test and remove UPS connections (NUT servers by IP or DNS name).</summary>
public sealed class ConnectionsView : UserControl
{
    private readonly App _app;
    private readonly AdminBanner _admin;
    private readonly StackPanel _list = new() { Spacing = 8 };
    private readonly Button _add = new() { Content = "+ Add a UPS", Classes = { "accent" } };
    private readonly Border _editorCard = new() { Classes = { "card" }, Padding = new Thickness(20, 16), IsVisible = false, Margin = new Thickness(0, 14, 0, 0) };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    private SettingsPage? _settings;
    private SettingsData? _data;

    // Editor fields.
    private readonly TextBlock _editorTitle = new() { FontSize = 16, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 10) };
    private readonly TextBox _name = new() { PlaceholderText = "e.g. Synology NAS" };
    private readonly TextBox _id = new() { PlaceholderText = "short id, e.g. nas" };
    private readonly TextBox _host = new() { PlaceholderText = "IP address or DNS name, e.g. 192.168.1.10 or diskstation.local" };
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Value = 3493, FormatString = "0", Width = 130, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _ups = new() { PlaceholderText = "Synology and QNAP use \"ups\"", Text = "ups" };
    private readonly WrapPanel _upsChoices = new();
    private readonly ComboBox _role = new() { ItemsSource = new[] { "Just monitor it", "This computer is plugged into it" }, SelectedIndex = 0, MinWidth = 300 };
    private readonly TextBox _user = new() { PlaceholderText = "NUT username (Synology: monuser)" };
    private readonly TextBox _password = new() { PasswordChar = '•', PlaceholderText = "NUT password (Synology: secret)" };
    private readonly NumericUpDown _poll = new() { Minimum = 1, Maximum = 300, Value = 5, FormatString = "0", Width = 130, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly CheckBox _enabled = new() { Content = "Enabled", IsChecked = true };
    private readonly TextBlock _testResult = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _save = new() { Content = "Save", Classes = { "accent" } };
    private EndpointConfig? _editing; // null = adding

    public ConnectionsView(App app)
    {
        _app = app;
        _admin = new AdminBanner(app);
        _add.Click += (_, _) => Edit(null);

        var test = new Button { Content = "Test connection" };
        test.Click += async (_, _) => await TestAsync();
        _save.Click += async (_, _) => await SaveAsync();
        var cancel = new Button { Content = "Cancel" };
        cancel.Click += (_, _) => _editorCard.IsVisible = false;
        var synology = new Button { Content = "Use Synology defaults" };
        synology.Click += (_, _) => { _ups.Text = "ups"; _user.Text = "monuser"; _password.Text = "secret"; _port.Value = 3493; };
        _name.TextChanged += (_, _) => { if (_editing is null && _id.IsEnabled) _id.Text = Slug(_name.Text); };
        _role.SelectionChanged += (_, _) => RoleHint();

        _editorCard.Child = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                _editorTitle,
                Field("Name", _name),
                Field("Id", _id, "Used in the settings file and logs. Letters, digits, '-' and '_'."),
                Field("Host", _host, "The NUT server: your NAS or whatever the UPS is plugged into. A DNS name keeps working if its address changes."),
                Field("Port", _port, "3493 unless you changed it on the server."),
                Field("UPS name", new StackPanel { Spacing = 6, Children = { _ups, _upsChoices } }),
                Field("This UPS", _role),
                Field("Username", _user),
                Field("Password", _password),
                Field("Check every", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
                    Children = { _poll, new TextBlock { Text = "seconds", VerticalAlignment = VerticalAlignment.Center } } }),
                _enabled,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 6, 0, 0),
                    Children = { test, _save, cancel, synology } },
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
                    new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Margin = new Thickness(0, 0, 0, 12),
                        Text = "Each UPS gets its own connection to the NUT server that manages it. Mark the ones this computer is plugged into, so WALLNut can shut it down safely when their batteries run low." },
                    _admin, _list, new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0), Children = { _add } },
                    _editorCard, _message,
                },
            },
        };
    }

    /// <summary>Called by the Settings page whenever it (re)loads the service's settings.</summary>
    internal void Show(SettingsPage settings, SettingsData data)
    {
        _settings = settings;
        _data = data;
        _admin.Show(data.View);
        _add.IsEnabled = data.View.CanEdit;
        _save.IsEnabled = data.View.CanEdit;
        _list.Children.Clear();
        foreach (var e in data.View.Endpoints) _list.Children.Add(Row(e, data.View.CanEdit, data.Live));
        if (data.View.Endpoints.Count == 0)
            _list.Children.Add(new TextBlock { Text = "No UPS yet. Add the NUT server your UPS is connected to (often your NAS).", Opacity = 0.7 });
    }

    private Control Row(EndpointConfig e, bool canEdit, IReadOnlyList<UpsSummary> live)
    {
        var state = live.FirstOrDefault(u => u.Id.Equals(e.Id, StringComparison.OrdinalIgnoreCase));
        var (label, color) = !e.Enabled ? ("Disabled", UpsLook.Neutral) : state is null ? ("Starting...", UpsLook.Neutral) : UpsLook.State(state);

        var edit = new Button { Content = "Edit", IsEnabled = canEdit };
        edit.Click += (_, _) => Edit(e);
        var remove = new Button { Content = "Remove", IsEnabled = canEdit };
        remove.Click += async (_, _) =>
        {
            if (remove.Tag is null) { remove.Tag = true; remove.Content = "Click again to remove"; return; }
            await RunAsync(() => _app.Connection.Client.DeleteEndpointAsync(e.Id), $"Removed {e.DisplayName ?? e.Id}.");
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        grid.Children.Add(new StackPanel
        {
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children =
                {
                    new TextBlock { Text = e.DisplayName ?? e.Id, FontWeight = FontWeight.SemiBold, FontSize = 15 },
                    new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 1), Background = color,
                        Child = new TextBlock { Text = label, FontSize = 11, Foreground = Brushes.White, FontWeight = FontWeight.SemiBold } },
                } },
                new TextBlock { Opacity = 0.65, FontSize = 12, Text =
                    $"{e.Ups}@{e.Host}:{e.Port}  ·  " +
                    (e.Role == "PowersThisMachine" ? "powers this computer" : "monitor only") +
                    (e.Username is { } u ? $"  ·  login {u}" : "") + $"  ·  every {e.PollIntervalSeconds:0.#} s  ·  id {e.Id}" },
            },
        });
        Grid.SetColumn(edit, 1); edit.Margin = new Thickness(8, 0); grid.Children.Add(edit);
        Grid.SetColumn(remove, 2); grid.Children.Add(remove);
        return new Border { Classes = { "card" }, Padding = new Thickness(16, 12), Child = grid };
    }

    private void Edit(EndpointConfig? e)
    {
        _editing = e;
        _editorTitle.Text = e is null ? "Add a UPS" : $"Edit {e.DisplayName ?? e.Id}";
        _name.Text = e?.DisplayName ?? "";
        _id.Text = e?.Id ?? "";
        _id.IsEnabled = e is null;
        _host.Text = e?.Host ?? "";
        _port.Value = e?.Port ?? 3493;
        _ups.Text = e?.Ups ?? "ups";
        _role.SelectedIndex = e?.Role == "PowersThisMachine" ? 1 : 0;
        _user.Text = e?.Username ?? "";
        _password.Text = "";
        _password.PlaceholderText = e?.PasswordSet == true ? "Leave blank to keep the saved password" : "NUT password (Synology: secret)";
        _poll.Value = (decimal)(e?.PollIntervalSeconds ?? 5);
        _enabled.IsChecked = e?.Enabled ?? true;
        _testResult.Text = "";
        _upsChoices.Children.Clear();
        RoleHint();
        _editorCard.IsVisible = true;
        _editorCard.BringIntoView();
    }

    private void RoleHint()
    {
        if (_role.SelectedIndex == 1 && string.IsNullOrWhiteSpace(_user.Text))
            _testResult.Text = "A UPS that powers this computer needs a NUT login, so WALLNut can register with the server like other NUT clients do.";
    }

    private EndpointConfig FromForm() => new()
    {
        Id = (_id.Text ?? "").Trim(),
        DisplayName = string.IsNullOrWhiteSpace(_name.Text) ? null : _name.Text.Trim(),
        Host = (_host.Text ?? "").Trim(),
        Port = (int)(_port.Value ?? 3493),
        Ups = (_ups.Text ?? "").Trim(),
        Role = _role.SelectedIndex == 1 ? "PowersThisMachine" : "Monitor",
        Username = string.IsNullOrWhiteSpace(_user.Text) ? null : _user.Text.Trim(),
        Password = string.IsNullOrEmpty(_password.Text) ? null : _password.Text,
        PollIntervalSeconds = (double)(_poll.Value ?? 5),
        TimeoutSeconds = _editing?.TimeoutSeconds ?? 10,
        PowerValue = _editing?.PowerValue ?? 1,
        Enabled = _enabled.IsChecked == true,
    };

    private async Task TestAsync()
    {
        _testResult.Text = "Testing...";
        _testResult.ClearValue(TextBlock.ForegroundProperty);
        _upsChoices.Children.Clear();
        try
        {
            var r = await _app.Connection.Client.TestEndpointAsync(FromForm());
            _testResult.Text = r.Message;
            _testResult.Foreground = r.Ok ? UpsLook.Good : UpsLook.Critical;
            if (!r.Ok && r.AvailableUps.Count > 0)
            {
                _upsChoices.Children.Add(new TextBlock { Text = "Pick one:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
                foreach (var name in r.AvailableUps)
                {
                    var b = new Button { Content = name, Margin = new Thickness(0, 0, 6, 0) };
                    b.Click += (_, _) => { _ups.Text = name; _upsChoices.Children.Clear(); };
                    _upsChoices.Children.Add(b);
                }
            }
        }
        catch (Exception e) { _testResult.Text = "Couldn't ask the WALLNut service to test it: " + e.Message; }
    }

    private async Task SaveAsync()
    {
        var e = FromForm();
        if (string.IsNullOrWhiteSpace(e.Id)) e = e with { Id = Slug(e.DisplayName ?? e.Host) };
        await RunAsync(() => _app.Connection.Client.SaveEndpointAsync(e),
            $"Saved {e.DisplayName ?? e.Id}. WALLNut is connecting to it now.");
        if (_message.Foreground == UpsLook.Good) _editorCard.IsVisible = false;
    }

    private async Task RunAsync(Func<Task> change, string done)
    {
        try
        {
            await change();
            _message.Text = done;
            _message.Foreground = UpsLook.Good;
            if (_settings is not null) await _settings.ReloadAsync();
        }
        catch (SettingsException e)
        {
            _message.Text = string.Join("\n", e.Problems);
            _message.Foreground = UpsLook.Critical;
        }
        catch (Exception e)
        {
            _message.Text = "Couldn't save: " + e.Message;
            _message.Foreground = UpsLook.Critical;
        }
    }

    private static string Slug(string? s)
    {
        var chars = (s ?? "").Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var slug = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return slug.Length > 40 ? slug[..40] : slug;
    }

    internal static Control Field(string label, Control input, string? hint = null)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("150,*") };
        grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 12, 0) });
        var right = new StackPanel { Spacing = 3, Children = { input } };
        if (hint is not null) right.Children.Add(new TextBlock { Text = hint, FontSize = 12, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        return grid;
    }
}
