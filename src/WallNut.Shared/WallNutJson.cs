using System.Text.Json;
using System.Text.Json.Serialization;
using WallNut.Shared.Notifications;

namespace WallNut.Shared;

/// <summary>
/// JSON for everything the apps read and write, generated at compile time so it also works in
/// trimmed / AOT builds (where reflection-based JSON is switched off).
/// </summary>
// Compact on purpose: the live event stream needs each event on a single line.
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ServiceInfo))]
[JsonSerializable(typeof(ServiceEvent))]
[JsonSerializable(typeof(List<ServiceEvent>))]
[JsonSerializable(typeof(NotificationPreferences))]
[JsonSerializable(typeof(History.HistoryResult))]
[JsonSerializable(typeof(List<History.MetricAvailability>))]
[JsonSerializable(typeof(List<History.HistoryEndpoint>))]
[JsonSerializable(typeof(History.OutageSummary))]
[JsonSerializable(typeof(Settings.EndpointConfig))]
[JsonSerializable(typeof(Settings.ShutdownSettings))]
[JsonSerializable(typeof(Settings.SettingsView))]
[JsonSerializable(typeof(Settings.EndpointTestResult))]
[JsonSerializable(typeof(Settings.CallerInfo))]
[JsonSerializable(typeof(Settings.SettingsProblem))]
[JsonSerializable(typeof(Settings.UpsDetail))]
[JsonSerializable(typeof(Actions.ActionConfig))]
[JsonSerializable(typeof(Actions.ActionTestResult))]
public partial class WallNutJsonContext : JsonSerializerContext;

public static class WallNutJson
{
    public static JsonSerializerOptions Options => WallNutJsonContext.Default.Options;
}
