namespace WallNut.Nut.Tests;

public class NutClientTests
{
    private static readonly TimeSpan Short = TimeSpan.FromSeconds(2);

    /// <summary>Replies like a Synology NAS with one UPS called "ups".</summary>
    private static IEnumerable<string>? Synology(string cmd) => cmd switch
    {
        "LIST UPS" => ["BEGIN LIST UPS", "UPS ups \"UPS on the NAS\"", "END LIST UPS"],
        "LIST VAR ups" =>
        [
            "BEGIN LIST VAR ups",
            "VAR ups battery.charge \"100\"",
            "VAR ups ups.status \"OL CHRG\"",
            "END LIST VAR ups",
        ],
        "GET VAR ups ups.status" => ["VAR ups ups.status \"OB DISCHRG\""],
        "GET VAR ups battery.charge" => ["VAR ups battery.charge \"87\""],
        "GET VAR UPS ups.status" => ["ERR UNKNOWN-UPS"],
        "USERNAME monuser" => ["OK"],
        "PASSWORD \"secret\"" => ["OK"],
        "LOGIN ups" => ["OK"],
        "LOGOUT" => ["OK Goodbye"],
        _ => ["ERR UNKNOWN-COMMAND"],
    };

    [Fact]
    public async Task Lists_ups_devices()
    {
        await using var server = new FakeNutServer(Synology);
        await using var client = await NutClient.ConnectAsync("127.0.0.1", server.Port, Short);

        var list = await client.ListUpsAsync();

        var ups = Assert.Single(list);
        Assert.Equal("ups", ups.Name);
        Assert.Equal("UPS on the NAS", ups.Description);
    }

    [Fact]
    public async Task Lists_and_gets_variables()
    {
        await using var server = new FakeNutServer(Synology);
        await using var client = await NutClient.ConnectAsync("127.0.0.1", server.Port, Short);

        var vars = await client.ListVarsAsync("ups");
        Assert.Equal("100", vars["battery.charge"]);
        Assert.Equal("OL CHRG", vars["ups.status"]);

        Assert.Equal("87", await client.GetVarAsync("ups", "battery.charge"));
        Assert.True((await client.GetStatusAsync("ups")).IsOnBattery);
    }

    [Fact]
    public async Task Server_errors_become_NutException_and_connection_stays_usable()
    {
        await using var server = new FakeNutServer(Synology);
        await using var client = await NutClient.ConnectAsync("127.0.0.1", server.Port, Short);

        var ex = await Assert.ThrowsAsync<NutException>(() => client.GetVarAsync("UPS", "ups.status"));
        Assert.Equal(NutErrorCode.UnknownUps, ex.Code);

        Assert.True(client.IsConnected);
        Assert.Equal("87", await client.GetVarAsync("ups", "battery.charge"));
    }

    [Fact]
    public async Task Access_denied_is_reported_clearly()
    {
        // What the Synology sends when this PC isn't on its permitted list.
        await using var server = new FakeNutServer(_ => ["ERR ACCESS-DENIED"]);
        await using var client = await NutClient.ConnectAsync("127.0.0.1", server.Port, Short);

        var ex = await Assert.ThrowsAsync<NutException>(() => client.ListUpsAsync());
        Assert.Equal(NutErrorCode.AccessDenied, ex.Code);
    }

    [Fact]
    public async Task Authenticates_and_logs_in_as_a_secondary()
    {
        await using var server = new FakeNutServer(Synology);
        await using var client = await NutClient.ConnectAsync("127.0.0.1", server.Port, Short);

        await client.AuthenticateAsync("monuser", "secret");
        await client.LoginAsync("ups");
        await client.LogoutAsync();

        Assert.Equal(["USERNAME monuser", "PASSWORD \"secret\"", "LOGIN ups", "LOGOUT"], server.Received);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task Server_hanging_up_is_detected()
    {
        await using var server = new FakeNutServer(_ => null);
        await using var client = await NutClient.ConnectAsync("127.0.0.1", server.Port, Short);

        await Assert.ThrowsAsync<IOException>(() => client.GetVarAsync("ups", "ups.status"));
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task Silent_server_times_out_and_connection_is_marked_broken()
    {
        await using var server = new FakeNutServer(_ => []);
        await using var client = await NutClient.ConnectAsync("127.0.0.1", server.Port, TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAsync<TimeoutException>(() => client.GetVarAsync("ups", "ups.status"));
        Assert.False(client.IsConnected);
        await Assert.ThrowsAsync<IOException>(() => client.GetVarAsync("ups", "ups.status"));
    }

    [Fact]
    public async Task Nothing_listening_fails_to_connect()
    {
        int port;
        await using (var server = new FakeNutServer(Synology)) port = server.Port;

        await Assert.ThrowsAnyAsync<Exception>(() => NutClient.ConnectAsync("127.0.0.1", port, Short));
    }

    [Theory]
    [InlineData("ups x")]
    [InlineData("ups\"")]
    [InlineData("ups\nLOGOUT")]
    public async Task Names_that_could_inject_commands_are_rejected(string badName)
    {
        await using var server = new FakeNutServer(Synology);
        await using var client = await NutClient.ConnectAsync("127.0.0.1", server.Port, Short);

        await Assert.ThrowsAsync<ArgumentException>(() => client.GetVarAsync(badName, "ups.status"));
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task Concurrent_callers_get_their_own_replies()
    {
        await using var server = new FakeNutServer(Synology);
        await using var client = await NutClient.ConnectAsync("127.0.0.1", server.Port, Short);

        var tasks = Enumerable.Range(0, 20).Select(i => i % 2 == 0
            ? client.GetVarAsync("ups", "battery.charge")
            : client.GetVarAsync("ups", "ups.status")).ToArray();
        var results = await Task.WhenAll(tasks);

        for (int i = 0; i < results.Length; i++)
            Assert.Equal(i % 2 == 0 ? "87" : "OB DISCHRG", results[i]);
    }
}
