#!/usr/bin/env bash
# Removes WALLNut. Keeps /etc/wallnut unless --remove-settings.  sudo ./uninstall.sh [--remove-settings]
set -euo pipefail
[[ $EUID -eq 0 ]] || { echo "Please run with sudo." >&2; exit 1; }

systemctl disable --now wallnut 2>/dev/null || true
rm -f /etc/systemd/system/wallnut.service
systemctl daemon-reload
pkill -x WALLNut 2>/dev/null || true
rm -f /etc/xdg/autostart/wallnut-tray.desktop /usr/share/applications/wallnut.desktop
rm -rf /opt/wallnut

if [[ "${1:-}" == "--remove-settings" ]]; then rm -rf /etc/wallnut; echo "Settings removed."
else echo "Kept your settings in /etc/wallnut (use --remove-settings to delete them)."; fi
echo "WALLNut has been removed."
