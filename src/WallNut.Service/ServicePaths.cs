namespace WallNut.Service;

/// <summary>What happened to the machine settings file at startup.</summary>
/// <param name="Path">The file that was looked for (null when none was requested).</param>
/// <param name="Loaded">True when it existed and was read.</param>
/// <param name="Problem">Why it couldn't be read, in plain words (null when fine or simply absent).</param>
public sealed record MachineConfigStatus(string? Path, bool Loaded, string? Problem);

/// <summary>Where an installed service keeps its settings, outside the program folder so upgrades never touch them.</summary>
public static class ServicePaths
{
    /// <summary>Environment variable that points the service at a different config file ("none" = don't load one).</summary>
    public const string ConfigOverrideVariable = "WALLNUT_CONFIG";

    /// <summary>
    /// C:\ProgramData\WALLNut\config.json, /etc/wallnut/config.json or
    /// /Library/Application Support/WALLNut/config.json.
    /// </summary>
    public static string DefaultConfigFile
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable(ConfigOverrideVariable);
            if (!string.IsNullOrWhiteSpace(overridden)) return overridden;
            if (OperatingSystem.IsWindows())
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WALLNut", "config.json");
            if (OperatingSystem.IsMacOS()) return "/Library/Application Support/WALLNut/config.json";
            return "/etc/wallnut/config.json";
        }
    }

    /// <summary>
    /// Where the service keeps data such as history: C:\ProgramData\WALLNut, /var/lib/wallnut or
    /// /Library/Application Support/WALLNut.
    /// </summary>
    public static string DataDirectory =>
        OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WALLNut")
        : OperatingSystem.IsMacOS() ? "/Library/Application Support/WALLNut"
        : "/var/lib/wallnut";

    /// <summary>Fallback for development runs that can't write to <see cref="DataDirectory"/> (it's admin-only).</summary>
    public static string UserDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WALLNut");
}
