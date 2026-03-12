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
#   --device       Device ID (default: Pi hardware serial number)
#   --openai-key   OpenAI API key (for AI playlist generation)
#   --branch       Git branch to track (default: main)
#   --repo         Git repo URL (default: https://github.com/dazzletag/BCH.MediaButton.Backend.git)
#   --no-wizard    Skip the setup wizard after install
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
OPENAI_KEY=""
BRANCH="main"
REPO_URL="https://github.com/dazzletag/BCH.MediaButton.Backend.git"
RUN_WIZARD=1

while [[ $# -gt 0 ]]; do
  case "$1" in
    --api)        API_BASE="$2";    shift 2 ;;
    --device)     DEVICE_ID="$2";  shift 2 ;;
    --key)        DEVICE_KEY="$2"; shift 2 ;;
    --openai-key) OPENAI_KEY="$2"; shift 2 ;;
    --branch)     BRANCH="$2";     shift 2 ;;
    --repo)       REPO_URL="$2";   shift 2 ;;
    --no-wizard)  RUN_WIZARD=0;    shift   ;;
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
  2>/dev/null || true
success "System packages installed."

# ── 2. Create app user ────────────────────────────────────────────────────────
if ! id "$APP_USER" &>/dev/null; then
  info "Creating user '$APP_USER'..."
  useradd -m -s /bin/bash "$APP_USER"
  # Add to useful groups
  usermod -aG audio,video,bluetooth,input "$APP_USER" 2>/dev/null || true
  success "User '$APP_USER' created."
else
  info "User '$APP_USER' already exists."
fi

# ── 3. Clone or update repo ───────────────────────────────────────────────────
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

# ── 4. Python virtual environment & dependencies ──────────────────────────────
info "Creating Python venv at $VENV_DIR..."
python3 -m venv "$VENV_DIR"
info "Installing Python dependencies..."
"$VENV_DIR/bin/pip" install --upgrade pip -q
"$VENV_DIR/bin/pip" install -r "$INSTALL_DIR/publish/pi/requirements.txt" -q
chown -R "$APP_USER":"$APP_USER" "$VENV_DIR"
success "Python dependencies installed."

# ── 5. Write environment file ─────────────────────────────────────────────────
info "Writing environment file to $ENV_FILE..."
mkdir -p "$ENV_DIR"
chmod 700 "$ENV_DIR"

cat > "$ENV_FILE" <<EOF
# Media Button device configuration
# Generated by install.sh on $(date -u +"%Y-%m-%dT%H:%M:%SZ")

API_BASE=$API_BASE
DEVICE_ID=$DEVICE_ID
DEVICE_KEY=$DEVICE_KEY
EOF

if [[ -n "$OPENAI_KEY" ]]; then
  echo "OPENAI_API_KEY=$OPENAI_KEY" >> "$ENV_FILE"
fi

cat >> "$ENV_FILE" <<'EOF'

# Optional overrides — uncomment and set as needed:
# POLL_SECONDS=60
# DEFAULT_RADIO_URL=
# VOLUME_PERCENT=80
# YT_FORCE_IPV4=1
# YT_EXTRACTOR_ARGS=youtube:player_client=android
EOF

chmod 600 "$ENV_FILE"
success "Environment file written."

# ── 6. Install systemd service ────────────────────────────────────────────────
info "Installing systemd service..."

# Patch the service file's ExecStartPre to use the correct branch
cp "$SERVICE_SRC" "$SERVICE_DEST"
# Replace 'origin/main' with the chosen branch if different
if [[ "$BRANCH" != "main" ]]; then
  sed -i "s|origin/main|origin/$BRANCH|g" "$SERVICE_DEST"
fi

systemctl daemon-reload
systemctl enable "$SERVICE_NAME"
success "Service installed and enabled."

# ── 7. Create log file ────────────────────────────────────────────────────────
LOG_FILE="/var/log/media-button.log"
touch "$LOG_FILE"
chown "$APP_USER":"$APP_USER" "$LOG_FILE"

# ── 8. Summary ────────────────────────────────────────────────────────────────
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

# ── 9. Setup wizard ───────────────────────────────────────────────────────────
if [[ "$RUN_WIZARD" -eq 1 ]]; then
  info "Launching setup wizard (resident & beacon configuration)..."
  echo ""
  sudo -u "$APP_USER" "$VENV_DIR/bin/python3" "$INSTALL_DIR/publish/pi/setup_wizard.py" || \
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
