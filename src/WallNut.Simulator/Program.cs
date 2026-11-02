using System.Globalization;
using System.Net;
using WallNut.Nut;
using WallNut.Simulator;

// wallnut-sim: a fake NUT server with simulated UPS devices.
//
//   wallnut-sim                                  rack, office and dc UPSes on 127.0.0.1:3493
//   wallnut-sim --port 3494 --ups lab=full-featured --speed 10
//   wallnut-sim --bind 0.0.0.0                   reachable from other machines on your network

var port = NutProtocol.DefaultPort;
var bind = IPAddress.Loopback;
var speed = 1.0;
var anonymous = true;
var upsArgs = new List<(string Name, string Profile)>();

for (int i = 0; i < args.Length; i++)
{
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
    switch (args[i])
    {
        case "--port" or "-p": port = int.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "--bind" or "-b": bind = IPAddress.Parse(Next()); break;
        case "--speed" or "-s": speed = double.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "--no-anonymous": anonymous = false; break;
        case "--ups" or "-u":
            var spec = Next().Split('=', 2);
            upsArgs.Add(spec.Length == 2 ? (spec[0], spec[1]) : (spec[0], "full-featured"));
            break;
        case "--help" or "-h" or "-?":
            PrintUsage();
            return 0;
        default:
            Console.Error.WriteLine($"Unknown option {args[i]}. Use --help.");
            return 2;
    }
}
if (upsArgs.Count == 0) upsArgs = [("rack", "full-featured"), ("office", "back-ups"), ("dc", "three-phase")];

var devices = upsArgs.Select(a => new SimulatedUps(a.Name, UpsProfile.Load(a.Profile))).ToList();
await using var server = new SimulatorServer(devices, new SimulatorOptions
{
    Address = bind,
    Port = port,
    AllowAnonymousRead = anonymous,
}) { Speed = speed };

var logCommands = false;
server.CommandReceived += (client, line) => { if (logCommands) Say($"  {client} > {line}"); };

try { server.StartListening(); }
catch (System.Net.Sockets.SocketException e)
{
    Console.Error.WriteLine($"Can't listen on {bind}:{port}: {e.Message}");
    return 1;
}
server.StartClock();

Console.WriteLine($"WALLNut UPS simulator - NUT server on {bind}:{server.Port}, speed {speed}x");
foreach (var d in devices) Console.WriteLine($"  {d.Name,-10} {d.Profile.Name,-14} {d.Description}  ({d.Snapshot().Count} variables, {d.Commands.Count} commands)");
Console.WriteLine("Users: admin/admin (primary, can set and run commands), monuser/secret (secondary).");
Console.WriteLine("Type 'help' for commands.");

// Print status changes as they happen.
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
var watcher = Task.Run(async () =>
{
    var last = devices.ToDictionary(d => d.Name, d => d.Get("ups.status"));
    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
    try
    {
        while (await timer.WaitForNextTickAsync(stop.Token))
        {
            foreach (var d in devices)
            {
                var now = d.Get("ups.status");
                if (now != last[d.Name]) { Say($"{d.Name}: {last[d.Name]} -> {now}   ({Summary(d)})"); last[d.Name] = now; }
            }
        }
    }
    catch (OperationCanceledException) { }
});

if (Console.IsInputRedirected)
{
    try { await Task.Delay(Timeout.Infinite, stop.Token); } catch (OperationCanceledException) { }
}
else
{
    while (!stop.IsCancellationRequested)
    {
        Console.Write("sim> ");
        var line = await Task.Run(Console.ReadLine);
        if (line is null) break;
        try { if (!Run(line.Trim())) break; }
        catch (Exception e) when (e is ArgumentException or FormatException or KeyNotFoundException) { Say(e.Message); }
    }
}
stop.Cancel();
await watcher;
return 0;

