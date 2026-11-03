#!/usr/bin/env bash
# Installs (or upgrades) WALLNut on macOS: the service (a LaunchDaemon) and the tray app (a LaunchAgent).
#   sudo ./install.sh [--nas-host 192.168.1.10] [--source-dir DIR] [--no-tray]
# Settings in /Library/Application Support/WALLNut/config.json are kept on upgrade.
set -euo pipefail

INSTALL_DIR=/usr/local/wallnut
CONFIG_DIR="/Library/Application Support/WALLNut"
CONFIG_FILE="$CONFIG_DIR/config.json"
DAEMON=/Library/LaunchDaemons/wallnut.service.plist
AGENT=/Library/LaunchAgents/wallnut.tray.plist
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$HERE/../.." && pwd)"
# In a release download the files sit next to this script; in the repository they're elsewhere.
pick() { for f in "$@"; do [[ -e "$f" ]] && { echo "$f"; return; }; done; echo "$1"; }
EXAMPLE_CONFIG="$(pick "$HERE/config.example.json" "$REPO_ROOT/packaging/config.example.json")"
SOURCE_DIR="" NAS_HOST="" NO_TRAY=0 UPS_NAME=ups USERNAME=monuser PASSWORD=secret ROLE=PowersThisMachine

while [[ $# -gt 0 ]]; do
  case "$1" in
    --source-dir) SOURCE_DIR="$2"; shift 2 ;;
    --nas-host) NAS_HOST="$2"; shift 2 ;;
    --ups) UPS_NAME="$2"; shift 2 ;;
    --username) USERNAME="$2"; shift 2 ;;
    --password) PASSWORD="$2"; shift 2 ;;
    --role) ROLE="$2"; shift 2 ;;
    --no-tray) NO_TRAY=1; shift ;;
    -h|--help) sed -n '2,4p' "$0"; exit 0 ;;
    *) echo "Unknown option $1" >&2; exit 2 ;;
  esac
done

step() { printf '\n==> %s\n' "$1"; }
[[ $EUID -eq 0 ]] || { echo "Please run with sudo." >&2; exit 1; }
case "$(uname -m)" in arm64) RID=osx-arm64 ;; x86_64) RID=osx-x64 ;; *) echo "Unsupported CPU" >&2; exit 1 ;; esac

if [[ -z "$SOURCE_DIR" && -x "$HERE/service/WallNut.Service" ]]; then SOURCE_DIR="$HERE"; fi  # release download
if [[ -z "$SOURCE_DIR" ]]; then
  step "Building WALLNut ($RID, self-contained)"
  command -v dotnet >/dev/null || { echo "The .NET SDK is needed to build (or pass --source-dir)." >&2; exit 1; }
  SOURCE_DIR="$(mktemp -d)"
  common=(-c Release -r "$RID" --self-contained -p:PublishSingleFile=true -p:DebugType=none --artifacts-path "$SOURCE_DIR/artifacts" --nologo -v quiet)
  dotnet publish "$REPO_ROOT/src/WallNut.Service" "${common[@]}" -o "$SOURCE_DIR/service"
  [[ $NO_TRAY -eq 1 ]] || dotnet publish "$REPO_ROOT/src/WallNut.App" "${common[@]}" -o "$SOURCE_DIR/tray"
fi

step "Stopping anything running"
launchctl bootout system "$DAEMON" 2>/dev/null || true
pkill -x WALLNut 2>/dev/null || true

