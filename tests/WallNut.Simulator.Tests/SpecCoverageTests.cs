using System.Text.RegularExpressions;

namespace WallNut.Simulator.Tests;

/// <summary>
/// The full-featured profile must report everything in the NUT spec. These tests read the spec
/// files themselves (cmdvartab and nut-names.txt), so a newer spec shows up as a test failure.
/// </summary>
public partial class SpecCoverageTests
{
    private static readonly UpsProfile Full = UpsProfile.Load("full-featured");

    private static bool Covered(string name) => Full.Vars.ContainsKey(name) || Full.Commands.Contains(name);

    [Fact]
    public void Every_cmdvartab_variable_is_present()
    {
        var missing = NutReference.VariableDescriptions.Keys.Where(v => !Full.Vars.ContainsKey(v)).ToList();
        Assert.True(missing.Count == 0, "Missing variables: " + string.Join(", ", missing));
    }

    [Fact]
    public void Every_cmdvartab_command_is_present()
    {
        var missing = NutReference.CommandDescriptions.Keys.Where(c => !Full.Commands.Contains(c)).ToList();
        Assert.True(missing.Count == 0, "Missing commands: " + string.Join(", ", missing));
    }

    [Fact]
    public void Every_name_in_the_naming_spec_is_present()
    {
        // Single-phase names only; three-phase SPEC fragments (current.maximum, voltage.status, ...)
        // aren't full names and are covered by the three-phase profile instead.
        string[] roots = ["device", "ups", "input", "output", "battery", "ambient", "outlet", "driver", "server", "experimental",
                          "beeper", "load", "shutdown", "test", "calibrate", "bypass", "reset", "clear"];

        var missing = new List<string>();
        foreach (Match m in SpecRow().Matches(NutReference.ReadNamingSpec()))
        {
            var name = m.Groups[1].Value;
            if (!roots.Contains(name.Split('.')[0])) continue;

            if (name.EndsWith(".xxx", StringComparison.Ordinal))
            {
                // e.g. driver.parameter.xxx: any concrete driver.parameter.* will do.
                var prefix = name[..^3];
                if (!Full.Vars.Keys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal))) missing.Add(name);
                continue;
            }

            // outlet.n.current -> outlet.1.current, ambient.n.contacts.x.status -> ambient.1.contacts.1.status
            var concrete = name.Replace(".n.", ".1.").Replace(".x.", ".1.");
            if (!Covered(concrete)) missing.Add(name);
        }
        Assert.True(missing.Count == 0, "Missing from full-featured: " + string.Join(", ", missing.Distinct()));
    }

    [Fact]
    public void Every_variable_and_command_has_a_description()
    {
        var ups = new SimulatedUps("rack", Full);
        Assert.All(ups.Snapshot(), kv => Assert.NotNull(NutReference.DescribeVariable(kv.Key)));
        Assert.Equal("Turn off the load immediately", NutReference.DescribeCommand("load.off"));
        // Numbered items NUT doesn't list individually fall back to item 1's description.
        Assert.Equal(NutReference.DescribeCommand("outlet.1.load.off"), NutReference.DescribeCommand("outlet.7.load.off"));
        Assert.Equal(NutReference.DescribeVariable("outlet.1.status"), NutReference.DescribeVariable("outlet.9.status"));
    }

    [Theory]
    [InlineData("full-featured")]
    [InlineData("back-ups")]
    [InlineData("three-phase")]
    public void Built_in_profiles_load_and_writable_values_are_valid(string name)
    {
        var p = UpsProfile.Load(name);
        Assert.NotEmpty(p.Vars);
        Assert.NotEmpty(p.Commands);
        Assert.All(p.Rw, kv =>
        {
            Assert.True(p.Vars.ContainsKey(kv.Key), $"{kv.Key} is writable but not a variable");
            Assert.True(kv.Value.Accepts(p.Vars[kv.Key]), $"{kv.Key}={p.Vars[kv.Key]} is outside its own limits");
        });
    }

    [Fact]
    public void Built_in_profile_list_matches_files()
    {
        Assert.Equal(["back-ups", "full-featured", "three-phase"], UpsProfile.BuiltIn);
    }

    [Fact]
    public void Three_phase_profile_reports_every_phase()
    {
        var p = UpsProfile.Load("three-phase");
        foreach (var l in new[] { "L1", "L2", "L3" })
        {
            Assert.Contains($"input.{l}-N.voltage", p.Vars.Keys);
            Assert.Contains($"input.{l}.current", p.Vars.Keys);
            Assert.Contains($"output.{l}.power", p.Vars.Keys);
        }
        Assert.Equal("3", p.Vars["input.phases"]);
        Assert.Equal("3", p.Vars["output.phases"]);
    }

    [GeneratedRegex(@"^\|\s*([a-z][a-z0-9]*(?:\.[a-zA-Z0-9_\-]+)+)\s*\|", RegexOptions.Multiline)]
    private static partial Regex SpecRow();
}
