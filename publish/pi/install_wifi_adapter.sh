#!/usr/bin/env bash
# install_wifi_adapter.sh
# Installs the RTL8821AU driver for the TP-Link Archer T2U Plus on Raspberry Pi OS.
#
# Run standalone:
#   sudo bash install_wifi_adapter.sh
#
# Or called automatically by install.sh when --wifi-adapter is passed.
set -euo pipefail

RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; CYAN='\033[0;36m'; NC='\033[0m'
info()    { echo -e "${CYAN}[wifi]${NC} $*"; }
success() { echo -e "${GREEN}[wifi]${NC} $*"; }
warn()    { echo -e "${YELLOW}[wifi]${NC} $*"; }
die()     { echo -e "${RED}[wifi] ERROR:${NC} $*" >&2; exit 1; }

[[ "$EUID" -eq 0 ]] || die "Please run with sudo: sudo bash install_wifi_adapter.sh"

# TP-Link Archer T2U Plus USB vendor:product ID
ADAPTER_USB_ID="2357:0120"
DRIVER_REPO="https://github.com/morrownr/8821au-20210708.git"
DRIVER_DIR="/tmp/rtl8821au-driver"

echo ""
echo -e "${CYAN}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
echo -e "${CYAN}  TP-Link Archer T2U Plus Driver Installer${NC}"
echo -e "${CYAN}  (RTL8821AU chipset)${NC}"
echo -e "${CYAN}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
echo ""

# ── 1. Detect adapter ─────────────────────────────────────────────────────────
if lsusb 2>/dev/null | grep -q "$ADAPTER_USB_ID"; then
  success "TP-Link Archer T2U Plus detected (USB $ADAPTER_USB_ID)."
else
  warn "Adapter not detected on USB (ID $ADAPTER_USB_ID)."
  warn "Make sure it is plugged in before rebooting. Continuing..."
fi

# ── 2. Check if driver is already loaded ─────────────────────────────────────
if lsmod 2>/dev/null | grep -q "8821au"; then
  success "RTL8821AU driver already loaded — nothing to do."
  echo ""
  exit 0
fi

# If DKMS module is already built, just load it (skip the build)
if dkms status 2>/dev/null | grep -qi "8821au"; then
  success "RTL8821AU DKMS module already installed — loading now..."
  modprobe 8821au && success "Driver loaded." || warn "modprobe failed — a reboot may be needed."
  echo ""
  exit 0
fi

# ── 3. Install build dependencies ─────────────────────────────────────────────
info "Updating package lists..."
apt-get update || die "apt-get update failed. Check the Pi's network connection."

info "Installing kernel headers and build tools..."
# Raspberry Pi OS ships raspberrypi-kernel-headers; standard Debian uses linux-headers-<version>
if apt-get install -y dkms git raspberrypi-kernel-headers; then
  success "Build dependencies and kernel headers installed."
else
  warn "raspberrypi-kernel-headers not available; trying linux-headers-$(uname -r)..."
  apt-get install -y dkms git build-essential bc "linux-headers-$(uname -r)" \
    || die "Could not install build dependencies. Run 'sudo apt-get install dkms git linux-headers-$(uname -r)' manually."
  success "Build dependencies and kernel headers installed (linux-headers-$(uname -r))."
fi

# ── 4. Clone driver source ────────────────────────────────────────────────────
info "Cloning RTL8821AU driver from ${DRIVER_REPO}..."
rm -rf "$DRIVER_DIR"
git clone --depth 1 "$DRIVER_REPO" "$DRIVER_DIR"

# ── 5. Build & install via DKMS ───────────────────────────────────────────────
info "Installing driver via DKMS (this may take a few minutes)..."
cd "$DRIVER_DIR"
# install-driver.sh accepts "NoPrompt" to skip interactive questions
bash ./install-driver.sh NoPrompt

success "RTL8821AU driver installed via DKMS."

# ── 6. Load the module now (no reboot needed) ─────────────────────────────────
modprobe 8821au && success "Driver loaded." || warn "modprobe failed — a reboot will load it."

# ── 7. Clean up temp files ────────────────────────────────────────────────────
cd /
rm -rf "$DRIVER_DIR"

echo ""
echo -e "${GREEN}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
echo -e "${GREEN}  Driver installation complete!${NC}"
echo -e "${GREEN}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
echo ""
echo "  The adapter should now be active (check with: ip link show)"
echo "  If it is not visible, a reboot will load it:  sudo reboot"
echo ""
