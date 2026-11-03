using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using WallNut.Service.Configuration;

namespace WallNut.Service.Shutdown;

/// <summary>Actually shuts the machine down (or, in test mode, says it would).</summary>
public interface IShutdownExecutor
{
    Task ExecuteAsync(ShutdownAction action, string reason, CancellationToken ct);
}

/// <summary>The command each operating system uses. Kept separate so it can be tested on any OS.</summary>
public static class ShutdownCommand
{
    public static (string File, string[] Args) For(OSPlatform os, ShutdownAction action, string reason)
    {
        if (os == OSPlatform.Windows)
        {
            // /d 6:12 = unplanned, "Power Failure: Environment" (shows in the Windows event log).
            var comment = ("WALLNut: " + reason).Replace("\"", "'");
            if (comment.Length > 500) comment = comment[..500];
            return action == ShutdownAction.Hibernate
                ? ("shutdown.exe", ["/h", "/f"])
                : ("shutdown.exe", ["/s", "/f", "/t", "0", "/d", "6:12", "/c", comment]);
        }
        if (os == OSPlatform.OSX)
        {
            return action == ShutdownAction.Hibernate
                ? ("/usr/bin/pmset", ["sleepnow"]) // hibernates when hibernatemode is set (the default on laptops)
                : ("/sbin/shutdown", ["-h", "now", "WALLNut: " + reason]);
        }
        return action == ShutdownAction.Hibernate
            ? ("systemctl", ["hibernate"])
            : ("systemctl", ["poweroff"]);
    }

    public static OSPlatform Current =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? OSPlatform.Windows
        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? OSPlatform.OSX
        : OSPlatform.Linux;
}

/// <summary>Runs the real shutdown command. Needs to run as the service account (LocalSystem / root).</summary>
public sealed class SystemShutdownExecutor(ILogger<SystemShutdownExecutor> log) : IShutdownExecutor
{
    public async Task ExecuteAsync(ShutdownAction action, string reason, CancellationToken ct)
    {
        var (file, args) = ShutdownCommand.For(ShutdownCommand.Current, action, reason);
        log.LogCritical("Shutting down now: {File} {Args}", file, string.Join(' ', args));

        var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {file}");
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0)
        {
            var err = (await p.StandardError.ReadToEndAsync(ct)).Trim();
            throw new InvalidOperationException($"{file} exited with code {p.ExitCode}: {err}");
        }
    }
}

/// <summary>
/// Picks test mode or the real thing at the moment of shutdown, from the current settings, so
/// switching test mode off (or on) in the app takes effect without restarting the service.
/// </summary>
public sealed class SwitchingShutdownExecutor(
    Microsoft.Extensions.Options.IOptionsMonitor<WallNutOptions> options,
    DryRunShutdownExecutor dryRun,
    SystemShutdownExecutor real) : IShutdownExecutor
{
    public Task ExecuteAsync(ShutdownAction action, string reason, CancellationToken ct) =>
        (options.CurrentValue.Shutdown.DryRun ? (IShutdownExecutor)dryRun : real).ExecuteAsync(action, reason, ct);
}

/// <summary>Test mode: logs what would happen and does nothing.</summary>
public sealed class DryRunShutdownExecutor(ILogger<DryRunShutdownExecutor> log) : IShutdownExecutor
{
    public Task ExecuteAsync(ShutdownAction action, string reason, CancellationToken ct)
    {
        var (file, args) = ShutdownCommand.For(ShutdownCommand.Current, action, reason);
        log.LogWarning("TEST MODE - would shut down now by running: {File} {Args}", file, string.Join(' ', args));
        return Task.CompletedTask;
    }
}
