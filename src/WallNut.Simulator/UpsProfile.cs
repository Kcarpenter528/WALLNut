using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WallNut.Simulator;

/// <summary>How a writable variable may be set (what LIST RW / ENUM / RANGE and GET TYPE report).</summary>
public sealed class RwSpec
{
    /// <summary>STRING, NUMBER, ENUM or RANGE.</summary>
    public string Type { get; init; } = "STRING";
    public int MaxLength { get; init; } = 32;
    public List<string> Values { get; init; } = [];
    public List<double[]> Ranges { get; init; } = [];

    public bool Accepts(string value) => Type switch
    {
        "ENUM" => Values.Contains(value),
        "RANGE" => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                   && Ranges.Any(r => v >= r[0] && v <= r[1]),
        "NUMBER" => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _),
        _ => value.Length <= MaxLength,
    };
}

/// <summary>Numbers the battery model uses; everything else comes from the variables.</summary>
public sealed class SimulationSettings
{
    /// <summary>Runtime on a full battery at 100% load. Lighter loads last longer.</summary>
    public double RuntimeAtFullLoadSec { get; init; } = 300;
    /// <summary>Time to recharge from empty to full.</summary>
    public double RechargeSec { get; init; } = 4 * 3600;
    /// <summary>Starting load, percent of capacity.</summary>
    public double LoadPercent { get; init; } = 25;
    public double PowerFactor { get; init; } = 0.9;
    /// <summary>1 or 3. Three-phase profiles also get per-phase readings.</summary>
    public int Phases { get; init; } = 1;
}

/// <summary>Repeats a block of variables/commands for each index, replacing {n} (and {x}).</summary>
public sealed class ProfileTemplate
{
    public List<int> N { get; init; } = [];
    public List<int> X { get; init; } = [];
    public Dictionary<string, string> Vars { get; init; } = [];
    public Dictionary<string, RwSpec> Rw { get; init; } = [];
    public List<string> Commands { get; init; } = [];
}

/// <summary>A simulated UPS model: its variables, writable settings, commands and battery behavior.</summary>
public sealed class UpsProfile
{
    public string Name { get; set; } = "";
    public string Description { get; init; } = "";
    public SimulationSettings Simulation { get; init; } = new();
    public Dictionary<string, string> Vars { get; init; } = [];
    public Dictionary<string, RwSpec> Rw { get; init; } = [];
    public List<string> Commands { get; set; } = [];
    public List<ProfileTemplate> Templates { get; init; } = [];

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>Names of the profiles built into the simulator.</summary>
    public static IReadOnlyList<string> BuiltIn { get; } = Assembly.GetExecutingAssembly()
        .GetManifestResourceNames()
        .Where(n => n.StartsWith("profile.", StringComparison.Ordinal))
        .Select(n => n["profile.".Length..])
        .Order()
        .ToList();

    /// <summary>Loads a built-in profile by name, or a profile JSON file by path.</summary>
    public static UpsProfile Load(string nameOrPath)
    {
        var json = File.Exists(nameOrPath)
            ? File.ReadAllText(nameOrPath)
            : BuiltIn.Contains(nameOrPath)
                ? NutReference.ReadResource("profile." + nameOrPath)
                : throw new ArgumentException(
                    $"Unknown profile '{nameOrPath}'. Built-in profiles: {string.Join(", ", BuiltIn)}");

        var profile = JsonSerializer.Deserialize<UpsProfile>(json, Json)
            ?? throw new FormatException($"Profile '{nameOrPath}' is empty.");
        profile.Name = Path.GetFileNameWithoutExtension(nameOrPath);
        profile.ExpandTemplates();
        return profile;
    }

    private void ExpandTemplates()
    {
        foreach (var t in Templates)
        {
            var xs = t.X.Count > 0 ? t.X : [0];
            foreach (var n in t.N)
            foreach (var x in xs)
            {
                string Sub(string s) => s.Replace("{n}", n.ToString(CultureInfo.InvariantCulture))
                                         .Replace("{x}", x.ToString(CultureInfo.InvariantCulture));
                foreach (var (k, v) in t.Vars) Vars[Sub(k)] = Sub(v);
                foreach (var (k, v) in t.Rw) Rw[Sub(k)] = v;
                foreach (var c in t.Commands) Commands.Add(Sub(c));
            }
        }
        Templates.Clear();
        Commands = Commands.Distinct().ToList();
    }
}
