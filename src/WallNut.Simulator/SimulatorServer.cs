using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using WallNut.Nut;

namespace WallNut.Simulator;

/// <summary>A user in the simulated upsd.users file.</summary>
public sealed record SimUser(
    string Name,
    string Password,
    bool CanSet = false,
    bool CanInstcmd = false,
    /// <summary>"primary", "secondary" or null (not an upsmon user, so LOGIN is refused).</summary>
    string? UpsmonRole = null);

public sealed class SimulatorOptions
{
    public IPAddress Address { get; init; } = IPAddress.Loopback;
    /// <summary>0 picks a free port (handy for tests).</summary>
    public int Port { get; init; } = NutProtocol.DefaultPort;
    public string ServerVersion { get; init; } = "2.8.2";
    /// <summary>When false, every read also needs a valid username and password.</summary>
    public bool AllowAnonymousRead { get; init; } = true;
    public List<SimUser> Users { get; init; } =
    [
        new("admin", "admin", CanSet: true, CanInstcmd: true, UpsmonRole: "primary"),
        new("monuser", "secret", UpsmonRole: "secondary"), // Synology's built-in account
    ];
}

/// <summary>
/// A fake upsd: speaks the NUT network protocol (v1.3) for a set of <see cref="SimulatedUps"/>.
/// Can also misbehave on purpose (deny, go silent, drop clients, stop listening) to test clients.
/// </summary>
public sealed class SimulatorServer : IAsyncDisposable
{
    private readonly Dictionary<string, SimulatedUps> _ups;
    private readonly SimulatorOptions _options;
    private readonly ConcurrentDictionary<int, Session> _sessions = new();
    private readonly ConcurrentDictionary<string, string> _tracking = new();
    private readonly CancellationTokenSource _stop = new();
    private TcpListener? _listener;
    private Task? _acceptLoop, _clockLoop;
    private int _nextSessionId;
    private int _port;

    public IReadOnlyCollection<SimulatedUps> Ups => _ups.Values;
    public int Port => _port;
    public bool IsListening => _listener is not null;
    public int ClientCount => _sessions.Count;

    /// <summary>Answer every command with ERR ACCESS-DENIED (like a NAS that doesn't permit this client).</summary>
    public bool DenyAll { get; set; }

    /// <summary>Read commands but never reply (a hung server).</summary>
    public bool Silent { get; set; }

    /// <summary>How many simulated seconds pass per real second while the clock runs.</summary>
    public double Speed { get; set; } = 1;

    /// <summary>Raised for each command received (client address, command). Passwords are masked.</summary>
    public event Action<string, string>? CommandReceived;

    public SimulatorServer(IEnumerable<SimulatedUps> ups, SimulatorOptions? options = null)
    {
        _ups = ups.ToDictionary(u => u.Name, StringComparer.Ordinal);
        _options = options ?? new SimulatorOptions();
        _port = _options.Port;
    }

    public SimulatedUps this[string name] => _ups[name];

    // ---- Lifecycle ----------------------------------------------------------------------

    /// <summary>Starts accepting connections. Keeps the same port across stop/start.</summary>
    public void StartListening()
    {
        if (_listener is not null) return;
        var listener = new TcpListener(_options.Address, _port);
        listener.Start();
        _port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _listener = listener;
        _acceptLoop = Task.Run(() => AcceptLoop(listener));
    }

    /// <summary>Stops accepting new connections (clients get "connection refused"). Existing ones stay open.</summary>
    public void StopListening()
    {
        var l = Interlocked.Exchange(ref _listener, null);
        l?.Stop();
        l?.Dispose();
    }

    /// <summary>Runs simulated time in the background at <see cref="Speed"/>.</summary>
    public void StartClock()
    {
        _clockLoop ??= Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
            var last = DateTime.UtcNow;
            try
            {
                while (await timer.WaitForNextTickAsync(_stop.Token))
                {
                    var now = DateTime.UtcNow;
                    var elapsed = (now - last) * Speed;
                    last = now;
                    foreach (var u in _ups.Values) u.Advance(elapsed);
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    /// <summary>Disconnects every client immediately.</summary>
    public void DropAllClients()
    {
        foreach (var s in _sessions.Values) s.Close();
    }

    public IReadOnlyList<(string Address, string? LoggedInTo, string? User)> Clients() =>
        _sessions.Values.Select(s => (s.Address, s.LoginUps, s.Username)).ToList();

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        StopListening();
        DropAllClients();
        foreach (var t in new[] { _acceptLoop, _clockLoop })
        {
            if (t is null) continue;
            try { await t; } catch { /* shutting down */ }
        }
    }

    private async Task AcceptLoop(TcpListener listener)
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(_stop.Token); }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException) { return; }

