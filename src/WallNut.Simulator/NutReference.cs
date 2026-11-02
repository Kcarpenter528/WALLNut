using System.Reflection;
using System.Text.RegularExpressions;
using WallNut.Nut;

namespace WallNut.Simulator;

/// <summary>Variable and command descriptions from NUT's cmdvartab file.</summary>
public static partial class NutReference
{
    private static readonly Lazy<(Dictionary<string, string> Vars, Dictionary<string, string> Cmds)> Tables =
        new(LoadCmdVarTab);

    public static IReadOnlyDictionary<string, string> VariableDescriptions => Tables.Value.Vars;
    public static IReadOnlyDictionary<string, string> CommandDescriptions => Tables.Value.Cmds;

    public static string DescribeVariable(string name) =>
        VariableDescriptions.TryGetValue(name, out var d) ? d
        : VariableDescriptions.TryGetValue(Generalize(name), out d) ? d
        : "Description unavailable";

    public static string DescribeCommand(string name) =>
        CommandDescriptions.TryGetValue(name, out var d) ? d
        : CommandDescriptions.TryGetValue(Generalize(name), out d) ? d
        : "Description unavailable";

    /// <summary>The NUT naming spec (docs/nut-names.txt), as text.</summary>
    public static string ReadNamingSpec() => ReadResource("nut-names.txt");

    // outlet.7.load.off -> outlet.1.load.off, so numbered items share descriptions.
    private static string Generalize(string name) => IndexPattern().Replace(name, ".1.");

    private static (Dictionary<string, string>, Dictionary<string, string>) LoadCmdVarTab()
    {
        var vars = new Dictionary<string, string>(StringComparer.Ordinal);
        var cmds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in ReadResource("cmdvartab").Split('\n'))
        {
            if (!line.StartsWith("VARDESC ", StringComparison.Ordinal) &&
                !line.StartsWith("CMDDESC ", StringComparison.Ordinal)) continue;
            var t = NutProtocol.Tokenize(line.TrimEnd('\r'));
            if (t.Count < 3) continue;
            (t[0] == "VARDESC" ? vars : cmds)[t[1]] = t[2];
        }
        return (vars, cmds);
    }

    internal static string ReadResource(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing embedded resource {name}");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    [GeneratedRegex(@"\.\d+\.")]
    private static partial Regex IndexPattern();
}
