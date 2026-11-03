# WALLNut

*A wall of data for all of your NUTs.*

WALLNut watches the UPS units on your network and tells you when something happens to the power. If the battery starts to run out, it shuts your computer down cleanly. It works with anything that shares a UPS over [Network UPS Tools (NUT)](https://networkupstools.org/). That includes Synology and QNAP NAS units, a Raspberry Pi running NUT, and full NUT servers.

It runs on Windows, macOS and Linux, and it's free and open source under the [GPL-3.0](LICENSE).

> [!NOTE]
> WALLNut is in early development. It works, and I use it at home, but expect rough edges. Keep your current UPS software in charge of shutdowns until you've tested WALLNut on your own setup (see [Test before you trust it](#test-before-you-trust-it)).

## Contents

- [What it does](#what-it-does)
- [Download](#download)
- [Install](#install)
- [Connect your first UPS](#connect-your-first-ups)
- [Set up automatic shutdown](#set-up-automatic-shutdown)
- [Get alerts on your phone, by email, or anywhere else](#get-alerts-on-your-phone-by-email-or-anywhere-else)
- [Desktop notifications](#desktop-notifications)
- [History and charts](#history-and-charts)
- [Troubleshooting](#troubleshooting)
- [Uninstall](#uninstall)
- [Where things are stored](#where-things-are-stored)
- [Contributing](#contributing)

## What it does

- **Watches several UPS units at once.** Your NAS, the rack in the closet, the one under your desk. Each has its own connection, so one going quiet doesn't affect the others. You can use IP addresses or host names like `diskstation.local`.
- **Shuts your computer down safely.** You choose which UPS units power this computer and when to act: on low battery, below a charge level, after a set time on battery, or when the NAS says it's powering off. A background service handles it, so it works even when nobody is logged in.
- **Starts in test mode.** Until you switch it off, WALLNut only logs what it *would* have done. You can rehearse an outage without anything shutting down.
- **Sends alerts wherever you want them.** Phone alerts through [ntfy](https://ntfy.sh), email, Discord, Slack, Microsoft Teams, Home Assistant, any webhook, or your own script.
- **Shows desktop notifications.** You get a notification when the power fails or comes back, the battery is low, or a UPS stops answering. Each person on the computer can choose which ones they see.
- **Shows the wall.** A live card for each UPS shows status, charge, runtime, load and input voltage. Click a card to see every value the UPS reports.
- **Keeps history.** Charts of battery, load, voltage and temperature over the last few hours or the last two years, with outages marked. You can export to CSV.

## Download

Get the latest release for your system:

| System | Installer | Other formats |
|---|---|---|
| Windows 10 or 11 (64-bit) | [WALLNut-win-x64-setup.exe](https://github.com/Kcarpenter528/WALLNut/releases/latest/download/WALLNut-win-x64-setup.exe) | [.zip](https://github.com/Kcarpenter528/WALLNut/releases/latest/download/WALLNut-win-x64.zip) |
| macOS, Apple silicon (M1 and later) | [WALLNut-osx-arm64.pkg](https://github.com/Kcarpenter528/WALLNut/releases/latest/download/WALLNut-osx-arm64.pkg) | [.tar.gz](https://github.com/Kcarpenter528/WALLNut/releases/latest/download/WALLNut-osx-arm64.tar.gz) |
| macOS, Intel | [WALLNut-osx-x64.pkg](https://github.com/Kcarpenter528/WALLNut/releases/latest/download/WALLNut-osx-x64.pkg) | [.tar.gz](https://github.com/Kcarpenter528/WALLNut/releases/latest/download/WALLNut-osx-x64.tar.gz) |
| Debian, Ubuntu, Linux Mint (64-bit PC) | [WALLNut-linux-x64.deb](https://github.com/Kcarpenter528/WALLNut/releases/latest/download/WALLNut-linux-x64.deb) | [.tar.gz](https://github.com/Kcarpenter528/WALLNut/releases/latest/download/WALLNut-linux-x64.tar.gz) |
| Fedora, RHEL, Rocky, AlmaLinux (64-bit PC) | [WALLNut-linux-x64.rpm](https://github.com/Kcarpenter528/WALLNut/releases/latest/download/WALLNut-linux-x64.rpm) | |
| Raspberry Pi OS and other ARM64 Debian/Ubuntu | [WALLNut-linux-arm64.deb](https://github.com/Kcarpenter528/WALLNut/releases/latest/download/WALLNut-linux-arm64.deb) | [.tar.gz](https://github.com/Kcarpenter528/WALLNut/releases/latest/download/WALLNut-linux-arm64.tar.gz) |
| Fedora and RHEL on ARM64 | [WALLNut-linux-arm64.rpm](https://github.com/Kcarpenter528/WALLNut/releases/latest/download/WALLNut-linux-arm64.rpm) | |

Not sure which Mac you have? Open the Apple menu > **About This Mac**. If **Chip** says Apple M1, M2, M3 or later, use Apple silicon. If it says **Processor** with an Intel name, use Intel. On a Raspberry Pi, you need a 64-bit OS (`uname -m` prints `aarch64`).

The **.tar.gz** downloads work on any Linux with systemd, and on macOS if you'd rather install from Terminal (see [Installing from the .tar.gz](#installing-from-the-targz)). The Windows **.zip** contains the same files as the setup program, for scripted installs.

Older versions, release notes and checksums (`SHA256SUMS.txt`) are on the [Releases page](https://github.com/Kcarpenter528/WALLNut/releases). You don't need to install .NET or anything else first; each download includes everything it needs.

**Want the newest code before it's released?** Every change is built automatically. Open the [Release workflow runs](https://github.com/Kcarpenter528/WALLNut/actions/workflows/release.yml), pick the most recent green run, and scroll down to **Artifacts**. Each download is listed by name, such as `WALLNut-osx-arm64.pkg`. GitHub wraps each one in a .zip, so unzip it first. You have to be signed in to GitHub to download artifacts, and they're deleted after 90 days.

## Install

Installing a newer version over an existing one upgrades it and keeps your settings.

Every installer sets up the same two parts:

- **The WALLNut service**, which does the monitoring and shutdowns. It starts with the computer and runs whether or not anyone is signed in.
- **The WALLNut app**, which sits in the system tray (menu bar on a Mac) for every user and shows notifications.

### Windows

1. Run **WALLNut-win-x64-setup.exe** and approve the administrator prompt.
2. Click through the installer. At the end, leave **Open WALLNut to add your UPS** checked.

> [!TIP]
> If Windows SmartScreen says "Windows protected your PC", click **More info** > **Run anyway**. The installer isn't code-signed yet, so new versions show this warning until enough people have downloaded them.

For unattended installs, run `WALLNut-win-x64-setup.exe /VERYSILENT /SUPPRESSMSGBOXES`.

### macOS

1. Double-click **WALLNut-osx-arm64.pkg** (or **WALLNut-osx-x64.pkg** on an Intel Mac).
2. Follow the installer and enter your Mac password when asked.

The menu bar app opens when the installer finishes, and again each time you log in.

> [!NOTE]
> The installer isn't signed by Apple yet, so macOS blocks it the first time with a message like "Apple could not verify WALLNut is free of malware". To open it anyway:
>
> - **macOS 15 Sequoia and later:** click **Done**, open **System Settings** > **Privacy & Security**, scroll down to the message about WALLNut, and click **Open Anyway**.
> - **macOS 14 Sonoma and earlier:** Control-click the .pkg file, choose **Open**, then click **Open** again.

If something goes wrong during the install, the details are in `/Library/Logs/WALLNut/install.log`.

### Debian, Ubuntu and Raspberry Pi OS

Download the .deb, then install it with `apt` so any missing system libraries are installed too:

```bash
sudo apt install ./WALLNut-linux-x64.deb       # or WALLNut-linux-arm64.deb on a Raspberry Pi
```

To upgrade, download the new .deb and run the same command.

On a server with no desktop, add `--no-install-recommends` to skip the libraries only the tray app needs.

### Fedora, RHEL, Rocky Linux and AlmaLinux

```bash
sudo dnf install ./WALLNut-linux-x64.rpm       # or WALLNut-linux-arm64.rpm
```

To upgrade, download the new .rpm and run `sudo dnf upgrade ./WALLNut-linux-x64.rpm`.

After installing on Linux, open **WALLNut** from your applications menu. It also starts automatically the next time you log in.

> [!NOTE]
> On GNOME, tray icons only appear if you install the [AppIndicator extension](https://extensions.gnome.org/extension/615/appindicator-support/). KDE, Cinnamon, XFCE and most other desktops show them without it.

On a server with no desktop, the service works the same; you set it up by editing the settings file (see [Where things are stored](#where-things-are-stored)).

### Installing from the .tar.gz

Use the .tar.gz on Linux distributions without apt or dnf, or on a Mac if you prefer Terminal. Extract it, then run the install script:

```bash
tar -xzf WALLNut-linux-x64.tar.gz
cd WALLNut-*-linux-x64
sudo ./install.sh                # add --no-tray on a server with no desktop
```

The same steps work for the macOS .tar.gz. On Linux the script installs to `/opt/wallnut` and registers a systemd service called `wallnut`. On macOS it installs to `/usr/local/wallnut`. Don't mix the two methods on one Linux machine: if WALLNut was installed from a .deb or .rpm, keep upgrading it that way.

## Connect your first UPS

### Step 1: Turn on network UPS sharing on your NAS or NUT server

WALLNut connects to a NUT server, so the device the UPS is plugged into (usually by USB) has to share it on the network.

**Synology DSM**

1. Go to **Control Panel** > **Hardware & Power** > **UPS**.
2. Check **Enable UPS support** and **Enable network UPS server**.
3. Click **Permitted DiskStation Devices** and add the IP address of each computer that runs WALLNut.
4. Click **Apply**.

Synology's built-in account is username `monuser` with password `secret`, and the UPS name is `ups`. The WALLNut app has a **Use Synology defaults** button that fills these in.

**QNAP QTS**

1. Go to **Control Panel** > **External Device** > **UPS**.
2. Check **Enable network UPS master** and add the IP addresses of your computers to the allowed list.
3. Click **Apply**.

The UPS name is `qnapups`. QNAP's built-in account is usually username `admin` with password `123456`.

**Your own NUT server**

Add a user for WALLNut with `upsmon secondary` rights in `upsd.users`, and make sure `upsd.conf` listens on a network address, not just `127.0.0.1`.

> [!IMPORTANT]
> Synology and QNAP only answer the computers on their allowed list. If your PC gets its address from DHCP, it can change one day and WALLNut will suddenly be refused. To avoid that, give the computer a fixed address or a DHCP reservation in your router.

### Step 2: Add the UPS in WALLNut

1. Open WALLNut (click the tray icon) and go to **Settings** > **UPS connections**.
2. Changing settings needs administrator rights. If you see a banner, click **Unlock with administrator rights**.
3. Click **+ Add a UPS** and fill in the form:

   | Field | What to enter |
   |---|---|
   | **Name** | Anything you like, such as `Synology NAS` |
   | **Host** | The NAS's IP address or host name, such as `192.168.1.10` or `diskstation.local` |
   | **Port** | `3493` (the NUT default) |
   | **UPS name** | `ups` for Synology, `qnapups` for QNAP |
   | **This UPS** | **This computer is plugged into it** if it powers this computer. Otherwise, **Just monitor it**. |
   | **Username / Password** | The NUT account from step 1. You need it when the UPS powers this computer. |

4. Click **Test connection**. You should see the UPS model and its current status. If you don't, the message tells you what went wrong. Also see [Troubleshooting](#troubleshooting).
5. Click **Save**. The UPS appears on the overview within a few seconds, with no restart needed.

Repeat for each UPS you want to watch.

## Set up automatic shutdown

Only a UPS set to **This computer is plugged into it** can shut this computer down. Open **Settings** > **Shutdown** to choose when that happens.

A UPS counts as failing when any of these is true (you can switch each one on or off):

- The NUT server orders a shutdown. A Synology does this right before it powers itself off.
- The UPS reports low battery.
- The charge or remaining runtime drops below a level you set, for example 30% or 5 minutes.
- It has been on battery longer than a time you set, for example 10 minutes.
- WALLNut loses contact with the UPS while it's on battery. If the network switch has no power, the NAS can't be reached, so WALLNut assumes the worst after a short wait.

Before it shuts down, WALLNut waits a few seconds so your alerts go out first. You can choose **Shutdown** or **Hibernate**.

If more than one UPS powers this computer (for example, a server with two power supplies), set **Needs** to the number of healthy UPS units required to keep running. WALLNut shuts down when fewer than that are left.

### Test before you trust it

Automatic shutdown starts in **test mode**. In test mode WALLNut goes through everything, including the notifications, alerts and the "Shutting down" warning, but it only *logs* the shutdown instead of doing it.

To rehearse:

1. Make sure test mode is on (**Settings** > **Shutdown** > **Test mode: only log what would happen**).
2. Unplug the UPS from the wall, or use your NAS's UPS test if it has one.
3. Watch WALLNut. You should get a **Power failure** notification, and when the battery gets low, **Shutting down (test mode)**.
4. Plug the UPS back in.

When it behaves the way you expect, select **Armed: really shut down** and save. If you were running other UPS software such as WinNUT, uninstall it now so the two don't fight over the shutdown.

## Get alerts on your phone, by email, or anywhere else

Desktop notifications only reach someone sitting at the computer. Alerts are sent by the WALLNut service itself, so they arrive even when nobody is logged in.

Set them up in **Settings** > **Alerts & actions**. Each alert has:

- **When:** the events that trigger it. Power failures, low battery, shutdowns and lost contact are selected by default.
- **For:** all UPS units, or only the ones you pick.
- **Only if it lasts:** a delay that skips short blips. For example, with 30 seconds, a 5-second flicker sends nothing. Low battery and shutdown warnings are never delayed.

Every alert has a **Send test** button. Use it.

> [!TIP]
> Alerts need the internet. If your router and modem aren't on a UPS, a power cut takes the internet down with it, and alerts wait until it comes back. Plugging the router into the UPS is the single best thing you can do for alerts.

### Phone alerts (ntfy)

[ntfy](https://ntfy.sh) is a free push notification service with apps for Android and iPhone. You don't need an account.

1. Install the **ntfy** app on your phone ([Android](https://play.google.com/store/apps/details?id=io.heckel.ntfy), [iPhone](https://apps.apple.com/app/ntfy/id1625396347)).
2. In WALLNut, click **+ Phone alert (ntfy)**. WALLNut makes up a long random topic name for you, because anyone who knows the topic can read its messages.
3. In the ntfy app, tap **+** and scan the QR code shown in WALLNut, or type the topic in by hand.
4. Click **Send test**, then **Save**.

Power failures and shutdowns are sent as high-priority alerts. On Android you can let ntfy override Do Not Disturb for them.

If you run your own ntfy server, change **Server** to its address and add an access token if your topics are protected.

### Email

Click **+ Email** and pick your provider. The server, port and security settings are filled in for you.

| Provider | What to use as the password |
|---|---|
| Gmail | An **app password**: Google Account > Security > 2-Step Verification > App passwords. Your normal Gmail password won't work. |
| iCloud Mail | An app-specific password from [account.apple.com](https://account.apple.com) > Sign-In and Security |
| Yahoo Mail | An app password from Account Security > Generate app password |
| Fastmail | An app password from Settings > Privacy & Security > App passwords |
| Outlook.com / Microsoft 365 | Your password, but Microsoft is phasing out password sign-in for sending mail, so this may be refused. Sign-in with a Microsoft account is planned. |
| Other mail server | Whatever your provider or server requires. Port 587 with STARTTLS is the usual choice. |

Leave **User name** blank to use the **From** address. To send to several people, separate the addresses with commas.

The password is stored in the service's settings file, which only administrators can read.

### Discord, Slack, Teams, Home Assistant and other webhooks

Click **+ Webhook** and choose what it's for. WALLNut formats the message the way that service expects:

| Send to | Where to find the URL |
|---|---|
| Discord | Server Settings > Integrations > Webhooks > New Webhook > Copy Webhook URL |
| Slack | Add the **Incoming Webhooks** app to a channel and copy its URL |
| Microsoft Teams | Workflows > **Post to a channel when a webhook request is received** |
| Home Assistant | Create an automation with a **Webhook** trigger. The URL looks like `http://homeassistant.local:8123/api/webhook/<id>`. |
| Generic | Any service that accepts JSON. See the example below. |
| Custom | Write your own body, with placeholders like `{ups}`, `{event}` and `{charge}` |

The **Generic** format sends JSON like this:

```json
{
  "source": "wallnut",
  "event": "OnBattery",
  "title": "Power failure: Synology NAS",
  "message": "Synology NAS is on battery.",
  "problem": true,
  "urgency": "critical",
  "time": "2026-11-02T19:42:07.0000000+00:00",
  "machine": "OFFICE-PC",
  "test_mode": false,
  "ups": {
    "id": "nas",
    "name": "Synology NAS",
    "host": "192.168.1.10",
    "status": "OB DISCHRG",
    "charge": 87,
    "runtime": 1260,
    "load": 31
  }
}
```

To let the receiver check that a request really came from WALLNut, set a **Signing secret**. Each request then carries an `X-WallNut-Signature: sha256=<hex>` header with an HMAC-SHA256 of `<X-WallNut-Timestamp>.<body>`.

### Run a script

Scripts let you do anything else: stop virtual machines, pause backups, or turn on a light. Click **+ Run a script** and choose the file. It can be a `.ps1`, `.bat`, `.cmd` or `.exe` file on Windows, or a shell script or other executable on macOS and Linux.

The event details are passed to the script as environment variables:

| Variable | Example |
|---|---|
| `WALLNUT_EVENT` | `OnBattery` |
| `WALLNUT_UPS` | `Synology NAS` |
| `WALLNUT_STATUS` | `OB DISCHRG` |
| `WALLNUT_CHARGE` | `87` |
| `WALLNUT_RUNTIME` | `1260` (seconds) |
| `WALLNUT_MESSAGE` | `Synology NAS is on battery.` |
| `WALLNUT_TEST_MODE` | `true` during a shutdown test |

You can also put the same values in the arguments, like `{event} "{ups}"`.

Check **Run before this computer shuts down** for anything that has to finish first, such as stopping VMs or containers cleanly. The shutdown waits for the script, up to its time limit, and then goes ahead regardless. During a shutdown test these scripts are skipped, unless you also check **Also run it during shutdown tests**.

> [!IMPORTANT]
> The service runs scripts with full system rights (SYSTEM on Windows, root on macOS and Linux). For that reason, WALLNut refuses to run a script that a normal user could edit. Put your scripts in the folder the installer created for them:
>
> - Windows: `C:\ProgramData\WALLNut\scripts`
> - macOS: `/Library/Application Support/WALLNut/scripts`
> - Linux: `/etc/wallnut/scripts`

The script's output goes to the WALLNut service log.

## Desktop notifications

The WALLNut app in the tray shows a notification for power failures, low battery, shutdown warnings and lost contact. The tray icon's dot also shows the overall state:

| Dot | Meaning |
|---|---|
| None | All good |
| Amber | Something needs attention, such as a UPS on battery or one that isn't answering |
| Red | Critical: low battery or a shutdown on the way |
| Grey | The WALLNut service isn't running |

To choose what you see, go to **Settings** > **Notifications**. You can turn notifications off entirely, hide power blips shorter than a few seconds, switch individual events on or off, or mute a single UPS. These choices are per user, so they don't need administrator rights. Shutdown warnings are always shown.

Use **Send test notification** in the tray menu to check that notifications get through.

## History and charts

The service records readings from every UPS in the background, whether or not the app is open. Open **History** in the app to see:

- Charts of battery charge, runtime, load, power, input and output voltage, frequency and temperature, depending on what your UPS reports.
- Outages shaded on every chart, with a summary of how many there were and how low the battery went.
- Ranges from the last hour to the last two years.
- **Export CSV** for the selected range.

WALLNut keeps every 15-second reading for 3 days, 5-minute averages for 90 days, and hourly averages for 2 years. A year of history for one UPS takes a few megabytes.

## Troubleshooting

### "Connection refused" or "the server closed the connection" right away

The NAS isn't accepting connections from this computer.

- On Synology, check that this computer's **current** IP address is in **Permitted DiskStation Devices**. This is the most common cause, especially after the computer's address changes. To see your address on Windows, run `ipconfig`.
- Check that **Enable network UPS server** is still on.
- Check that a firewall on the NAS, or between you and the NAS, allows TCP port 3493.

### "Unknown UPS"

The **UPS name** doesn't match what the server calls it. The error message lists the names the server does have. Synology uses `ups` and QNAP uses `qnapups`.

### "Access denied" or "invalid password"

The username or password is wrong, or the account isn't allowed to monitor. For Synology, click **Use Synology defaults**. For your own NUT server, check `upsd.users` and restart `upsd`.

### It connects, then drops every few minutes

This usually comes down to the network or the NAS rather than WALLNut. WALLNut reconnects on its own and shows the UPS as **Not connected** in the meantime. If it happens a lot:

- Use a wired connection for this computer if you can.
- Make sure the computer's IP address isn't changing (see the note in [Step 1](#step-1-turn-on-network-ups-sharing-on-your-nas-or-nut-server)).

### The app says "Can't reach the WALLNut service"

The background service isn't running.

- **Windows:** open **Services** (`services.msc`), find **WALLNut UPS Monitor**, and start it. If it stops again, look in **Event Viewer** > **Windows Logs** > **Application** for errors from the source `WALLNut`.
- **macOS:** `sudo launchctl kickstart -k system/wallnut.service`, then check `/Library/Logs/WALLNut/service.log`.
- **Linux:** `sudo systemctl restart wallnut`, then check `journalctl -u wallnut -e`.

### Checking your settings from the command line

The service can check its own settings and try each UPS connection:

```powershell
# Windows (administrator PowerShell)
& "C:\Program Files\WALLNut\Service\WallNut.Service.exe" --check
```

```bash
# macOS
sudo /usr/local/wallnut/service/WallNut.Service --check
# Linux
sudo /opt/wallnut/service/WallNut.Service --check
```

### I don't get notifications on my phone

- In WALLNut, click **Send test** on the alert. If the test fails, the message explains why.
- If the test works but real alerts don't arrive, check the alert's **When** and **For** settings, and whether **Only if it lasts** is longer than the outage was.
- If an alert keeps failing, WALLNut shows an **Alert or action failed** notification on the desktop.

## Uninstall

| Installed with | To remove WALLNut | To also delete your settings and history |
|---|---|---|
| Windows setup | **Settings** > **Apps** > **WALLNut** > **Uninstall** | Answer **Yes** when you're asked |
| macOS .pkg or .tar.gz | `sudo /usr/local/wallnut/uninstall.sh` | `sudo /usr/local/wallnut/uninstall.sh --remove-settings` |
| .deb | `sudo apt remove wallnut` | `sudo apt purge wallnut` |
| .rpm | `sudo dnf remove wallnut` | Then `sudo rm -rf /etc/wallnut /var/lib/wallnut` |
| Linux .tar.gz | `sudo /opt/wallnut/uninstall.sh` | `sudo /opt/wallnut/uninstall.sh --remove-settings` |

## Where things are stored

| | Windows | macOS | Linux |
|---|---|---|---|
| Program | `C:\Program Files\WALLNut` | `/usr/local/wallnut` | `/opt/wallnut` |
| Settings | `C:\ProgramData\WALLNut\config.json` | `/Library/Application Support/WALLNut/config.json` | `/etc/wallnut/config.json` |
| History | `C:\ProgramData\WALLNut\history.db` | `/Library/Application Support/WALLNut/history.db` | `/var/lib/wallnut/history.db` |
| Logs | Event Viewer > Application, source `WALLNut` | `/Library/Logs/WALLNut/service.log` | `journalctl -u wallnut` |

Only administrators can read the settings file, because it can contain passwords. You can edit it by hand if you prefer. Every option is described in [config.example.json](packaging/config.example.json), which is also copied into the program folder. Restart the service after editing.

## Contributing

Bug reports and ideas are welcome in [Issues](https://github.com/Kcarpenter528/WALLNut/issues). When reporting a connection problem, it helps to include your NAS or NUT server model and the output of `WallNut.Service --check`. Remove any passwords first.

To build WALLNut yourself, run it against the built-in UPS simulator, or work on the code, see [docs/development.md](docs/development.md).

WALLNut isn't affiliated with the Network UPS Tools project, Synology or QNAP, and it isn't related to the GNOME Shell extension "walNUT".
