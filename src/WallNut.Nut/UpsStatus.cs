namespace WallNut.Nut;

/// <summary>Flags reported in the ups.status variable, e.g. "OL CHRG" or "OB LB".</summary>
[Flags]
public enum UpsStatusFlags
{
    None = 0,
    OnLine = 1 << 0,          // OL
    OnBattery = 1 << 1,       // OB
    LowBattery = 1 << 2,      // LB
    HighBattery = 1 << 3,     // HB
    ReplaceBattery = 1 << 4,  // RB
    Charging = 1 << 5,        // CHRG
    Discharging = 1 << 6,     // DISCHRG
    Bypass = 1 << 7,          // BYPASS
    Calibrating = 1 << 8,     // CAL
    Offline = 1 << 9,         // OFF
    Overloaded = 1 << 10,     // OVER
    Trimming = 1 << 11,       // TRIM
    Boosting = 1 << 12,       // BOOST
    ForcedShutdown = 1 << 13, // FSD
    Alarm = 1 << 14,          // ALARM
}

/// <summary>A parsed ups.status value.</summary>
public readonly record struct UpsStatus(UpsStatusFlags Flags, string Raw)
{
    private static readonly Dictionary<string, UpsStatusFlags> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["OL"] = UpsStatusFlags.OnLine,
        ["OB"] = UpsStatusFlags.OnBattery,
        ["LB"] = UpsStatusFlags.LowBattery,
        ["HB"] = UpsStatusFlags.HighBattery,
        ["RB"] = UpsStatusFlags.ReplaceBattery,
        ["CHRG"] = UpsStatusFlags.Charging,
        ["DISCHRG"] = UpsStatusFlags.Discharging,
        ["BYPASS"] = UpsStatusFlags.Bypass,
        ["CAL"] = UpsStatusFlags.Calibrating,
        ["OFF"] = UpsStatusFlags.Offline,
        ["OVER"] = UpsStatusFlags.Overloaded,
        ["TRIM"] = UpsStatusFlags.Trimming,
        ["BOOST"] = UpsStatusFlags.Boosting,
        ["FSD"] = UpsStatusFlags.ForcedShutdown,
        ["ALARM"] = UpsStatusFlags.Alarm,
    };

    public static UpsStatus Parse(string? raw)
    {
        raw ??= "";
        var flags = UpsStatusFlags.None;
        foreach (var word in raw.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Map.TryGetValue(word, out var f)) flags |= f;
        }
        return new UpsStatus(flags, raw);
    }

    public bool Has(UpsStatusFlags flag) => (Flags & flag) == flag;
    public bool IsOnBattery => Has(UpsStatusFlags.OnBattery);
    public bool IsLowBattery => Has(UpsStatusFlags.LowBattery);
    public bool IsForcedShutdown => Has(UpsStatusFlags.ForcedShutdown);

    /// <summary>The standard NUT rule for "this UPS is about to run out": on battery and low battery.</summary>
    public bool IsCritical => (IsOnBattery && IsLowBattery) || IsForcedShutdown;

    public override string ToString() => Raw;
}
