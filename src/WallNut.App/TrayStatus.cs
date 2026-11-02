using System.Globalization;
using WallNut.Shared;

namespace WallNut.App;

public enum TrayState { Ok, Warning, Problem, Offline }

/// <summary>Turns the service's status into the tray icon state and a short tooltip.</summary>
public static class TrayStatus
{
    public static (TrayState State, string Text) Describe(ServiceInfo? info)
    {
        if (info is null) return (TrayState.Offline, "WALLNut service isn't running");
        if (info.ShutdownInitiated && !info.ShutdownTestMode) return (TrayState.Problem, "Shutting down because of a power problem");
        if (info.Ups.Count == 0) return (TrayState.Warning, "No UPS is configured");

        var problems = new List<string>();
        var state = TrayState.Ok;
        foreach (var u in info.Ups)
        {
            var flags = (u.Status ?? "").Split(' ');
            if (u.OnBattery)
            {
                var low = flags.Contains("LB");
                state = low ? TrayState.Problem : Max(state, TrayState.Warning);
                problems.Add($"{u.DisplayName}: {(low ? "LOW BATTERY" : "on battery")}{Charge(u)}");
            }
            else if (!u.Connected)
            {
                state = Max(state, TrayState.Warning);
                problems.Add($"{u.DisplayName}: not connected");
            }
            else if (flags.Contains("RB") || flags.Contains("OVER") || flags.Contains("ALARM"))
            {
                state = Max(state, TrayState.Warning);
                problems.Add($"{u.DisplayName}: needs attention ({u.Status})");
            }
        }

        var text = problems.Count > 0
            ? string.Join("\n", problems)
            : info.Ups.Count == 1
                ? $"{info.Ups[0].DisplayName}: on line power{Charge(info.Ups[0])}"
                : $"All {info.Ups.Count} UPSes on line power";
        if (info.ShutdownTestMode) text += "\n(shutdown test mode)";
        return (state, text);
    }

    private static TrayState Max(TrayState a, TrayState b) => (TrayState)Math.Max((int)a, (int)b);

    private static string Charge(UpsSummary u) =>
        u.ChargePercent is { } c ? $", battery {c.ToString("0", CultureInfo.InvariantCulture)}%" : "";
}
