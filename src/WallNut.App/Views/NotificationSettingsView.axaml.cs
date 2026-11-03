using Avalonia.Controls;
using Avalonia.Interactivity;
using WallNut.Shared;

namespace WallNut.App.Views;

/// <summary>This user's desktop notification choices (a tab in Settings).</summary>
public partial class NotificationSettingsView : UserControl
{
    private readonly App _app;
    private readonly SettingsViewModel _vm;

    // For the XAML designer.
    public NotificationSettingsView() : this((App)Avalonia.Application.Current!) { }

    public NotificationSettingsView(App app)
    {
        _app = app;
        _vm = new SettingsViewModel(app.Preferences);
        DataContext = _vm;
        InitializeComponent();

        this.FindControl<Button>("SaveButton")!.Click += Save;
        this.FindControl<Button>("TestButton")!.Click += Test;
    }

    public void UpdateService(ServiceInfo? info) => _vm.UpdateService(info);

    private void Save(object? sender, RoutedEventArgs e)
    {
        try
        {
            _app.SavePreferences(_vm.ToPreferences());
            _vm.Message = $"Saved at {DateTime.Now:t}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _vm.Message = $"Couldn't save: {ex.Message}";
        }
    }

    private async void Test(object? sender, RoutedEventArgs e)
    {
        _vm.Message = "Sending...";
        _vm.Message = await _app.SendTestAsync();
    }
}
