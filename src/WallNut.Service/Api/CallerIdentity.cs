using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.AspNetCore.Connections.Features;

namespace WallNut.Service.Api;

/// <summary>Works out who is calling the local API (replaceable in tests).</summary>
public interface ICallerIdentity
{
    (string? User, bool IsAdmin) Get(HttpContext http);
}

/// <summary>The real check, from the pipe or socket the request arrived on.</summary>
public sealed class ConnectionCallerIdentity : ICallerIdentity
{
    public (string? User, bool IsAdmin) Get(HttpContext http) => CallerIdentity.Get(http);
}

/// <summary>
/// Who is calling the local API. Settings changes are allowed only from an elevated administrator
/// (or SYSTEM) on Windows, or root on macOS/Linux. A normal user can still read status and history.
/// </summary>
public static class CallerIdentity
{
    public static (string? User, bool IsAdmin) Get(HttpContext http)
    {
        try
        {
            if (OperatingSystem.IsWindows() && http.Features.Get<IConnectionNamedPipeFeature>()?.NamedPipe is { } pipe)
                return FromPipe(pipe);
            if (!OperatingSystem.IsWindows() && http.Features.Get<IConnectionSocketFeature>()?.Socket is { } socket)
                return FromSocket(socket);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SocketException or InvalidOperationException)
        {
            // Can't tell who it is: treat as an ordinary user.
        }
        return (null, false);
    }

    [SupportedOSPlatform("windows")]
    private static (string?, bool) FromPipe(System.IO.Pipes.NamedPipeServerStream pipe)
    {
        (string?, bool) result = (null, false);
        using var service = WindowsIdentity.GetCurrent();
        var serviceAccount = service.User;
        // The client connects with "Identification" level, so we can look at its token without acting as it.
        pipe.RunAsClient(() =>
        {
            using var id = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            var principal = new WindowsPrincipal(id);
            // IsInRole only sees Administrators in an elevated token, which is what UAC intends.
            // The account the service runs as could edit its settings file anyway, so it's trusted too
            // (in production that's SYSTEM; for a developer's own copy, it's them).
            result = (id.Name, id.IsSystem || principal.IsInRole(WindowsBuiltInRole.Administrator) || id.User == serviceAccount);
        });
        return result;
    }

    [UnsupportedOSPlatform("windows")]
    private static (string?, bool) FromSocket(Socket socket)
    {
        Span<byte> buffer = stackalloc byte[16];
        int uid;
        if (OperatingSystem.IsLinux())
        {
            // SOL_SOCKET (1), SO_PEERCRED (17): struct ucred { pid_t pid; uid_t uid; gid_t gid; }
            var n = socket.GetRawSocketOption(1, 17, buffer);
            if (n < 8) return (null, false);
            uid = BitConverter.ToInt32(buffer[4..8]);
        }
        else if (OperatingSystem.IsMacOS())
        {
            // SOL_LOCAL (0), LOCAL_PEERCRED (1): struct xucred { u_int cr_version; uid_t cr_uid; ... }
            var n = socket.GetRawSocketOption(0, 1, buffer);
            if (n < 8) return (null, false);
            uid = BitConverter.ToInt32(buffer[4..8]);
        }
        else return (null, false);
        // root, or the account the service itself runs as (which could edit its settings file anyway).
        return (UserName((uint)uid) ?? $"uid {uid}", uid == 0 || (uint)uid == geteuid());
    }

    /// <summary>The account name for a user id, from the system's user database.</summary>
    [UnsupportedOSPlatform("windows")]
    private static string? UserName(uint uid)
    {
        try
        {
            // struct passwd starts with char* pw_name on both Linux and macOS.
            var entry = getpwuid(uid);
            if (entry == IntPtr.Zero) return null;
            var name = System.Runtime.InteropServices.Marshal.ReadIntPtr(entry);
            return name == IntPtr.Zero ? null : System.Runtime.InteropServices.Marshal.PtrToStringUTF8(name);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = false)]
    private static extern uint geteuid();

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = false)]
    private static extern IntPtr getpwuid(uint uid);
}
