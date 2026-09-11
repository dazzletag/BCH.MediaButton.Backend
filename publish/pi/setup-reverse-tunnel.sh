#!/usr/bin/env bash
# One-time bootstrap of the reverse SSH tunnel on a NEW media button Pi.
#
#   sudo bash setup-reverse-tunnel.sh --port 2227 [--label "field-2"]
#
# Afterwards sync-system-files.sh keeps the unit in step with the repo on
# every service start; this script only has to run once per device.
#
# What it does:
#   1. installs autossh
#   2. generates /home/<user>/.ssh/tunnel_key if absent
#   3. records TUNNEL_PORT in /etc/media-button/env so the sync can render
#      the unit for this device
#   4. accepts the VM host key AS THE APP USER — autossh otherwise sits in a
#      retry loop forever despite StrictHostKeyChecking=accept-new, which is
#      exactly how the Field House device stalled
#   5. renders and starts the unit
#
# Two things still have to happen outside this script:
#   - the printed public key must be added to the VM's authorized_keys
#     (C:\Users\field-nursecall\.ssh\authorized_keys, needs an ADMIN
#      PowerShell — the field-nursecall account cannot write it)
#   - the Pi's own SSH *server* must be enabled, which Raspberry Pi OS
#     disables by default. This script enables it.

set -uo pipefail

PORT=""; LABEL=""; APP_USER="${TUNNEL_APP_USER:-dazzletag}"
VM_USER="field-nursecall"; VM_HOST="bch-app.uksouth.cloudapp.azure.com"
ENV_FILE=/etc/media-button/env

while [[ $# -gt 0 ]]; do
  case "$1" in
    --port)  PORT="$2"; shift 2 ;;
    --label) LABEL="$2"; shift 2 ;;
    --user)  APP_USER="$2"; shift 2 ;;
    *) echo "Unknown argument: $1" >&2; exit 1 ;;
  esac
done

[[ $EUID -eq 0 ]] || { echo "Run with sudo." >&2; exit 1; }
[[ "$PORT" =~ ^[0-9]+$ ]] || { echo "--port is required and must be numeric." >&2; exit 1; }
id "$APP_USER" >/dev/null 2>&1 || { echo "No such user: $APP_USER" >&2; exit 1; }
LABEL="${LABEL:-$(hostname)}"
KEY="/home/${APP_USER}/.ssh/tunnel_key"

echo "==> Installing autossh"
apt-get update -qq && apt-get install -y -qq autossh

echo "==> Enabling the SSH server (off by default on Raspberry Pi OS)"
systemctl enable --now ssh >/dev/null 2>&1
ss -tln | grep -q ':22 ' && echo "    sshd listening" || echo "    WARNING: sshd not listening — the tunnel will forward to nothing"

echo "==> Tunnel key"
install -d -m 700 -o "$APP_USER" -g "$APP_USER" "/home/${APP_USER}/.ssh"
if [[ -f "$KEY" ]]; then echo "    already present"
else sudo -u "$APP_USER" ssh-keygen -t ed25519 -f "$KEY" -N "" -C "${LABEL}-tunnel"; fi

echo "==> Recording TUNNEL_PORT=${PORT} in ${ENV_FILE}"
install -d -m 755 "$(dirname "$ENV_FILE")"; touch "$ENV_FILE"
if grep -qE '^[[:space:]]*TUNNEL_PORT=' "$ENV_FILE"; then
  sed -i -E "s|^[[:space:]]*TUNNEL_PORT=.*|TUNNEL_PORT=${PORT}|" "$ENV_FILE"
else
  printf '\n# Reverse tunnel port for this device (see sync-system-files.sh)\nTUNNEL_PORT=%s\n' "$PORT" >> "$ENV_FILE"
fi
# Quoted deliberately: the label contains spaces, and this file is sourced
# by shell as well as read by systemd. Unquoted, `. /etc/media-button/env`
# dies with "House: command not found" and skips every later line.
if grep -qE '^[[:space:]]*TUNNEL_LABEL=' "$ENV_FILE"; then
  sed -i -E "s|^[[:space:]]*TUNNEL_LABEL=.*|TUNNEL_LABEL=\"${LABEL}\"|" "$ENV_FILE"
else
  printf 'TUNNEL_LABEL="%s"
' "$LABEL" >> "$ENV_FILE"
fi

echo "==> Accepting the VM host key as ${APP_USER}"
sudo -u "$APP_USER" ssh -o BatchMode=yes -o StrictHostKeyChecking=accept-new -o ConnectTimeout=15 \
  -o IdentitiesOnly=yes -i "$KEY" "${VM_USER}@${VM_HOST}" "echo TUNNEL_AUTH_OK" 2>&1 |
  grep -q TUNNEL_AUTH_OK && echo "    authenticated" ||
  echo "    not authenticated yet — expected until the key below is added to the VM"

echo "==> Rendering and starting the unit"
"$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/sync-system-files.sh"
systemctl enable --now reverse-tunnel >/dev/null 2>&1
sleep 3
systemctl is-active reverse-tunnel >/dev/null && echo "    running" || echo "    not running (journalctl -u reverse-tunnel -n 20)"

cat <<BANNER

================================================================
 Add this key to the VM, in an ADMIN PowerShell:

$(cat "${KEY}.pub")

 \$p = 'C:\Users\field-nursecall\.ssh\authorized_keys'
 \$lines = @(Get-Content \$p)
 Set-Content -Path \$p -Value (\$lines + '<the line above>') -Encoding ascii

 Then from the laptop:  ssh -p ${PORT} via the bchvm ProxyJump
================================================================
BANNER
