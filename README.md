# WALLNut

A wall of data for all of your NUTs.

WALLNut is a cross-platform (Windows, macOS, Linux) monitor for UPS devices served by
[Network UPS Tools (NUT)](https://networkupstools.org/), including Synology and QNAP NAS units.

> Status: early development. Not related to the GNOME Shell extension "walNUT".

License: [GPL-3.0-or-later](LICENSE).

## Planned features

- Monitor several NUT servers and UPS devices at once, each on its own connection
- Choose which UPS devices power this machine and can trigger shutdown
- Background service that shuts the machine down safely, even when nobody is logged in
- Phone notifications (ntfy first, more later) and webhooks on power events
- History and charts of battery, load and runtime
- A modern desktop app (Avalonia) with a tray icon

## Layout

| Path | What it is |
|---|---|
| `src/WallNut.Nut` | NUT network protocol client |
| `src/WallNut.Service` | Background service (monitoring, shutdown, alerts, history) |
| `src/WallNut.App` | Avalonia tray app: desktop notifications and settings (dashboard later) |
| `src/WallNut.Shared` | Messages and client shared by the service and apps; notification rules |
| `src/WallNut.Simulator` | Fake NUT server with simulated UPS devices (`wallnut-sim`) |
| `tests/` | Tests for each project |
| `tools/nut-watch.ps1` | Diagnostic script: holds a connection open and logs every drop |

## Install

Download from the [Releases](https://github.com/Kcarpenter528/WALLNut/releases) page. Every
download is self-contained (no .NET needed), installs the service to start at boot and restart if
it crashes, and starts the tray app at login for every user. Installing again upgrades in place and
keeps your settings. Then open WALLNut and add your UPS under **Settings > UPS connections**.

| OS | Download | Install | Settings | Logs |
|---|---|---|---|---|
| Windows 10/11 | `WALLNut-<version>-win-x64-setup.exe` | Run it | `C:\ProgramData\WALLNut\config.json` | Event Viewer > Application, source `WALLNut` |
| Linux (systemd) | `WALLNut-<version>-linux-x64.tar.gz` or `-linux-arm64` (Raspberry Pi 4/5) | Extract, then `sudo ./install.sh` | `/etc/wallnut/config.json` | `journalctl -u wallnut -f` |
| macOS | `WALLNut-<version>-osx-arm64.tar.gz` (Apple silicon) or `-osx-x64` (Intel) | Extract, then `sudo ./install.sh` | `/Library/Application Support/WALLNut/config.json` | `/Library/Logs/WALLNut/service.log` |

Settings files are readable by administrators only (they can hold passwords). See
[packaging/config.example.json](packaging/config.example.json) for every option, and check them any
time with `WallNut.Service --check`. Shutdown starts in **test mode**; turn it off in
Settings > Shutdown once you've rehearsed an outage.

The macOS downloads aren't signed by Apple yet; `install.sh` clears the download quarantine.

### From source

The same scripts build from a clone (needs the .NET 10 SDK) and can set up the first UPS:

| OS | Command |
|---|---|
| Windows | `packaging\windows\install.ps1 -NasHost 192.168.1.10` (admin PowerShell) |
| Linux | `sudo packaging/linux/install.sh --nas-host 192.168.1.10` |
| macOS | `sudo packaging/macos/install.sh --nas-host 192.168.1.10` |

### Making the downloads

`packaging/build.ps1 -Rid <win-x64|linux-x64|linux-arm64|osx-x64|osx-arm64> -Version 1.2.3`
(PowerShell 7) writes them to `dist/`. The Windows setup program needs
[Inno Setup 6](https://jrsoftware.org/isinfo.php). GitHub Actions runs the tests on Windows, Linux
and macOS for every push ([ci.yml](.github/workflows/ci.yml)); pushing a tag such as `v0.2.0`
builds every download and drafts a release with them ([release.yml](.github/workflows/release.yml)).

## Try it with the simulator

```bash
dotnet run --project src/WallNut.Simulator
```

This starts a NUT server on `127.0.0.1:3493` with three UPSes:

| Name | Profile | What it is |
|---|---|---|
| `rack` | `full-featured` | Reports every variable and command in the NUT spec (375 variables, 95 commands) |
| `office` | `back-ups` | A small APC Back-UPS, like the ones usually plugged into a NAS |
| `dc` | `three-phase` | A 30 kVA three-phase UPS with per-phase readings |

At the `sim>` prompt, type `outage rack`, `restore rack`, `charge rack 15`, `rb office on`, `drop`,
`deny on`, `speed 60` and so on (`help` lists everything). Users: `admin`/`admin` (can change
settings and run commands) and `monuser`/`secret` (Synology's monitoring account).

In another terminal, run the service. In Development it watches the three simulated UPSes plus
whatever is in `appsettings.json`:

```bash
dotnet run --project src/WallNut.Service
```

## Desktop app

Click the tray icon (or **Open WALLNut**) for the main window:

- **Overview** - the wall: a live card per UPS with status, battery gauge, runtime, load, input
  voltage and the last hour at a glance, plus the overall state and shutdown mode.
- **UPS details** - click a card for big gauges, identity (model, serial, firmware) and every
  variable the UPS reports, searchable and refreshed every 2 seconds.
- **History** - charts, outages and CSV export (see below).
- **Events** - the last 30 days of events, filterable, updating live.
- **Settings** - add, edit, test and remove UPS connections (IP address or DNS name), shutdown rules
  and test mode, and your own notification choices.

Anyone logged in can look at settings; changing them needs an administrator. **Unlock with
administrator rights** reopens Settings elevated (Windows asks for approval). Changes are written to
the machine settings file and the service applies them immediately, without a restart.

Shortcuts: `WALLNut --open`, `--page=history|events|settings|shutdown`, `--ups=<id>`.

## Desktop notifications (tray app)

Background services can't show desktop notifications on Windows, macOS or Linux, so each
logged-in user runs the small WALLNut tray app. It connects to the service over a local-only
channel (named pipe `\\.\pipe\WallNut.Service` on Windows, a Unix socket elsewhere) and shows
native notifications: Windows toasts, macOS Notification Center, or `notify-send` on Linux.

```bash
dotnet run --project src/WallNut.App -- --settings
```

The tray icon's dot shows overall health (none = fine, amber = needs attention, red = critical,
grey = service not running). Click it for notification settings: a master switch, a delay that
hides short power blips, per-group and per-event switches, and per-UPS muting. Settings are
saved per user. Shutdown warnings can't be turned off.

## History and charts

The service records every UPS's readings and events in SQLite (`history.db` in the data folder:
`C:\ProgramData\WALLNut`, `/var/lib/wallnut` or `/Library/Application Support/WALLNut`), even
when nobody is logged in. To keep it small it stores every 15-second sample for 3 days, then
5-minute and hourly average / min / max for 90 days and 2 years.

Click the tray icon (or **History and charts...**) for stacked, linked charts: battery, load,
power, voltage and frequency, temperatures, and per-phase or per-outlet readings where the UPS
reports them. Outages are shaded on every chart, summary cards show outage counts and lowest
battery, and the range can be exported as CSV. `WALLNut --history --range=30d` opens it directly.

To try the charts without waiting weeks, fill a development database with simulated history:

```bash
dotnet run --project src/WallNut.Simulator -- seed-history --days 30
```

## Configuring UPS endpoints

Edit `src/WallNut.Service/appsettings.json`. Each entry under `WallNut:Endpoints` is one UPS with
its own connection. `Host` can be an IP address or a DNS name; names are looked up again on every
reconnect, so a NAS whose address changes is followed automatically.

## Shutdown

Only UPSes with `"Role": "PowersThisMachine"` can shut this machine down. Like NUT's `upsmon`, each
counts as a power supply (`PowerValue`, usually 1), and the machine shuts down when fewer than
`Shutdown:MinimumSupplies` are healthy. A UPS is critical when:

- the NUT server sets forced shutdown (FSD), e.g. a Synology about to power off
- it is on battery and reports low battery
- it is on battery and below `BatteryChargeBelowPercent` or `RuntimeBelowSeconds` (optional)
- it has been on battery for `OnBatteryForSeconds` (optional)
- contact was lost, or its data went stale, for `LostWhileOnBatterySeconds` while it was on battery

**`Shutdown:DryRun` is `true` by default**: WALLNut logs "would shut down now" with the exact command
instead of running it. Rehearse with the simulator (`outage office`, then `charge office 5`) before
setting it to `false`.

## Build and test

Requires the .NET 10 SDK.

```bash
dotnet build
dotnet test
```

To also run the read-only test against a real NUT server:

```powershell
$env:WALLNUT_LIVE_HOST = "192.168.1.10"; dotnet test
```
