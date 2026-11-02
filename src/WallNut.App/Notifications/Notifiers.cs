using System.Diagnostics;
using WallNut.Shared;

namespace WallNut.App.Notifications;

/// <summary>Shows one native desktop notification.</summary>
public interface INotifier
{
    void Show(string title, string body, NotificationUrgency urgency, string? group = null);
}

public static class Notifier
{
    /// <summary>The right notifier for this operating system.</summary>
    public static INotifier ForThisPlatform()
    {
#if WINDOWS
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) return new WindowsToastNotifier();
#endif
        if (OperatingSystem.IsMacOS()) return new MacNotifier();
        if (OperatingSystem.IsLinux()) return new LinuxNotifier();
        return new TraceNotifier();
    }

    internal static void Run(string file, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
        }
        catch (Exception e) { Trace.TraceWarning($"WALLNut: couldn't run {file} to show a notification: {e.Message}"); }
    }
}

/// <summary>macOS Notification Center, via AppleScript (works without a signed app bundle).</summary>
public sealed class MacNotifier : INotifier
{
    public void Show(string title, string body, NotificationUrgency urgency, string? group = null)
    {
        static string Q(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        var sound = urgency == NotificationUrgency.Critical ? " sound name \"Sosumi\"" : "";
        Notifier.Run("/usr/bin/osascript", "-e", $"display notification {Q(body)} with title \"WALLNut\" subtitle {Q(title)}{sound}");
    }
}

/// <summary>Linux desktops (GNOME, KDE, XFCE...) through the freedesktop notification service.</summary>
public sealed class LinuxNotifier : INotifier
{
    public void Show(string title, string body, NotificationUrgency urgency, string? group = null)
    {
        var level = urgency switch
        {
            NotificationUrgency.Critical => "critical",
            NotificationUrgency.Low => "low",
            _ => "normal",
        };
        Notifier.Run("notify-send", "--app-name=WALLNut", $"--urgency={level}", "--icon=battery-caution", title, body);
    }
}

/// <summary>Last resort: write to the debug trace.</summary>
public sealed class TraceNotifier : INotifier
{
    public void Show(string title, string body, NotificationUrgency urgency, string? group = null) =>
        Trace.TraceInformation($"WALLNut notification [{urgency}] {title}: {body}");
}

#if WINDOWS
/// <summary>
/// Windows toast notifications for an unpackaged app. Registers an app identity under the
/// current user (no admin needed) so toasts show "WALLNut" with its icon in Action Center.
/// Critical toasts stay on screen until dismissed.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows10.0.17763.0")]
public sealed class WindowsToastNotifier : INotifier
{
    public const string AppId = "WALLNut.Tray";
    private readonly Windows.UI.Notifications.ToastNotifier _notifier;

    public WindowsToastNotifier()
    {
        RegisterAppIdentity();
        _notifier = Windows.UI.Notifications.ToastNotificationManager.CreateToastNotifier(AppId);
    }

    private static void RegisterAppIdentity()
    {
        // Copy the icon somewhere stable so Windows can show it.
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WALLNut");
        Directory.CreateDirectory(dir);
        var icon = Path.Combine(dir, "wallnut.png");
        if (!File.Exists(icon))
        {
            using var src = Avalonia.Platform.AssetLoader.Open(new Uri("avares://WALLNut/Assets/wallnut.png"));
            using var dst = File.Create(icon);
            src.CopyTo(dst);
        }

        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{AppId}");
        key.SetValue("DisplayName", "WALLNut");
        key.SetValue("IconUri", icon);
        key.SetValue("IconBackgroundColor", "FF7A4E26");
    }

    public void Show(string title, string body, NotificationUrgency urgency, string? group = null)
    {
        static string X(string s) => System.Security.SecurityElement.Escape(s) ?? "";
        var critical = urgency == NotificationUrgency.Critical;
        var xml = $"""
            <toast{(critical ? " scenario=\"reminder\"" : "")}>
              <visual><binding template="ToastGeneric">
                <text>{X(title)}</text>
                <text>{X(body)}</text>
              </binding></visual>
              {(critical ? "<actions><action content=\"Dismiss\" arguments=\"dismiss\" activationType=\"system\"/></actions>" : "")}
              {(urgency == NotificationUrgency.Low ? "<audio silent=\"true\"/>" : "")}
            </toast>
            """;
        var doc = new Windows.Data.Xml.Dom.XmlDocument();
        doc.LoadXml(xml);
        var toast = new Windows.UI.Notifications.ToastNotification(doc);
        if (group is not null) toast.Group = group.Length > 64 ? group[..64] : group;
        _notifier.Show(toast);
    }
}
#endif
