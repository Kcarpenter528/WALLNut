# Working on WALLNut

This page covers building WALLNut from source, running it against the built-in UPS simulator, and making release builds. If you just want to use WALLNut, the [README](../README.md) is what you need.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [PowerShell 7](https://learn.microsoft.com/powershell/scripting/install/installing-powershell), only for making release builds
- [Inno Setup 6](https://jrsoftware.org/isinfo.php), only for making the Windows setup program (`winget install JRSoftware.InnoSetup`)

## Build and test

```bash
dotnet build
dotnet test
```

The tests start their own fake NUT servers, so you don't need a UPS. To also run the read-only test against a real NUT server:

```powershell
$env:WALLNUT_LIVE_HOST = "192.168.1.10"; dotnet test
```

## Project layout

| Folder | What's in it |
|---|---|
| `src/WallNut.Nut` | NUT network protocol client |
| `src/WallNut.Service` | The background service: monitoring, shutdown, alerts and actions, history, and the local API |
| `src/WallNut.App` | The desktop and tray app (Avalonia) |
| `src/WallNut.Shared` | Types and the API client shared by the service and the app |
| `src/WallNut.History` | History storage (SQLite) |
| `src/WallNut.Charts` | History charts (ScottPlot) |
| `src/WallNut.Simulator` | A fake NUT server with simulated UPS units (`wallnut-sim`) |
| `tests/` | Tests for each project |
| `packaging/` | Install scripts, the Windows setup script, and `build.ps1` |
| `tools/nut-watch.ps1` | Holds a NUT connection open and logs every drop. Useful for diagnosing flaky NAS connections. |

The service and the app talk over a local-only channel: the named pipe `\\.\pipe\WallNut.Service` on Windows, or a Unix socket on macOS and Linux. Nothing listens on a network port. Reading status is open to any local user; changing settings requires an administrator (or root), or the account the service itself runs as.

## Run it against the simulator

Start the simulator:

```bash
dotnet run --project src/WallNut.Simulator
```

It serves three UPS units on `127.0.0.1:3493`:

| Name | Profile | What it is |
|---|---|---|
| `rack` | `full-featured` | Reports every variable and command in the NUT spec (375 variables, 95 commands) |
| `office` | `back-ups` | A small APC Back-UPS, like the ones usually plugged into a NAS |
| `dc` | `three-phase` | A 30 kVA three-phase UPS with per-phase readings |

At the `sim>` prompt you can type `outage rack`, `restore rack`, `charge rack 15`, `rb office on`, `drop`, `deny on`, `speed 60` and more. Type `help` for the full list. The simulator has two accounts: `admin`/`admin`, which can change settings and run commands, and `monuser`/`secret`, which matches Synology's monitoring account.

In a second terminal, run the service in Development mode. It watches the three simulated UPS units, uses its own pipe name (`WallNut.Service.Dev`), and stays in test mode:

```bash
dotnet run --project src/WallNut.Service
```

In a third terminal, run the app against it:

```bash
dotnet run --project src/WallNut.App -- --endpoint=WallNut.Service.Dev --open
```

The app also accepts `--page=overview|history|events|settings|shutdown|actions`, `--ups=<id>`, `--history --range=30d`, and `--settings`.

To try the charts without waiting weeks for data, fill a development database with simulated history:

```bash
dotnet run --project src/WallNut.Simulator -- seed-history --days 30
```

To rehearse a shutdown, run `outage office` and then `charge office 5` in the simulator, and watch the service log.

## Install from a source checkout

The install scripts build everything themselves when they're run from the repository, and they can set up the first UPS for you:

```powershell
# Windows, from an administrator PowerShell
packaging\windows\install.ps1 -NasHost 192.168.1.10
```

```bash
sudo packaging/linux/install.sh --nas-host 192.168.1.10
sudo packaging/macos/install.sh --nas-host 192.168.1.10
```

## Make release builds

`packaging/build.ps1` publishes self-contained builds and packages them into `dist/`:

```powershell
./packaging/build.ps1 -Rid win-x64 -Version 0.2.0       # setup.exe and .zip
./packaging/build.ps1 -Rid linux-x64 -Version 0.2.0     # .tar.gz
```

Supported runtimes are `win-x64`, `linux-x64`, `linux-arm64`, `osx-x64` and `osx-arm64`. Build each one on its own operating system so file permissions come out right. Add `-NoInstaller` to skip the Windows setup program.

## Continuous integration and releases

- [ci.yml](../.github/workflows/ci.yml) builds and tests on Windows, Linux and macOS for every push and pull request.
- [release.yml](../.github/workflows/release.yml) runs the tests, then builds all five downloads.
  - **Tag push (`v0.2.0`):** creates a **draft** GitHub release with the downloads and a `SHA256SUMS.txt`. Review it, then click **Publish**. Tags with a hyphen, like `v0.2.0-beta.1`, are marked as pre-releases.
  - **Manual run (Actions > Release > Run workflow):** builds a test version you choose. The downloads are attached to the run as artifacts and no release is made.

Release files are named without the version (for example `WALLNut-win-x64-setup.exe`), so the README's `releases/latest/download/...` links always point at the newest published release. Pre-releases and drafts aren't counted as "latest".

To make a release:

```bash
git tag v0.2.0
git push origin v0.2.0
```
