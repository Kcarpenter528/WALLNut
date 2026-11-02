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
| `tests/WallNut.Nut.Tests` | Tests for the protocol client |
| `tools/nut-watch.ps1` | Diagnostic script: holds a connection open and logs every drop |

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
