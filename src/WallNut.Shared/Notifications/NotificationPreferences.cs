using System.Text.Json;

namespace WallNut.Shared.Notifications;

/// <summary>
/// One person's desktop notification choices. Stored per user, so changing them needs no admin
/// rights and doesn't affect anyone else on the machine.
/// </summary>
public sealed class NotificationPreferences
{
    /// <summary>Master switch. Shutdown warnings and test notifications are shown regardless.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Per group (NotificationGroup name). Missing = on.</summary>
    public Dictionary<string, bool> Groups { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Per event kind. Missing = the kind's default from <see cref="NotificationCatalog"/>.</summary>
    public Dictionary<string, bool> Kinds { get; set; } = new(StringComparer.Ordinal);

    /// <summary>UPS endpoint ids this person doesn't want to hear about.</summary>
    public HashSet<string> MutedUps { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Wait this long before announcing a power failure. If power comes back first (a blip),
    /// neither "power failure" nor "power restored" is shown. 0 = announce immediately.
    /// </summary>
    public double OnBatteryDelaySeconds { get; set; } = 5;

    public bool GroupEnabled(NotificationGroup g) => !Groups.TryGetValue(g.ToString(), out var on) || on;

    public bool KindEnabled(NotificationKind k) => Kinds.TryGetValue(k.Kind, out var on) ? on : k.OnByDefault;

    /// <summary>Should this event pop up a notification for this person?</summary>
    public bool Wants(ServiceEvent e)
    {
        var kind = NotificationCatalog.Find(e.Kind);
        if (kind.AlwaysShown) return true;
        if (!Enabled) return false;
        if (MutedUps.Contains(e.EndpointId)) return false;
        return GroupEnabled(kind.Group) && KindEnabled(kind);
    }

    // ---- Storage ----------------------------------------------------------------------

    /// <summary>%APPDATA%\WALLNut, ~/Library/Application Support/WALLNut or ~/.config/WALLNut.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WALLNut", "notifications.json");

    /// <summary>Loads saved preferences, or defaults if there are none (or the file is damaged).</summary>
    public static NotificationPreferences Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize(File.ReadAllText(path), WallNutJsonContext.Default.NotificationPreferences) is { } p)
            {
                // The JSON reader builds plain collections; restore case-insensitive UPS ids.
                p.MutedUps = new HashSet<string>(p.MutedUps ?? [], StringComparer.OrdinalIgnoreCase);
                p.Groups ??= new(StringComparer.Ordinal);
                p.Kinds ??= new(StringComparer.Ordinal);
                return p;
            }
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { }
        return new();
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        using (var file = File.Create(temp))
        using (var writer = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true })) // readable if opened by hand
        {
            JsonSerializer.Serialize(writer, this, WallNutJsonContext.Default.NotificationPreferences);
        }
        File.Move(temp, path, overwrite: true); // never leave a half-written file
    }
}
