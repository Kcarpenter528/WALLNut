using System.Net.Sockets;
using System.Text;
using WallNut.Nut;

namespace WallNut.Simulator.Tests;

/// <summary>The fake upsd over a real TCP connection: protocol replies, permissions and misbehavior.</summary>
public sealed class ServerTests : IAsyncLifetime
{
    private SimulatorServer _server = null!;

    public ValueTask InitializeAsync()
    {
        _server = new SimulatorServer(
        [
            new SimulatedUps("rack", UpsProfile.Load("full-featured")) { Jitter = false },
            new SimulatedUps("office", UpsProfile.Load("back-ups")) { Jitter = false },
        ], new SimulatorOptions { Port = 0 });
        _server.StartListening();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => _server.DisposeAsync();

    private Task<NutClient> Connect(TimeSpan? timeout = null) =>
        NutClient.ConnectAsync("127.0.0.1", _server.Port, timeout ?? TimeSpan.FromSeconds(3));

    /// <summary>Sends raw protocol lines; returns all reply lines (a LIST reply as several lines).</summary>
    private sealed class Raw : IDisposable
    {
        private readonly TcpClient _tcp;
        private readonly StreamReader _r;
        private readonly StreamWriter _w;

        public Raw(int port)
        {
            _tcp = new TcpClient("127.0.0.1", port) { ReceiveTimeout = 3000 };
            var s = _tcp.GetStream();
            _r = new StreamReader(s, Encoding.UTF8);
            _w = new StreamWriter(s, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
        }

        public List<string> Send(string line)
        {
            _w.WriteLine(line);
            var lines = new List<string>();
            while (true)
            {
                var l = _r.ReadLine() ?? throw new IOException("Server closed the connection");
                lines.Add(l);
                if (!l.StartsWith("BEGIN LIST", StringComparison.Ordinal) && (lines.Count == 1 || l.StartsWith("END LIST", StringComparison.Ordinal)))
                    return lines;
            }
        }

        public string One(string line) => Send(line).Single();

        public void Login(string user, string pass)
        {
            Assert.Equal("OK", One($"USERNAME {user}"));
            Assert.Equal("OK", One($"PASSWORD {pass}"));
        }

        public void Dispose() => _tcp.Dispose();
    }

    // ---- Reading, through the real WALLNut client --------------------------------------

    [Fact]
    public async Task Client_lists_both_ups_and_all_variables()
    {
        await using var c = await Connect();
        var ups = await c.ListUpsAsync();
        Assert.Equal(["office", "rack"], ups.Select(u => u.Name).Order());

        var rack = await c.ListVarsAsync("rack");
        Assert.True(rack.Count > 300, $"only {rack.Count} variables");
        Assert.Equal("WNL3K25031400042", rack["ups.serial"]);
        Assert.Equal("WALLNut Labs", rack["device.mfr"]);

        var office = await c.ListVarsAsync("office");
        Assert.Equal("Back-UPS ES 600M1", office["ups.model"]);
        Assert.True(office.Count < 60);
    }

    [Fact]
    public async Task Client_sees_an_outage()
    {
        await using var c = await Connect();
        Assert.False((await c.GetStatusAsync("office")).IsOnBattery);

        _server["office"].PowerFail();
        var status = await c.GetStatusAsync("office");
        Assert.True(status.IsOnBattery);
        Assert.False(status.IsCritical);

        _server["office"].SetCharge(5);
        Assert.True((await c.GetStatusAsync("office")).IsCritical);
    }

    // ---- Protocol details --------------------------------------------------------------

    [Fact]
    public void Version_and_help()
    {
        using var r = new Raw(_server.Port);
        Assert.StartsWith("Network UPS Tools upsd 2.8.2", r.One("VER"));
        Assert.Equal("1.3", r.One("NETVER"));
        Assert.Equal("1.3", r.One("PROTVER"));
        Assert.StartsWith("Commands:", r.One("HELP"));
        Assert.Equal("ERR FEATURE-NOT-CONFIGURED", r.One("STARTTLS"));
        Assert.Equal("ERR UNKNOWN-COMMAND", r.One("FROB"));
    }

    [Fact]
    public void Get_variants()
    {
        using var r = new Raw(_server.Port);
        Assert.Equal("UPSDESC rack \"WALLNut Labs FX-3000RT (full-featured, simulated)\"", r.One("GET UPSDESC rack"));
        Assert.Equal("DESC rack battery.runtime \"Battery runtime (seconds)\"", r.One("GET DESC rack battery.runtime"));
        Assert.StartsWith("CMDDESC rack load.off ", r.One("GET CMDDESC rack load.off"));
        Assert.Equal("TYPE rack input.sensitivity RW ENUM", r.One("GET TYPE rack input.sensitivity"));
        Assert.Equal("TYPE rack ups.id RW STRING:16", r.One("GET TYPE rack ups.id"));
        Assert.Equal("TYPE rack battery.charge.low RW RANGE", r.One("GET TYPE rack battery.charge.low"));
        Assert.Equal("TYPE rack battery.charge NUMBER", r.One("GET TYPE rack battery.charge"));
        Assert.Equal("NUMLOGINS rack 0", r.One("GET NUMLOGINS rack"));
    }

    [Fact]
    public void Get_errors()
    {
        using var r = new Raw(_server.Port);
        Assert.Equal("ERR UNKNOWN-UPS", r.One("GET VAR nope ups.status"));
        Assert.Equal("ERR VAR-NOT-SUPPORTED", r.One("GET VAR office ambient.temperature"));
        Assert.Equal("ERR CMD-NOT-SUPPORTED", r.One("GET CMDDESC office calibrate.start"));
        Assert.Equal("ERR INVALID-ARGUMENT", r.One("GET VAR rack"));
    }

    [Fact]
    public void List_variants()
    {
        using var r = new Raw(_server.Port);
        Assert.Equal(["BEGIN LIST ENUM rack input.sensitivity", "ENUM rack input.sensitivity \"low\"",
            "ENUM rack input.sensitivity \"medium\"", "ENUM rack input.sensitivity \"high\"", "END LIST ENUM rack input.sensitivity"],
            r.Send("LIST ENUM rack input.sensitivity"));
        Assert.Equal(["BEGIN LIST RANGE rack battery.charge.low", "RANGE rack battery.charge.low \"5\" \"90\"",
            "END LIST RANGE rack battery.charge.low"], r.Send("LIST RANGE rack battery.charge.low"));

        var cmds = r.Send("LIST CMD office");
        Assert.Contains("CMD office test.battery.start.quick", cmds);
        Assert.Equal(14 + 2, cmds.Count);

        var rw = r.Send("LIST RW office");
        Assert.Contains("RW office input.sensitivity \"medium\"", rw);
    }

    [Fact]
    public void Quoted_values_with_spaces_round_trip()
    {
        using var r = new Raw(_server.Port);
        r.Login("admin", "admin");
        Assert.Equal("OK", r.One("SET VAR rack device.location \"Rack 4, \\\"top\\\" shelf\""));
        Assert.Equal("VAR rack device.location \"Rack 4, \\\"top\\\" shelf\"", r.One("GET VAR rack device.location"));
        Assert.Equal("Rack 4, \"top\" shelf", _server["rack"].Get("device.location"));
    }

    // ---- Permissions -------------------------------------------------------------------

    [Fact]
    public void Writes_need_a_user_with_rights()
    {
        using var anon = new Raw(_server.Port);
        Assert.Equal("ERR ACCESS-DENIED", anon.One("SET VAR rack ups.id \"x\""));
        Assert.Equal("ERR ACCESS-DENIED", anon.One("INSTCMD rack beeper.mute"));

        using var mon = new Raw(_server.Port);
        mon.Login("monuser", "secret");
        Assert.Equal("ERR ACCESS-DENIED", mon.One("SET VAR rack ups.id \"x\""));

        using var bad = new Raw(_server.Port);
        bad.Login("admin", "wrong");
        Assert.Equal("ERR ACCESS-DENIED", bad.One("INSTCMD rack beeper.mute"));

        using var admin = new Raw(_server.Port);
        admin.Login("admin", "admin");
        Assert.Equal("OK", admin.One("SET VAR rack ups.id \"lab-1\""));
        Assert.Equal("ERR READONLY", admin.One("SET VAR rack ups.status \"OB\""));
        Assert.Equal("ERR INVALID-VALUE", admin.One("SET VAR rack input.sensitivity \"max\""));
        Assert.Equal("OK", admin.One("INSTCMD rack beeper.mute"));
        Assert.Equal("ERR CMD-NOT-SUPPORTED", admin.One("INSTCMD office calibrate.start"));
        Assert.Equal("muted", _server["rack"].Get("ups.beeper.status"));
    }

    [Fact]
    public void Username_and_password_can_only_be_set_once()
    {
        using var r = new Raw(_server.Port);
        r.Login("monuser", "secret");
        Assert.Equal("ERR ALREADY-SET-USERNAME", r.One("USERNAME admin"));
        Assert.Equal("ERR ALREADY-SET-PASSWORD", r.One("PASSWORD admin"));
    }

    [Fact]
    public async Task Login_registers_a_secondary_and_shows_in_numlogins()
    {
        using var r = new Raw(_server.Port);
        Assert.Equal("ERR USERNAME-REQUIRED", r.One("LOGIN rack"));
        r.Login("monuser", "secret");
        Assert.Equal("OK", r.One("LOGIN rack"));
        Assert.Equal("ERR ALREADY-LOGGED-IN", r.One("LOGIN rack"));

        await using var c = await Connect();
        Assert.Equal(1, _server.Clients().Count(x => x.LoggedInTo == "rack"));
        using var other = new Raw(_server.Port);
        Assert.Equal("NUMLOGINS rack 1", other.One("GET NUMLOGINS rack"));
        Assert.Equal(["BEGIN LIST CLIENT rack", "CLIENT rack 127.0.0.1", "END LIST CLIENT rack"], other.Send("LIST CLIENT rack"));
    }

    [Fact]
    public void Primary_and_fsd_need_a_primary_user()
    {
        using var sec = new Raw(_server.Port);
        sec.Login("monuser", "secret");
        Assert.Equal("ERR ACCESS-DENIED", sec.One("PRIMARY rack"));
        Assert.Equal("ERR ACCESS-DENIED", sec.One("FSD rack"));

        using var pri = new Raw(_server.Port);
        pri.Login("admin", "admin");
        Assert.Equal("OK PRIMARY-GRANTED", pri.One("PRIMARY rack"));
        Assert.Equal("OK FSD-SET", pri.One("FSD rack"));
        Assert.Contains("FSD", _server["rack"].Get("ups.status"));
    }

    [Fact]
    public void Tracking_returns_ids_that_report_success()
    {
        using var r = new Raw(_server.Port);
        r.Login("admin", "admin");
        Assert.Equal("OFF", r.One("GET TRACKING"));
        Assert.Equal("OK", r.One("SET TRACKING ON"));
        var reply = r.One("INSTCMD rack beeper.enable");
        Assert.StartsWith("OK TRACKING ", reply);
        Assert.Equal("SUCCESS", r.One($"GET TRACKING {reply["OK TRACKING ".Length..]}"));
    }

    [Fact]
    public async Task Anonymous_reads_can_be_turned_off()
    {
        // A server where every read needs a login.
        await using var strict = new SimulatorServer([new SimulatedUps("ups", UpsProfile.Load("back-ups"))],
            new SimulatorOptions { Port = 0, AllowAnonymousRead = false });
        strict.StartListening();

        using var anon = new Raw(strict.Port);
        Assert.Equal("ERR ACCESS-DENIED", anon.One("GET VAR ups ups.status"));
        using var mon = new Raw(strict.Port);
        mon.Login("monuser", "secret");
        Assert.StartsWith("VAR ups ups.status", mon.One("GET VAR ups ups.status"));
    }

    // ---- Misbehavior -------------------------------------------------------------------

    [Fact]
    public async Task Stale_data_is_reported()
    {
        await using var c = await Connect();
        _server["office"].SetStale(true);
        var ex = await Assert.ThrowsAsync<NutException>(() => c.GetVarAsync("office", "ups.status"));
        Assert.Equal(NutErrorCode.DataStale, ex.Code);
        _server["office"].SetStale(false);
        Assert.NotNull(await c.GetVarAsync("office", "ups.status"));
    }

    [Fact]
    public async Task Deny_all_looks_like_a_nas_that_does_not_permit_us()
    {
        _server.DenyAll = true;
        await using var c = await Connect();
        var ex = await Assert.ThrowsAsync<NutException>(() => c.ListUpsAsync());
        Assert.Equal(NutErrorCode.AccessDenied, ex.Code);
    }

    [Fact]
    public async Task Silent_server_times_out()
    {
        _server.Silent = true;
        await using var c = await Connect(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAsync<TimeoutException>(() => c.GetVarAsync("rack", "ups.status"));
        Assert.False(c.IsConnected);
    }

    [Fact]
    public async Task Dropped_clients_see_the_connection_close()
    {
        await using var c = await Connect();
        await c.GetVarAsync("rack", "ups.status");
        await WaitFor(() => _server.ClientCount == 1);

        _server.DropAllClients();
        await Assert.ThrowsAnyAsync<Exception>(() => c.GetVarAsync("rack", "ups.status"));
        Assert.False(c.IsConnected);
    }

    [Fact]
    public async Task Stop_listening_refuses_then_start_accepts_on_the_same_port()
    {
        var port = _server.Port;
        _server.StopListening();
        // Windows retries a refused connection for ~2 s before failing, so allow longer than that.
        await Assert.ThrowsAsync<SocketException>(() => NutClient.ConnectAsync("127.0.0.1", port, TimeSpan.FromSeconds(10)));

        _server.StartListening();
        Assert.Equal(port, _server.Port);
        await using var c = await Connect();
        Assert.NotEmpty(await c.ListUpsAsync());
    }

    [Fact]
    public async Task Logout_says_goodbye_and_closes()
    {
        await using var c = await Connect();
        await c.LogoutAsync();
        Assert.False(c.IsConnected);
        await WaitFor(() => _server.ClientCount == 0);
    }

    [Fact]
    public async Task Clock_moves_simulated_time_at_the_set_speed()
    {
        _server.Speed = 600; // 10 simulated minutes per real second
        _server["office"].PowerFail();
        _server.StartClock();
        await WaitFor(() => _server["office"].Charge < 95);
    }

    private static async Task WaitFor(Func<bool> condition, int timeoutMs = 3000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > until) Assert.Fail("Timed out waiting for condition");
            await Task.Delay(20);
        }
    }
}