bool Run(string line)
{
    var w = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (w.Length == 0) return true;
    string Arg(int n) => w.Length > n ? w[n] : throw new ArgumentException($"'{w[0]}' needs more arguments. Type 'help'.");
    IEnumerable<SimulatedUps> Target(int n) => Arg(n) == "all" ? devices
        : [devices.FirstOrDefault(d => d.Name == Arg(n)) ?? throw new KeyNotFoundException($"No UPS named '{Arg(n)}'.")];
    double Number(int n) => double.Parse(Arg(n), CultureInfo.InvariantCulture);
    bool OnOff(int n) => Arg(n) switch { "on" => true, "off" => false, _ => throw new ArgumentException("Use on or off.") };

    switch (w[0].ToLowerInvariant())
    {
        case "help" or "?": PrintHelp(); break;
        case "list" or "ls":
            foreach (var d in devices) Console.WriteLine($"  {d.Name,-10} {Summary(d)}");
            break;
        case "show":
            foreach (var d in Target(1))
            foreach (var (k, v) in d.Snapshot()) Console.WriteLine($"  {k,-40} {v}");
            break;
        case "outage" or "fail":
            foreach (var d in Target(1)) d.PowerFail(w.Length > 2 ? string.Join(' ', w[2..]) : "input voltage out of range");
            break;
        case "restore": foreach (var d in Target(1)) d.PowerRestore(); break;
        case "load": foreach (var d in Target(1)) d.SetLoad(Number(2)); break;
        case "charge": foreach (var d in Target(1)) d.SetCharge(Number(2)); break;
        case "voltage": foreach (var d in Target(1)) d.SetInputVoltage(Number(2)); break;
        case "rb": foreach (var d in Target(1)) d.SetReplaceBattery(OnOff(2)); break;
        case "stale": foreach (var d in Target(1)) d.SetStale(OnOff(2)); break;
        case "fsd": foreach (var d in Target(1)) d.SetForcedShutdown(); break;
        case "cmd":
            foreach (var d in Target(1)) Say($"{d.Name}: {d.RunCommand(Arg(2), w.Length > 3 ? w[3] : null)}");
            break;
        case "set":
            foreach (var d in Target(1)) Say($"{d.Name}: {d.Set(Arg(2), string.Join(' ', w[3..]))}");
            break;
        case "advance":
            foreach (var d in devices) d.Advance(TimeSpan.FromSeconds(Number(1)));
            Say($"Moved time forward {Number(1)}s.");
            break;
        case "speed": server.Speed = Number(1); Say($"Speed {server.Speed}x."); break;
        case "deny": server.DenyAll = OnOff(1); Say($"Deny all: {server.DenyAll}"); break;
        case "silent": server.Silent = OnOff(1); Say($"Silent: {server.Silent}"); break;
        case "drop": Say($"Dropping {server.ClientCount} client(s)."); server.DropAllClients(); break;
        case "stop": server.StopListening(); Say("Stopped listening (new connections are refused)."); break;
        case "start": server.StartListening(); Say($"Listening on port {server.Port}."); break;
        case "clients":
            foreach (var c in server.Clients()) Console.WriteLine($"  {c.Address,-16} user={c.User ?? "-",-10} login={c.LoggedInTo ?? "-"}");
            if (server.ClientCount == 0) Console.WriteLine("  (none)");
            break;
        case "log": logCommands = OnOff(1); break;
        case "quit" or "exit" or "q": return false;
        default: Say($"Unknown command '{w[0]}'. Type 'help'."); break;
    }
    return true;
}

static string Summary(SimulatedUps d) =>
    $"status={d.Get("ups.status")}  charge={d.Get("battery.charge")}%  runtime={d.Get("battery.runtime")}s  load={d.Get("ups.load")}%";

static void Say(string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");

static void PrintHelp() => Console.WriteLine("""
      list                       One line per UPS
      show <ups>                 All variables
      outage <ups|all> [reason]  Mains power fails
      restore <ups|all>          Mains power returns
      load <ups> <percent>       Change the load (over 100 = overload)
      charge <ups> <percent>     Set the battery charge
      voltage <ups> <volts>      Change mains voltage (outside transfer limits = on battery)
      rb <ups> on|off            Battery needs replacing
      stale <ups> on|off         Driver loses the UPS (clients get DATA-STALE)
      fsd <ups>                  Set forced shutdown (stays set until restart)
      cmd <ups> <command> [arg]  Run an instant command, e.g. cmd rack test.battery.start.quick
      set <ups> <var> <value>    Change a writable setting
      advance <seconds>          Jump simulated time forward
      speed <n>                  Simulated seconds per real second
      deny on|off                Refuse everything (ERR ACCESS-DENIED)
      silent on|off              Stop answering
      drop                       Disconnect all clients
      stop / start               Stop / start accepting connections
      clients                    Connected clients
      log on|off                 Print every command clients send
      quit
    """);

static void PrintUsage() => Console.WriteLine($"""
    wallnut-sim [options]
      --port, -p <n>          Port (default 3493)
      --bind, -b <address>    Address to listen on (default 127.0.0.1)
      --ups, -u name=profile  Add a UPS (repeatable). Profiles: {string.Join(", ", UpsProfile.BuiltIn)}, or a .json path
      --speed, -s <n>         Simulated seconds per real second (default 1)
      --no-anonymous          Require a username and password for reads too
    """);
