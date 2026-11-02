namespace WallNut.Nut;

/// <summary>Error codes a NUT server can send back as "ERR &lt;code&gt;".</summary>
public enum NutErrorCode
{
    Unknown,
    AccessDenied,
    UnknownUps,
    VarNotSupported,
    CmdNotSupported,
    InvalidArgument,
    InstcmdFailed,
    SetFailed,
    Readonly,
    TooLong,
    FeatureNotSupported,
    FeatureNotConfigured,
    AlreadySslMode,
    DriverNotConnected,
    DataStale,
    AlreadyLoggedIn,
    InvalidPassword,
    AlreadySetPassword,
    InvalidUsername,
    AlreadySetUsername,
    UsernameRequired,
    PasswordRequired,
    UnknownCommand,
    InvalidValue,
}

/// <summary>The NUT server answered a command with an error.</summary>
public sealed class NutException : Exception
{
    public NutErrorCode Code { get; }

    /// <summary>The error word exactly as the server sent it, e.g. "ACCESS-DENIED".</summary>
    public string RawCode { get; }

    public NutException(string rawCode, string command)
        : base($"NUT server returned ERR {rawCode} for: {command}")
    {
        RawCode = rawCode;
        Code = Parse(rawCode);
    }

    public static NutErrorCode Parse(string rawCode)
    {
        // "ACCESS-DENIED" -> "AccessDenied"
        var name = string.Concat(rawCode.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => char.ToUpperInvariant(p[0]) + p[1..].ToLowerInvariant()));
        return Enum.TryParse<NutErrorCode>(name, out var code) ? code : NutErrorCode.Unknown;
    }
}
