#!/usr/bin/env bash
# Media Button Pi installer
# Usage:
#   curl -sSL <url>/install.sh | sudo bash -s -- --api URL --key KEY
#   sudo bash install.sh --api https://bch-media.azurewebsites.net --key "abc123..."
#
# Required:
#   --api      API base URL (no trailing slash)
#   --key      Device secret key
#
# Optional:
#   --device          Device ID (default: Pi hardware serial number)
#   --branch          Git branch to track (default: main)
#   --repo            Git repo URL (default: https://github.com/dazzletag/BCH.MediaButton.Backend.git)
#   --no-wizard       Skip the setup wizard after install
#   --tenant-id       Microsoft Entra tenant ID (for SharePoint/OneDrive spreadsheet sync)
#   --client-id       Azure app client ID (for SharePoint/OneDrive spreadsheet sync)
#   --client-secret   Azure app client secret (for SharePoint/OneDrive spreadsheet sync)
#   --drive-id        OneDrive drive ID containing the survey spreadsheet
#   --item-id         OneDrive item ID of the survey spreadsheet (preferred over --item-path)
#   --item-path       OneDrive path to the survey spreadsheet (e.g. /BCH/survey.xlsx)
set -euo pipefail

# ── Colours ──────────────────────────────────────────────────────────────────
RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; CYAN='\033[0;36m'; NC='\033[0m'
info()    { echo -e "${CYAN}[install]${NC} $*"; }
success() { echo -e "${GREEN}[install]${NC} $*"; }
warn()    { echo -e "${YELLOW}[install]${NC} $*"; }
die()     { echo -e "${RED}[install] ERROR:${NC} $*" >&2; exit 1; }

# ── Argument parsing ──────────────────────────────────────────────────────────
API_BASE=""
DEVICE_ID=""
DEVICE_KEY=""
BRANCH="main"
REPO_URL="https://github.com/dazzletag/BCH.MediaButton.Backend.git"
RUN_WIZARD=1
TENANT_ID=""
CLIENT_ID=""
CLIENT_SECRET=""
DRIVE_ID=""
ITEM_ID=""
ITEM_PATH=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --api)           API_BASE="$2";      shift 2 ;;
    --device)        DEVICE_ID="$2";     shift 2 ;;
    --key)           DEVICE_KEY="$2";    shift 2 ;;
    --branch)        BRANCH="$2";        shift 2 ;;
    --repo)          REPO_URL="$2";      shift 2 ;;
    --no-wizard)     RUN_WIZARD=0;       shift   ;;
    --tenant-id)     TENANT_ID="$2";     shift 2 ;;
    --client-id)     CLIENT_ID="$2";     shift 2 ;;
    --client-secret) CLIENT_SECRET="$2"; shift 2 ;;
    --drive-id)      DRIVE_ID="$2";      shift 2 ;;
    --item-id)       ITEM_ID="$2";       shift 2 ;;
    --item-path)     ITEM_PATH="$2";     shift 2 ;;
    # legacy / ignored
    --anthropic-key|--openai-key) shift 2 ;;
    *) die "Unknown argument: $1" ;;
  esac
done

[[ -n "$API_BASE"   ]] || die "--api is required"
[[ -n "$DEVICE_KEY" ]] || die "--key is required"

# Strip trailing slash from API base
API_BASE="${API_BASE%/}"

# ── Auto-detect Pi serial if --device not provided ────────────────────────────
if [[ -z "$DEVICE_ID" ]]; then
  # Try device tree first (most reliable on Pi), then cpuinfo
  if [[ -f /sys/firmware/devicetree/base/serial-number ]]; then
    DEVICE_ID="$(tr -d '\0' < /sys/firmware/devicetree/base/serial-number)"
  else
    DEVICE_ID="$(grep -m1 '^Serial' /proc/cpuinfo 2>/dev/null | awk '{print $3}' || true)"
  fi
  # Strip leading zeros for a cleaner ID
  DEVICE_ID="${DEVICE_ID#"${DEVICE_ID%%[!0]*}"}"
  [[ -n "$DEVICE_ID" ]] || die "Could not detect Pi serial number. Provide --device manually."
  info "Auto-detected device ID from hardware serial: $DEVICE_ID"
