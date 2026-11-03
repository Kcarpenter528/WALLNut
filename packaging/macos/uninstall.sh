#!/usr/bin/env bash
# Removes WALLNut. Keeps settings unless --remove-settings.  sudo ./uninstall.sh [--remove-settings]
set -euo pipefail
[[ $EUID -eq 0 ]] || { echo "Please run with sudo." >&2; exit 1; }

launchctl bootout system /Library/LaunchDaemons/wallnut.service.plist 2>/dev/null || true
console_user="$(stat -f %Su /dev/console)"
[[ -n "$console_user" && "$console_user" != "root" ]] && \
  launchctl bootout "gui/$(id -u "$console_user")" /Library/LaunchAgents/wallnut.tray.plist 2>/dev/null || true
pkill -x WALLNut 2>/dev/null || true
rm -f /Library/LaunchDaemons/wallnut.service.plist /Library/LaunchAgents/wallnut.tray.plist
rm -rf /usr/local/wallnut /var/run/wallnut
pkgutil --forget io.github.kcarpenter528.wallnut >/dev/null 2>&1 || true   # installed from the .pkg

if [[ "${1:-}" == "--remove-settings" ]]; then rm -rf "/Library/Application Support/WALLNut"; echo "Settings removed."
else echo "Kept your settings in /Library/Application Support/WALLNut (use --remove-settings to delete them)."; fi
echo "WALLNut has been removed."
