using WallNut.Service;

// WallNut.Service             run (as a console app, Windows service, systemd or launchd service)
// WallNut.Service --check     validate settings, try each UPS once, and exit
var check = args.Contains("--check");
var app = ServiceHost.Build(args.Where(a => a != "--check").ToArray());

if (check) return await ConfigCheck.RunAsync(app, Console.Out);

await app.RunAsync();
return 0;