            var session = new Session(Interlocked.Increment(ref _nextSessionId), client);
            _sessions[session.Id] = session;
            _ = Task.Run(async () =>
            {
                try { await Serve(session); }
                finally { _sessions.TryRemove(session.Id, out _); session.Close(); }
            });
        }
    }

    private async Task Serve(Session s)
    {
        var stream = s.Client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
        try
        {
            while (!_stop.IsCancellationRequested && await reader.ReadLineAsync(_stop.Token) is { } line)
            {
                line = line.TrimEnd('\r');
                if (line.Length == 0) continue;
                CommandReceived?.Invoke(s.Address, line.StartsWith("PASSWORD", StringComparison.Ordinal) ? "PASSWORD ****" : line);
                if (Silent) continue;

                var reply = DenyAll ? ["ERR ACCESS-DENIED"] : Handle(s, line);
                foreach (var r in reply) await writer.WriteLineAsync(r);
                if (s.LoggedOut) return;
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or SocketException) { }
    }

    // ---- Protocol -----------------------------------------------------------------------

    private static string[] Err(string code) => [$"ERR {code}"];

    private IReadOnlyList<string> Handle(Session s, string line)
    {
        IReadOnlyList<string> t;
        try { t = NutProtocol.Tokenize(line); }
        catch (FormatException) { return Err("INVALID-ARGUMENT"); }
        if (t.Count == 0) return Err("UNKNOWN-COMMAND");

        var cmd = t[0].ToUpperInvariant();
        var args = t.Skip(1).ToList();
        return cmd switch
        {
            "VER" => [$"Network UPS Tools upsd {_options.ServerVersion} - https://networkupstools.org/"],
            "NETVER" or "PROTVER" => ["1.3"],
            "HELP" => ["Commands: HELP VER GET LIST SET INSTCMD LOGIN LOGOUT USERNAME PASSWORD STARTTLS"],
            "STARTTLS" => Err("FEATURE-NOT-CONFIGURED"),
            "USERNAME" => SetOnce(ref s.Username, args, "USERNAME"),
            "PASSWORD" => SetOnce(ref s.Password, args, "PASSWORD"),
            "LOGIN" => Login(s, args),
            "LOGOUT" => Logout(s),
            "PRIMARY" or "MASTER" => Primary(s, args, cmd),
            "FSD" => Fsd(s, args),
            "GET" => CanRead(s) ? Get(s, args) : Err("ACCESS-DENIED"),
            "LIST" => CanRead(s) ? List(args) : Err("ACCESS-DENIED"),
            "SET" => Set(s, args),
            "INSTCMD" => Instcmd(s, args),
            _ => Err("UNKNOWN-COMMAND"),
        };
    }

    private static IReadOnlyList<string> SetOnce(ref string? field, List<string> args, string what)
    {
        if (args.Count != 1) return Err("INVALID-ARGUMENT");
        if (field is not null) return Err($"ALREADY-SET-{what}");
        field = args[0];
        return ["OK"];
    }

    private SimUser? Authenticated(Session s) =>
        s.Username is null || s.Password is null ? null
        : _options.Users.FirstOrDefault(u => u.Name == s.Username && u.Password == s.Password);

    private bool CanRead(Session s) => _options.AllowAnonymousRead || Authenticated(s) is not null;

    private IReadOnlyList<string> Login(Session s, List<string> args)
    {
        if (args.Count != 1) return Err("INVALID-ARGUMENT");
        if (s.Username is null) return Err("USERNAME-REQUIRED");
        if (s.Password is null) return Err("PASSWORD-REQUIRED");
        if (s.LoginUps is not null) return Err("ALREADY-LOGGED-IN");
        if (!_ups.ContainsKey(args[0])) return Err("UNKNOWN-UPS");
        if (Authenticated(s) is not { UpsmonRole: not null }) return Err("ACCESS-DENIED");
        s.LoginUps = args[0];
        return ["OK"];
    }

    private static IReadOnlyList<string> Logout(Session s)
    {
        s.LoggedOut = true;
        return ["OK Goodbye"];
    }

    private IReadOnlyList<string> Primary(Session s, List<string> args, string cmd)
    {
        if (args.Count != 1) return Err("INVALID-ARGUMENT");
        if (!_ups.ContainsKey(args[0])) return Err("UNKNOWN-UPS");
        if (Authenticated(s) is not { UpsmonRole: "primary" }) return Err("ACCESS-DENIED");
        s.IsPrimary = true;
        return [cmd == "MASTER" ? "OK MASTER-GRANTED" : "OK PRIMARY-GRANTED"];
    }

    private IReadOnlyList<string> Fsd(Session s, List<string> args)
    {
        if (args.Count != 1) return Err("INVALID-ARGUMENT");
        if (!_ups.TryGetValue(args[0], out var ups)) return Err("UNKNOWN-UPS");
        if (Authenticated(s) is not { UpsmonRole: "primary" }) return Err("ACCESS-DENIED");
        ups.SetForcedShutdown();
        return ["OK FSD-SET"];
    }

    private IReadOnlyList<string> Get(Session s, List<string> args)
    {
        if (args.Count == 0) return Err("INVALID-ARGUMENT");
        var sub = args[0].ToUpperInvariant();

        if (sub == "TRACKING")
        {
            if (args.Count == 1) return [s.Tracking ? "ON" : "OFF"];
            return _tracking.TryGetValue(args[1], out var result) ? [result] : Err("UNKNOWN");
        }

        if (args.Count < 2) return Err("INVALID-ARGUMENT");
        if (!_ups.TryGetValue(args[1], out var ups)) return Err("UNKNOWN-UPS");
        var name = ups.Name;

        switch (sub)
        {
            case "NUMLOGINS":
                return [$"NUMLOGINS {name} {_sessions.Values.Count(x => x.LoginUps == name)}"];
            case "UPSDESC":
                return [$"UPSDESC {name} {NutProtocol.Quote(ups.Description)}"];
        }

        if (args.Count != 3) return Err("INVALID-ARGUMENT");
        var item = args[2];
        switch (sub)
        {
            case "VAR":
                if (ups.IsStale) return Err("DATA-STALE");
                return ups.Get(item) is { } v ? [$"VAR {name} {item} {NutProtocol.Quote(v)}"] : Err("VAR-NOT-SUPPORTED");
            case "TYPE":
                return ups.Get(item) is { } tv ? [$"TYPE {name} {item} {TypeOf(ups, item, tv)}"] : Err("VAR-NOT-SUPPORTED");
            case "DESC":
                return ups.Get(item) is not null
                    ? [$"DESC {name} {item} {NutProtocol.Quote(NutReference.DescribeVariable(item))}"]
                    : Err("VAR-NOT-SUPPORTED");
            case "CMDDESC":
                return ups.Commands.Contains(item)
                    ? [$"CMDDESC {name} {item} {NutProtocol.Quote(NutReference.DescribeCommand(item))}"]
                    : Err("CMD-NOT-SUPPORTED");
            default:
                return Err("INVALID-ARGUMENT");
        }
    }

    private static string TypeOf(SimulatedUps ups, string name, string value)
    {
        if (ups.Profile.Rw.TryGetValue(name, out var spec))
        {
            return spec.Type switch
            {
                "ENUM" => "RW ENUM",
                "RANGE" => "RW RANGE",
                "NUMBER" => "RW NUMBER",
                _ => $"RW STRING:{spec.MaxLength}",
            };
        }
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
            ? "NUMBER" : $"STRING:{Math.Max(32, value.Length)}";
    }

    private IReadOnlyList<string> List(List<string> args)
    {
        if (args.Count == 0) return Err("INVALID-ARGUMENT");
        var sub = args[0].ToUpperInvariant();
        var query = string.Join(' ', args.Select((a, i) => i == 0 ? sub : a));

        if (sub == "UPS")
        {
            if (args.Count != 1) return Err("INVALID-ARGUMENT");
            return Wrap(query, _ups.Values.Select(u => $"UPS {u.Name} {NutProtocol.Quote(u.Description)}"));
        }

        if (args.Count < 2) return Err("INVALID-ARGUMENT");
        if (!_ups.TryGetValue(args[1], out var ups)) return Err("UNKNOWN-UPS");
        var n = ups.Name;

        switch (sub)
        {
            case "VAR" when args.Count == 2:
                if (ups.IsStale) return Err("DATA-STALE");
                return Wrap(query, ups.Snapshot().Select(kv => $"VAR {n} {kv.Key} {NutProtocol.Quote(kv.Value)}"));
            case "RW" when args.Count == 2:
                return Wrap(query, ups.Snapshot().Where(kv => ups.IsWritable(kv.Key))
                    .Select(kv => $"RW {n} {kv.Key} {NutProtocol.Quote(kv.Value)}"));
            case "CMD" when args.Count == 2:
                return Wrap(query, ups.Commands.Order(StringComparer.Ordinal).Select(c => $"CMD {n} {c}"));
            case "CLIENT" when args.Count == 2:
                return Wrap(query, _sessions.Values.Where(x => x.LoginUps == n).Select(x => $"CLIENT {n} {x.Address}"));
            case "ENUM" when args.Count == 3:
            case "RANGE" when args.Count == 3:
                var v = args[2];
                if (ups.Get(v) is null) return Err("VAR-NOT-SUPPORTED");
                ups.Profile.Rw.TryGetValue(v, out var spec);
                return sub == "ENUM"
                    ? Wrap(query, (spec?.Type == "ENUM" ? spec.Values : []).Select(e => $"ENUM {n} {v} {NutProtocol.Quote(e)}"))
                    : Wrap(query, (spec?.Type == "RANGE" ? spec.Ranges : []).Select(r =>
                        $"RANGE {n} {v} {NutProtocol.Quote(F(r[0]))} {NutProtocol.Quote(F(r[1]))}"));
            default:
                return Err("INVALID-ARGUMENT");
        }

        static string F(double d) => d.ToString(CultureInfo.InvariantCulture);
    }

    private static List<string> Wrap(string query, IEnumerable<string> lines) =>
        [$"BEGIN LIST {query}", .. lines, $"END LIST {query}"];

    private IReadOnlyList<string> Set(Session s, List<string> args)
    {
        if (args.Count == 2 && args[0].Equals("TRACKING", StringComparison.OrdinalIgnoreCase))
        {
            var mode = args[1].ToUpperInvariant();
            if (mode is not ("ON" or "OFF")) return Err("INVALID-ARGUMENT");
            s.Tracking = mode == "ON";
            return ["OK"];
        }
        if (args.Count != 4 || !args[0].Equals("VAR", StringComparison.OrdinalIgnoreCase)) return Err("INVALID-ARGUMENT");
        if (!_ups.TryGetValue(args[1], out var ups)) return Err("UNKNOWN-UPS");
        if (Authenticated(s) is not { CanSet: true }) return Err("ACCESS-DENIED");

        return ups.Set(args[2], args[3]) switch
        {
            SimResult.Ok => Track(s),
            SimResult.UnknownVariable => Err("VAR-NOT-SUPPORTED"),
            SimResult.Readonly => Err("READONLY"),
            _ => Err("INVALID-VALUE"),
        };
    }

    private IReadOnlyList<string> Instcmd(Session s, List<string> args)
    {
        if (args.Count is < 2 or > 3) return Err("INVALID-ARGUMENT");
        if (!_ups.TryGetValue(args[0], out var ups)) return Err("UNKNOWN-UPS");
        if (Authenticated(s) is not { CanInstcmd: true }) return Err("ACCESS-DENIED");

        return ups.RunCommand(args[1], args.Count == 3 ? args[2] : null) switch
        {
            SimResult.Ok => Track(s),
            SimResult.UnknownCommand => Err("CMD-NOT-SUPPORTED"),
            _ => Err("INVALID-ARGUMENT"),
        };
    }

    private IReadOnlyList<string> Track(Session s)
    {
        if (!s.Tracking) return ["OK"];
        var id = Guid.NewGuid().ToString();
        _tracking[id] = "SUCCESS"; // the simulator applies changes immediately
        return [$"OK TRACKING {id}"];
    }

    private sealed class Session(int id, TcpClient client)
    {
        public int Id { get; } = id;
        public TcpClient Client { get; } = client;
        public string Address { get; } = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "unknown";
        public string? Username;
        public string? Password;
        public string? LoginUps { get; set; }
        public bool IsPrimary { get; set; }
        public bool Tracking { get; set; }
        public bool LoggedOut { get; set; }

        public void Close()
        {
            try { Client.Client.Shutdown(SocketShutdown.Both); } catch { }
            Client.Dispose();
        }
    }
}
