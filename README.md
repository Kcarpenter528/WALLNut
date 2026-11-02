# WALLNut

A wall of data for all of your NUTs.

WALLNut is a cross-platform (Windows, macOS, Linux) monitor for UPS devices served by
[Network UPS Tools (NUT)](https://networkupstools.org/), including Synology and QNAP NAS units.

> Status: early development. Not related to the GNOME Shell extension "walNUT".

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
| `src/WallNut.App` | Avalonia desktop app |
| `src/WallNut.Simulator` | Fake NUT server with simulated UPS devices (`wallnut-sim`) |
| `tests/` | Tests for each project |
| `tools/nut-watch.ps1` | Diagnostic script: holds a connection open and logs every drop |

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
