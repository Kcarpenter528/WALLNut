#!/bin/sh
# Setup steps for the .deb and .rpm packages, run by the package manager:
#   configure  after install or upgrade: settings file, permissions, start the service
#   remove     before the package is removed: stop the service and tray app
# Installed as /opt/wallnut/package-setup.sh. Settings in /etc/wallnut are kept on upgrade and
# removal; "apt purge wallnut" deletes them.
set -e

CONFIG_DIR=/etc/wallnut
CONFIG_FILE=$CONFIG_DIR/config.json
has_systemd() { [ -d /run/systemd/system ] && command -v systemctl >/dev/null 2>&1; }

case "$1" in
  configure)
    chmod 755 /opt/wallnut/service/WallNut.Service
    [ -f /opt/wallnut/tray/WALLNut ] && chmod 755 /opt/wallnut/tray/WALLNut

    mkdir -p "$CONFIG_DIR/scripts"
    if [ ! -f "$CONFIG_FILE" ]; then
      cat > "$CONFIG_FILE" <<'EOF'
{
  // WALLNut settings. Add your UPS in the WALLNut app (Settings > UPS connections).
  // Every option is described in /opt/wallnut/config.example.json.
  "WallNut": {
    "Endpoints": {},
    "Shutdown": { "DryRun": true }
  }
}
EOF
      echo "WALLNut: created $CONFIG_FILE. Add your UPS in the WALLNut app: Settings > UPS connections."
    fi
    # Settings can hold passwords: root only. Scripts for actions: only root may change them.
    chown -R root:root "$CONFIG_DIR"
    chmod 700 "$CONFIG_DIR"; chmod 600 "$CONFIG_FILE"; chmod 755 "$CONFIG_DIR/scripts"

    # An earlier install from the .tar.gz put its own copy of the unit here, which would win.
    if [ -f /etc/systemd/system/wallnut.service ] && grep -q '/opt/wallnut/service' /etc/systemd/system/wallnut.service; then
      rm -f /etc/systemd/system/wallnut.service
    fi

    if has_systemd; then
      systemctl daemon-reload
      systemctl enable wallnut >/dev/null 2>&1
      systemctl restart wallnut
      echo "WALLNut: service running. Logs: journalctl -u wallnut -f"
    else
      echo "WALLNut: systemd isn't running here, so the service wasn't started."
    fi
    ;;

  remove)
    if has_systemd; then systemctl disable --now wallnut >/dev/null 2>&1 || true; fi
    pkill -x WALLNut 2>/dev/null || true
    ;;

  *)
    echo "usage: $0 configure|remove" >&2
    exit 2
    ;;
esac
exit 0