step "Installing to $INSTALL_DIR"
mkdir -p "$INSTALL_DIR" /Library/Logs/WALLNut
rm -rf "$INSTALL_DIR/service"; cp -R "$SOURCE_DIR/service" "$INSTALL_DIR/service"
[[ $NO_TRAY -eq 0 && -d "$SOURCE_DIR/tray" ]] && { rm -rf "$INSTALL_DIR/tray"; cp -R "$SOURCE_DIR/tray" "$INSTALL_DIR/tray"; }
chmod 755 "$INSTALL_DIR/service/WallNut.Service"; [[ -f "$INSTALL_DIR/tray/WALLNut" ]] && chmod 755 "$INSTALL_DIR/tray/WALLNut"
cp "$EXAMPLE_CONFIG" "$INSTALL_DIR/config.example.json"
cp "$HERE/uninstall.sh" "$INSTALL_DIR/uninstall.sh"; chmod 755 "$INSTALL_DIR/uninstall.sh"
# Downloaded builds are quarantined by Gatekeeper (they aren't signed by Apple yet); the user chose to install them.
xattr -dr com.apple.quarantine "$INSTALL_DIR" 2>/dev/null || true

step "Settings: $CONFIG_FILE"
mkdir -p "$CONFIG_DIR" "$CONFIG_DIR/scripts"
chown root "$CONFIG_DIR/scripts"; chmod 755 "$CONFIG_DIR/scripts"   # scripts for actions: root-only to change
if [[ -f "$CONFIG_FILE" ]]; then
  echo "    Keeping your existing settings."
elif [[ -n "$NAS_HOST" ]]; then
  cat > "$CONFIG_FILE" <<EOF
{
  "WallNut": {
    "Endpoints": {
      "nas": { "DisplayName": "NAS UPS", "Host": "$NAS_HOST", "Port": 3493, "Ups": "$UPS_NAME",
               "Username": "$USERNAME", "Password": "$PASSWORD", "Role": "$ROLE" }
    },
    "Shutdown": { "DryRun": true }
  }
}
EOF
  echo "    Created, watching $UPS_NAME@$NAS_HOST ($ROLE). Shutdown is in TEST MODE."
else
  cat > "$CONFIG_FILE" <<'EOF'
{
  // WALLNut settings. Add your UPS in the WALLNut app (Settings > UPS connections).
  // Every option is described in config.example.json in the program folder.
  "WallNut": {
    "Endpoints": {},
    "Shutdown": { "DryRun": true }
  }
}
EOF
  echo "    Created. Add your UPS in the WALLNut app: Settings > UPS connections."
fi
chown -R root:wheel "$CONFIG_DIR"; chmod 700 "$CONFIG_DIR"; chmod 600 "$CONFIG_FILE"  # may hold passwords

step "Checking settings and UPS connections"
set +e; (cd "$INSTALL_DIR/service" && ./WallNut.Service --check); code=$?; set -e
[[ $code -eq 1 ]] && echo "    Fix $CONFIG_FILE, then: sudo launchctl kickstart -k system/wallnut.service"
[[ $code -eq 2 ]] && echo "    A UPS could not be reached; the service will keep retrying."

step "Starting the service"
# The socket directory for the tray app.
mkdir -p /var/run/wallnut; chmod 755 /var/run/wallnut
install -m 644 -o root -g wheel "$HERE/wallnut.service.plist" "$DAEMON"
launchctl bootstrap system "$DAEMON"
echo "    Running (starts at boot, restarts if it stops)."

if [[ $NO_TRAY -eq 0 && -d "$INSTALL_DIR/tray" ]]; then
  step "Setting up the tray app"
  install -m 644 -o root -g wheel "$HERE/wallnut.tray.plist" "$AGENT"
  console_user="$(stat -f %Su /dev/console)"
  if [[ -n "$console_user" && "$console_user" != "root" ]]; then
    launchctl bootstrap "gui/$(id -u "$console_user")" "$AGENT" 2>/dev/null || true
  fi
  echo "    Starts for every user at login."
fi

step "Done"
echo "    Program:   $INSTALL_DIR"
echo "    Settings:  $CONFIG_FILE"
echo "    Logs:      /Library/Logs/WALLNut/service.log"
echo "    Uninstall: sudo $INSTALL_DIR/uninstall.sh"
grep -Eq '"DryRun"[[:space:]]*:[[:space:]]*true' "$CONFIG_FILE" && \
  echo -e "\n    Shutdown is in TEST MODE. Rehearse an outage, then set \"DryRun\": false and reinstall."
exit 0
