using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WallNut.Service.Configuration;
using WallNut.Simulator;

namespace WallNut.Service.Tests;

/// <summary>Machine config file layering and the installer's "--check" command.</summary>
public sealed class InstallConfigTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"wallnut-config-{Guid.NewGuid():N}");
    private readonly List<WebApplication> _apps = [];
    private SimulatorServer _sim = null!;

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _sim = new SimulatorServer([new SimulatedUps("ups", UpsProfile.Load("back-ups"))], new SimulatorOptions { Port = 0 });
        _sim.StartListening();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var a in _apps) await a.DisposeAsync();
        await _sim.DisposeAsync();
        Directory.Delete(_dir, true);
    }

    private string Config(string json)
    {
        var path = Path.Combine(_dir, $"config-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    private WebApplication Build(string config, params string[] args)
    {
        var endpoint = OperatingSystem.IsWindows() ? $"WallNut.Test.{Guid.NewGuid():N}" : Path.Combine(_dir, $"{Guid.NewGuid():N}.sock");
        var app = ServiceHost.Build(["--environment", "Testing", $"--WallNut:Api:Endpoint={endpoint}", .. args], config);
        _apps.Add(app);
        return app;
    }

    private static WallNutOptions Options(WebApplication app) => app.Services.GetRequiredService<IOptions<WallNutOptions>>().Value;

    [Fact]
    public void Machine_file_overrides_defaults_and_arguments_override_it()
    {
        var path = Config("""
            {
              // comments are allowed, like in the example file
              "WallNut": {
                "Endpoints": { "nas": { "Host": "nas.example", "Ups": "ups", "Role": "PowersThisMachine", "Username": "monuser", "Password": "secret" } },
                "Shutdown": { "DryRun": false, "FinalDelaySeconds": 9 }
              }
            }
            """);
        var o = Options(Build(path, "--WallNut:Shutdown:FinalDelaySeconds=3"));

        Assert.Equal("nas.example", o.Endpoints["nas"].Host);
        Assert.Equal(UpsRole.PowersThisMachine, o.Endpoints["nas"].Role);
        Assert.False(o.Shutdown.DryRun);                  // from the machine file
        Assert.Equal(3, o.Shutdown.FinalDelaySeconds);    // argument wins
        Assert.Equal(15, o.Shutdown.LostWhileOnBatterySeconds); // built-in default kept
    }

    [Fact]
    public void Missing_machine_file_is_fine()
    {
        var o = Options(Build(Path.Combine(_dir, "nope.json")));
        Assert.Empty(o.Endpoints);
        Assert.True(o.Shutdown.DryRun);
    }

    [Fact]
    public void Repo_defaults_contain_no_personal_endpoints()
    {
        var o = Options(Build("none"));
        Assert.Empty(o.Endpoints);
    }

    [Fact]
    public void Example_config_is_valid_and_safe()
    {
        var example = Path.Combine(FindRepoRoot(), "packaging", "config.example.json");
        var o = Options(Build(example));
        Assert.Empty(o.Validate());
        Assert.True(o.Shutdown.DryRun, "The example must ship in test mode.");
    }

    [Fact]
    public async Task Check_reports_a_reachable_ups()
    {
        var path = Config($$"""
            { "WallNut": { "Endpoints": { "sim": { "Host": "127.0.0.1", "Port": {{_sim.Port}}, "Ups": "ups",
                "Role": "PowersThisMachine", "Username": "monuser", "Password": "secret" } } } }
            """);
        var output = new StringWriter();
        var code = await ConfigCheck.RunAsync(Build(path), output);

        Assert.Equal(0, code);
        Assert.Contains("OK - Back-UPS ES 600M1", output.ToString());
        Assert.Contains("login as a secondary (upsmon) works", output.ToString());
        Assert.Contains("TEST MODE", output.ToString());
    }

    [Fact]
    public async Task Check_explains_an_unreachable_ups()
    {
        _sim.DenyAll = true;
        var path = Config($$"""
            { "WallNut": { "Endpoints": { "sim": { "Host": "127.0.0.1", "Port": {{_sim.Port}}, "Ups": "ups" } } } }
            """);
        var output = new StringWriter();
        Assert.Equal(2, await ConfigCheck.RunAsync(Build(path), output));
        Assert.Contains("Permitted DiskStation Devices", output.ToString());
    }

    [Fact]
    public async Task Check_reports_settings_problems()
    {
        var output = new StringWriter();
        Assert.Equal(1, await ConfigCheck.RunAsync(Build("none"), output));
        Assert.Contains("No UPS endpoints are configured", output.ToString());
    }

    [Fact]
    public async Task Broken_json_is_explained_instead_of_crashing()
    {
        var path = Config("{ \"WallNut\": { \"Endpoints\": ");
        var app = Build(path); // must not throw
        var status = app.Services.GetRequiredService<MachineConfigStatus>();
        Assert.Contains("isn't valid JSON", status.Problem);

        var output = new StringWriter();
        Assert.Equal(1, await ConfigCheck.RunAsync(app, output));
        Assert.Contains("isn't valid JSON", output.ToString());
    }

    [Fact]
    public async Task Unreadable_file_is_explained_instead_of_crashing()
    {
        // The failure from the first Windows install: a settings file nobody may read.
        if (!OperatingSystem.IsWindows()) { Assert.Skip("Uses Windows file permissions."); return; }
        var path = Config("{}");
        var me = "*" + System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        RunIcacls(path, "/deny", $"{me}:(R)");
        try
        {
            var app = Build(path);
            var status = app.Services.GetRequiredService<MachineConfigStatus>();
            Assert.Contains("access was denied", status.Problem);
            Assert.Contains("re-running the installer repairs this", status.Problem);

            var output = new StringWriter();
            Assert.Equal(1, await ConfigCheck.RunAsync(app, output));
            Assert.Contains("PROBLEM: Can't read the settings file", output.ToString());
        }
        finally { RunIcacls(path, "/reset"); }
    }

    private static void RunIcacls(params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("icacls.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }

    internal static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WallNut.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? Environment.GetEnvironmentVariable("WALLNUT_REPO")
            ?? throw new InvalidOperationException("Can't find the repository root (set WALLNUT_REPO).");
    }
}
