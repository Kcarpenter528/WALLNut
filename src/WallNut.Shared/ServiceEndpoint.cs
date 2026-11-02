namespace WallNut.Shared;

/// <summary>
/// Where the service listens for local apps: a named pipe on Windows, a Unix socket elsewhere.
/// Neither can be reached from the network.
/// </summary>
public static class ServiceEndpoint
{
    public const string DefaultPipeName = "WallNut.Service";

    /// <summary>Environment variable that overrides the pipe name or socket path (handy for tests and side-by-side runs).</summary>
    public const string OverrideVariable = "WALLNUT_ENDPOINT";

    public static string Default
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable(OverrideVariable);
            if (!string.IsNullOrWhiteSpace(overridden)) return overridden;
            if (OperatingSystem.IsWindows()) return DefaultPipeName;

            // The installed service (root) listens on a system-wide socket that the tray app (a normal
            // user) must find too. Without an installed service, a developer run uses a per-user path.
            var system = OperatingSystem.IsMacOS() ? "/var/run/wallnut/wallnut.sock" : "/run/wallnut/wallnut.sock";
            if (Environment.UserName == "root" || File.Exists(system)) return system;
            var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            return Path.Combine(string.IsNullOrEmpty(runtimeDir) ? Path.GetTempPath() : runtimeDir, "wallnut.sock");
        }
    }

    /// <summary>Unix socket paths contain a slash; anything else is a Windows pipe name.</summary>
    public static bool IsSocketPath(string endpoint) => endpoint.Contains('/');
}
