using System.Globalization;
using Avalonia.Media;
using WallNut.Shared;

namespace WallNut.App.Views;

/// <summary>How a UPS's state is shown: a short label and a color, used by cards, pills and lists.</summary>
public static class UpsLook
{
    public static readonly IBrush Good = new SolidColorBrush(Color.Parse("#30A46C"));
    public static readonly IBrush Warning = new SolidColorBrush(Color.Parse("#F5A524"));
    public static readonly IBrush Critical = new SolidColorBrush(Color.Parse("#E5484D"));
    public static readonly IBrush Neutral = new SolidColorBrush(Color.Parse("#8D8D8D"));
    public static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#3E63DD"));

    public static (string Label, IBrush Color) State(UpsSummary u)
    {
        var flags = (u.Status ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!u.Connected) return ("Not connected", Neutral);
        if (u.Stale) return ("Not reporting", Neutral);
        if (flags.Contains("FSD")) return ("Forced shutdown", Critical);
        if (flags.Contains("OFF")) return ("Outlets off", Critical);
        if (u.OnBattery && flags.Contains("LB")) return ("LOW BATTERY", Critical);
        if (u.OnBattery) return ("On battery", Warning);
        if (flags.Contains("OVER")) return ("Overloaded", Critical);
        if (flags.Contains("RB")) return ("Replace battery", Warning);
        if (flags.Contains("BYPASS")) return ("On bypass", Warning);
        if (flags.Contains("CAL")) return ("Calibrating", Warning);
        if (flags.Contains("CHRG")) return ("On line · charging", Good);
        return ("On line", Good);
    }

    /// <summary>Battery color: green, amber below half, red below a fifth.</summary>
    public static IBrush ChargeColor(double? percent) =>
        percent is null ? Neutral : percent < 20 ? Critical : percent < 50 ? Warning : Good;

    public static string Runtime(double? seconds) =>
        seconds is not { } s ? "-"
        : s >= 3600 ? $"{(int)(s / 3600)} h {(int)(s % 3600 / 60)} min"
        : s >= 60 ? $"{(int)(s / 60)} min"
        : $"{s:0} s";

    public static string Percent(double? v) => v is { } x ? x.ToString("0", CultureInfo.CurrentCulture) + "%" : "-";

    public static string Ago(DateTimeOffset? t)
    {
        if (t is not { } when) return "never";
        var s = (DateTimeOffset.UtcNow - when).TotalSeconds;
        return s < 5 ? "just now" : s < 60 ? $"{s:0} s ago" : s < 3600 ? $"{s / 60:0} min ago" : when.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    }
}
