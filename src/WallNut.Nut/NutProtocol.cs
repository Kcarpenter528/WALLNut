using System.Text;

namespace WallNut.Nut;

/// <summary>
/// Helpers for the NUT text protocol: one command or reply per line, words separated by
/// spaces, multi-word values in double quotes, with \" and \\ escapes inside quotes.
/// See https://networkupstools.org/docs/developer-guide.chunked/net-protocol.html
/// </summary>
public static class NutProtocol
{
    public const int DefaultPort = 3493;

    /// <summary>Splits a reply line into words, unquoting and unescaping quoted values.</summary>
    public static IReadOnlyList<string> Tokenize(string line)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false, hasToken = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '\\' && i + 1 < line.Length) { current.Append(line[++i]); }
                else if (c == '"') { inQuotes = false; }
                else { current.Append(c); }
            }
            else if (c == '"') { inQuotes = true; hasToken = true; }
            else if (c == ' ')
            {
                if (hasToken) { tokens.Add(current.ToString()); current.Clear(); hasToken = false; }
            }
            else { current.Append(c); hasToken = true; }
        }

        if (inQuotes) throw new FormatException($"Unterminated quote in NUT reply: {line}");
        if (hasToken) tokens.Add(current.ToString());
        return tokens;
    }

    /// <summary>Quotes a value for sending, escaping quotes and backslashes.</summary>
    public static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>Rejects names that could break or inject into a command line.</summary>
    internal static string RequireName(string value, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);
        foreach (char c in value)
        {
            if (char.IsWhiteSpace(c) || c == '"' || c == '\\' || char.IsControl(c))
                throw new ArgumentException($"'{value}' is not a valid NUT name.", paramName);
        }
        return value;
    }
}
