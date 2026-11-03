using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace WallNut.Service.Actions;

/// <summary>
/// The service runs scripts with its own rights (SYSTEM or root when installed), so a script that an
/// ordinary user could change would let them run anything as SYSTEM. This refuses scripts where the
/// file, its folder, or any folder above could be changed, replaced or renamed by anyone other than
/// administrators, SYSTEM/root, or the account the service runs as.
/// </summary>
public static class ScriptSafety
{
    /// <summary>Null when the script is safe to run, otherwise the reason in plain words.</summary>
    public static string? Check(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (!File.Exists(full)) return $"the script {full} doesn't exist.";
            return OperatingSystem.IsWindows() ? Windows.Check(full) : CheckUnix(full);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException)
        {
            return $"couldn't check who can change {path}: {e.Message}";
        }
    }

    internal const string Advice = " Put scripts in a folder only administrators can change, such as {0}.";

    public static string SuggestedFolder => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WALLNut", "scripts")
        : OperatingSystem.IsMacOS() ? "/Library/Application Support/WALLNut/scripts" : "/etc/wallnut/scripts";

    // ---- Windows -------------------------------------------------------------------------

    [SupportedOSPlatform("windows")]
    private static class Windows
    {

        // Rights that let someone change a file's contents or permissions, or delete it.
        private const FileSystemRights FileWrite = FileSystemRights.WriteData | FileSystemRights.AppendData |
            FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

        // Rights on a folder that let someone remove, rename or replace what's in it, or take it over.
        private const FileSystemRights FolderWrite = FileSystemRights.DeleteSubdirectoriesAndFiles |
            FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

        // The script's own folder also must not accept new files (a planted helper could be picked up).
        private const FileSystemRights OwnFolderWrite = FolderWrite | FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories;

        private const int GenericAll = 0x10000000, GenericWrite = 0x40000000;

        [SupportedOSPlatform("windows")]
        public static string? Check(string file)
        {
            using var me = WindowsIdentity.GetCurrent();
            var trusted = new HashSet<SecurityIdentifier>
            {
                new(WellKnownSidType.LocalSystemSid, null),
                new(WellKnownSidType.BuiltinAdministratorsSid, null),
                new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"), // TrustedInstaller
            };
            if (me.User is { } user) trusted.Add(user);

            var problem = CheckEntry(new FileInfo(file).GetAccessControl(), FileWrite, trusted, file);
            if (problem is not null) return problem;

            var dir = new DirectoryInfo(Path.GetDirectoryName(file)!);
            var own = true;
            while (dir is not null)
            {
                problem = CheckEntry(dir.GetAccessControl(), own ? OwnFolderWrite : FolderWrite, trusted, dir.FullName);
                if (problem is not null) return problem;
                own = false;
                dir = dir.Parent;
            }
            return null;
        }

        [SupportedOSPlatform("windows")]
        private static string? CheckEntry(FileSystemSecurity acl, FileSystemRights dangerous, HashSet<SecurityIdentifier> trusted, string what)
        {
            var owner = acl.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (owner is not null && !trusted.Contains(owner))
                return $"{what} is owned by {Name(owner)}, who could change it." + string.Format(ScriptSafety.Advice, SuggestedFolder);

            foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow) continue;
                if (rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)) continue; // applies to children, not this
                var sid = (SecurityIdentifier)rule.IdentityReference;
                if (trusted.Contains(sid)) continue;
                if (sid.Value is "S-1-3-0" or "S-1-3-4") continue; // CREATOR OWNER / OWNER RIGHTS: the owner, checked above
                // App package and capability SIDs only ever narrow what a sandboxed app may do: the
                // app's user must be allowed as well, and that is checked through the user's own entries.
                if (sid.Value.StartsWith("S-1-15-2-", StringComparison.Ordinal) || sid.Value.StartsWith("S-1-15-3-", StringComparison.Ordinal)) continue;
                var raw = (int)rule.FileSystemRights;
                if ((rule.FileSystemRights & dangerous) != 0 || (raw & (GenericAll | GenericWrite)) != 0)
                    return $"{Name(sid)} can change {what}." + string.Format(ScriptSafety.Advice, SuggestedFolder);
            }
            return null;
        }

        [SupportedOSPlatform("windows")]
        private static string Name(SecurityIdentifier sid)
        {
            try { return sid.Translate(typeof(NTAccount)).Value; }
            catch (IdentityNotMappedException) { return sid.Value; }
        }
    }

    // ---- macOS / Linux -------------------------------------------------------------------

    [UnsupportedOSPlatform("windows")]
    private static string? CheckUnix(string file)
    {
        var me = geteuid();
        string? Check(string path, bool isFile)
        {
            var mode = File.GetUnixFileMode(path);
            var owner = OwnerOf(path);
            if (owner is null) return $"couldn't find who owns {path}.";
            if (owner != 0 && owner != me) return $"{path} is owned by uid {owner}, who could change it." + string.Format(ScriptSafety.Advice, SuggestedFolder);
            var othersWrite = (mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0;
            // A shared folder like /tmp is fine above the script: with the sticky bit, people can only
            // remove or rename their own entries.
            var sticky = !isFile && (mode & UnixFileMode.StickyBit) != 0;
            if (othersWrite && !sticky) return $"{path} can be changed by other users (group or other write)." + string.Format(ScriptSafety.Advice, SuggestedFolder);
            return null;
        }

        if (Check(file, true) is { } p) return p;
        var dir = Path.GetDirectoryName(file);
        var own = true;
        while (!string.IsNullOrEmpty(dir))
        {
            if (Check(dir, false) is { } q) return q;
            if (own && (File.GetUnixFileMode(dir) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
                return $"{dir} lets other users add files next to the script." + string.Format(ScriptSafety.Advice, SuggestedFolder);
            own = false;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    [UnsupportedOSPlatform("windows")]
    private static uint? OwnerOf(string path)
    {
        // .NET doesn't expose the owner; stat(1) does, with different flags on macOS and Linux.
        var psi = new ProcessStartInfo("stat") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("-L");
        psi.ArgumentList.Add(OperatingSystem.IsMacOS() ? "-f" : "-c");
        psi.ArgumentList.Add("%u");
        psi.ArgumentList.Add(path);
        using var p = Process.Start(psi);
        if (p is null) return null;
        var output = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit(5000);
        return p.ExitCode == 0 && uint.TryParse(output, out var uid) ? uid : null;
    }

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = false)]
    private static extern uint geteuid();
}
