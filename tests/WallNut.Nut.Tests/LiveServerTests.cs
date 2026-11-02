namespace WallNut.Nut.Tests;

/// <summary>
/// Talks to a real NUT server. Skipped unless WALLNUT_LIVE_HOST is set, e.g.
///   $env:WALLNUT_LIVE_HOST = "192.168.1.10"; dotnet test
/// Read-only: lists UPS devices and reads variables, never sends commands.
/// </summary>
public class LiveServerTests
{
    private static readonly string? Host = Environment.GetEnvironmentVariable("WALLNUT_LIVE_HOST");

    [Fact]
    public async Task Reads_status_from_a_real_server()
    {
        if (string.IsNullOrWhiteSpace(Host))
        {
            Assert.Skip("Set WALLNUT_LIVE_HOST to run against a real NUT server.");
            return;
        }

        await using var client = await NutClient.ConnectAsync(Host);
        var ups = Assert.Single(await client.ListUpsAsync());
        var vars = await client.ListVarsAsync(ups.Name);
        var status = await client.GetStatusAsync(ups.Name);

        Assert.Contains("ups.status", vars.Keys);
        Assert.NotEqual(UpsStatusFlags.None, status.Flags);
        TestContext.Current.SendDiagnosticMessage(
            $"{ups.Name}: status={status} charge={vars.GetValueOrDefault("battery.charge")}% vars={vars.Count}");
    }
}