fi

# ── Must run as root ──────────────────────────────────────────────────────────
[[ "$EUID" -eq 0 ]] || die "Please run with sudo: sudo bash install.sh ..."

# ── Paths & constants ─────────────────────────────────────────────────────────
INSTALL_DIR="/opt/media-button"
VENV_DIR="$INSTALL_DIR/.venv"
ENV_DIR="/etc/media-button"
ENV_FILE="$ENV_DIR/env"
SERVICE_SRC="$INSTALL_DIR/publish/pi/media-button.service"
SERVICE_DEST="/etc/systemd/system/media-button.service"
SERVICE_NAME="media-button"
APP_USER="dazzletag"

echo ""
echo -e "${CYAN}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
echo -e "${CYAN}  Media Button Pi Installer${NC}"
echo -e "${CYAN}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
echo ""
info "Device ID : $DEVICE_ID"
info "API base  : $API_BASE"
info "Branch    : $BRANCH"
echo ""

# ── 1. System packages ────────────────────────────────────────────────────────
# Remove any stale FLIRC apt sources from a previous failed run so they
# don't pollute this apt-get update.
rm -f /etc/apt/sources.list.d/flirc.list /usr/share/keyrings/flirc-archive-keyring.gpg

info "Updating apt and installing system packages..."
apt-get update -qq
apt-get install -y --no-install-recommends \
  git \
  python3 \
  python3-venv \
  python3-pip \
  python3-tk \
  vlc \
  bluetooth \
  bluez \
  libatlas-base-dev \
  libopenblas-dev \
  libtk8.6 \
  curl \
  gnupg \
  2>/dev/null || true
success "System packages installed."

# ── 2. FLIRC ──────────────────────────────────────────────────────────────────
info "Installing FLIRC software..."
if ! command -v flirc_util &>/dev/null; then
  _flirc_ok=0

  # Gemfury repo — supports armhf, arm64 and amd64 including Raspberry Pi OS
  echo "deb [trusted=yes] https://apt.fury.io/flirc/ /" \
    > /etc/apt/sources.list.d/flirc.list
  if apt-get update -qq 2>/dev/null; then
    apt-get install -y --no-install-recommends flirc 2>/dev/null && _flirc_ok=1 || true
  else
    rm -f /etc/apt/sources.list.d/flirc.list
  fi

  if [ "$_flirc_ok" -eq 1 ] && command -v flirc_util &>/dev/null; then
    success "FLIRC installed."
  else
    warn "FLIRC could not be installed automatically — the wizard will prompt you if you choose remote control mode."
  fi
else
  success "FLIRC already installed."
fi

# ── 3. WiFi adapter driver (TP-Link Archer T2U Plus / RTL8821AU) ──────────────
# Auto-detect the adapter and install the driver now if it's plugged in.
# Regardless, install the wifi systemd service so the driver is installed
# automatically on any future boot where the adapter is present.
_T2U_USB_ID="2357:0120"
if lsusb 2>/dev/null | grep -q "$_T2U_USB_ID"; then
  info "TP-Link Archer T2U Plus detected — installing RTL8821AU driver now..."
  WIFI_SCRIPT="$( cd "$(dirname "${BASH_SOURCE[0]:-install.sh}")" 2>/dev/null && pwd )/install_wifi_adapter.sh"
  if [[ -f "$WIFI_SCRIPT" ]]; then
    bash "$WIFI_SCRIPT"
  else
    _wifi_tmp="$(mktemp /tmp/install_wifi_adapter.XXXXXX.sh)"
    curl -sSL "https://raw.githubusercontent.com/dazzletag/BCH.MediaButton.Backend/$BRANCH/publish/pi/install_wifi_adapter.sh" \
      -o "$_wifi_tmp"
    bash "$_wifi_tmp"
    rm -f "$_wifi_tmp"
  fi
  success "RTL8821AU driver installed."
else
  info "TP-Link Archer T2U Plus not detected now — driver will be installed automatically on first boot with the adapter plugged in."
fi

# ── 4. Create app user ────────────────────────────────────────────────────────
if ! id "$APP_USER" &>/dev/null; then
  info "Creating user '$APP_USER'..."
  useradd -m -s /bin/bash "$APP_USER"
  # Add to useful groups
  usermod -aG audio,video,bluetooth,input "$APP_USER" 2>/dev/null || true
  success "User '$APP_USER' created."
else
  info "User '$APP_USER' already exists."
fi

# Enable linger so systemd user services (PipeWire etc.) survive without an active login session
loginctl enable-linger "$APP_USER" 2>/dev/null || true

# ── 4. Clone or update repo ───────────────────────────────────────────────────
if [[ -d "$INSTALL_DIR/.git" ]]; then
  info "Repo already present at $INSTALL_DIR — updating to branch '$BRANCH'..."
  git -C "$INSTALL_DIR" fetch origin "$BRANCH"
  git -C "$INSTALL_DIR" reset --hard "origin/$BRANCH"
else
  info "Cloning repo to $INSTALL_DIR..."
  git clone --branch "$BRANCH" --depth 1 "$REPO_URL" "$INSTALL_DIR"
fi
chown -R "$APP_USER":"$APP_USER" "$INSTALL_DIR"
success "Repo ready at $INSTALL_DIR."

# ── 5. Python virtual environment & dependencies ──────────────────────────────
info "Creating Python venv at $VENV_DIR..."
python3 -m venv "$VENV_DIR"
info "Installing Python dependencies..."
"$VENV_DIR/bin/pip" install --upgrade pip -q
"$VENV_DIR/bin/pip" install -r "$INSTALL_DIR/publish/pi/requirements.txt" -q
chown -R "$APP_USER":"$APP_USER" "$VENV_DIR"
success "Python dependencies installed."

# ── 6. Write environment file ─────────────────────────────────────────────────
info "Writing environment file to $ENV_FILE..."
mkdir -p "$ENV_DIR"
chown "$APP_USER":"$APP_USER" "$ENV_DIR"
chmod 700 "$ENV_DIR"

cat > "$ENV_FILE" <<EOF
# Media Button device configuration
# Generated by install.sh on $(date -u +"%Y-%m-%dT%H:%M:%SZ")

API_BASE=$API_BASE
DEVICE_ID=$DEVICE_ID
DEVICE_KEY=$DEVICE_KEY
EOF

# SharePoint / OneDrive credentials (for survey spreadsheet sync)
if [[ -n "$TENANT_ID" ]];     then echo "TENANT_ID=$TENANT_ID"         >> "$ENV_FILE"; fi
if [[ -n "$CLIENT_ID" ]];     then echo "CLIENT_ID=$CLIENT_ID"         >> "$ENV_FILE"; fi
if [[ -n "$CLIENT_SECRET" ]]; then echo "CLIENT_SECRET=$CLIENT_SECRET" >> "$ENV_FILE"; fi
if [[ -n "$DRIVE_ID" ]];      then echo "DRIVE_ID=$DRIVE_ID"           >> "$ENV_FILE"; fi
if [[ -n "$ITEM_ID" ]];       then echo "ITEM_ID=$ITEM_ID"             >> "$ENV_FILE"; fi
if [[ -n "$ITEM_PATH" ]];     then echo "ITEM_PATH=$ITEM_PATH"         >> "$ENV_FILE"; fi

cat >> "$ENV_FILE" <<'EOF'

# Optional overrides — uncomment and set as needed:
# POLL_SECONDS=60
# DEFAULT_RADIO_URL=
# VOLUME_PERCENT=80
# YT_FORCE_IPV4=1
# YT_EXTRACTOR_ARGS=youtube:player_client=android
# STABILITY_API_KEY=      # Stability AI key for AI-generated resident logos
EOF

chown "$APP_USER":"$APP_USER" "$ENV_FILE"
chmod 600 "$ENV_FILE"
success "Environment file written."

# ── 6b. Seed config.yaml (outside the git repo so git reset --hard can't wipe it) ──
CONFIG_FILE="$ENV_DIR/config.yaml"
if [[ ! -f "$CONFIG_FILE" ]]; then
  info "Seeding config file from template..."
  cp "$INSTALL_DIR/publish/pi/config.yaml" "$CONFIG_FILE"
  chown "$APP_USER":"$APP_USER" "$CONFIG_FILE"
  chmod 600 "$CONFIG_FILE"
  success "Config file seeded at $CONFIG_FILE."
else
  info "Config file already exists at $CONFIG_FILE — preserving."
fi

# ── 7. Install systemd services ───────────────────────────────────────────────
info "Installing systemd services..."

# WiFi adapter one-shot service (runs as root before the main service)
WIFI_SERVICE_SRC="$INSTALL_DIR/publish/pi/media-button-wifi.service"
WIFI_SERVICE_DEST="/etc/systemd/system/media-button-wifi.service"
cp "$WIFI_SERVICE_SRC" "$WIFI_SERVICE_DEST"

# Main app service
# Patch the service file to use the correct branch and actual user UID
cp "$SERVICE_SRC" "$SERVICE_DEST"
if [[ "$BRANCH" != "main" ]]; then
  sed -i "s|origin/main|origin/$BRANCH|g" "$SERVICE_DEST"
fi
# Replace hardcoded UID 1000 with the real UID of $APP_USER (in case it differs)
_app_uid=$(id -u "$APP_USER")
if [[ "$_app_uid" != "1000" ]]; then
  sed -i "s|/run/user/1000|/run/user/$_app_uid|g" "$SERVICE_DEST"
fi

systemctl daemon-reload
systemctl enable media-button-wifi
systemctl enable "$SERVICE_NAME"
success "Services installed and enabled."

# ── 8. Create log file + rotation ─────────────────────────────────────────────
LOG_FILE="/var/log/media-button.log"
touch "$LOG_FILE"
chown "$APP_USER":"$APP_USER" "$LOG_FILE"

# Without rotation this log grows unbounded (one device reached 170 MB).
install -m 0644 "$INSTALL_DIR/publish/pi/media-button.logrotate"         /etc/logrotate.d/media-button
success "Log rotation installed (daily, 7 kept, rotated early past 50M)."

# ── 9. Summary ────────────────────────────────────────────────────────────────
echo ""
echo -e "${GREEN}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
echo -e "${GREEN}  Installation complete!${NC}"
echo -e "${GREEN}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
echo ""
echo "  Device ID : $DEVICE_ID"
echo "  API base  : $API_BASE"
echo "  Config    : $ENV_FILE"
echo "  Logs      : $LOG_FILE"
echo ""

# ── 10. Setup wizard ──────────────────────────────────────────────────────────
if [[ "$RUN_WIZARD" -eq 1 ]]; then
  info "Launching setup wizard (resident & beacon configuration)..."
  echo ""
  # Redirect stdin from /dev/tty so the wizard can read keyboard input even
  # when the installer itself was piped from curl.
  sudo -u "$APP_USER" "$VENV_DIR/bin/python3" "$INSTALL_DIR/publish/pi/setup_wizard.py" \
    </dev/tty || \
    warn "Setup wizard exited with an error. You can re-run it later:"  \
         "  sudo -u $APP_USER $VENV_DIR/bin/python3 $INSTALL_DIR/publish/pi/setup_wizard.py"
else
  echo -e "${YELLOW}  Setup wizard skipped.${NC} Run it when ready:"
  echo "    sudo -u $APP_USER $VENV_DIR/bin/python3 $INSTALL_DIR/publish/pi/setup_wizard.py"
fi

echo ""
echo "  To start the service now:"
echo "    sudo systemctl start $SERVICE_NAME"
echo ""
echo "  To watch the logs:"
echo "    sudo journalctl -u $SERVICE_NAME -f"
echo "    tail -f $LOG_FILE"
echo ""
