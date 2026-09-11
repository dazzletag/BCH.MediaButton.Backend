#!/usr/bin/env bash
# Install the parts of this repo that belong outside /opt/media-button.
#
# The unit refreshes itself with `git reset --hard origin/main` on every
# start, so anything under /opt/media-button reaches the whole fleet for
# free. Files that belong in /etc do not: only install.sh puts them there,
# and install.sh does not run on a restart. Without this script a logrotate
# config added to the repo would land in the checkout and never rotate
# anything, which is exactly what happened when it was first added.
#
# Invoked from the unit as root via "ExecStartPre=+...". The "+" prefix runs
# it with full privileges regardless of User=, which is needed to write to
# /etc.
#
# Two rules for anything added here:
#   - it must be idempotent, because it runs on every single start;
#   - it must never fail the start. A media button that will not play
#     because a config file could not be copied is a worse outcome than the
#     stale config. Hence no `set -e`, and an unconditional exit 0.

set -uo pipefail

SRC_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Copy only when the content differs, so a restart is silent in the log
# unless something actually changed.
install_if_changed() {
  local src="$1" dest="$2" mode="$3"
  if [[ ! -f "$src" ]]; then
    echo "[SYNC] Missing source, skipping: $src"
    return 0
  fi
  if cmp -s "$src" "$dest"; then
    return 0
  fi
  if install -m "$mode" "$src" "$dest"; then
    echo "[SYNC] Installed $dest"
  else
    echo "[SYNC] FAILED to install $dest (continuing anyway)"
  fi
}

install_if_changed "$SRC_DIR/media-button.logrotate" /etc/logrotate.d/media-button 0644

# ---------------------------------------------------------------------------
# Reverse SSH tunnel
#
# The unit cannot be a plain copy: every device forwards a different port, so
# it is rendered from reverse-tunnel.service.template. The port comes from
# TUNNEL_PORT in /etc/media-button/env; failing that it is adopted from the
# unit already installed, so devices set up by hand pick this up without any
# per-device change.
#
# Deliberately does NOT restart the tunnel. Whoever is watching this run is
# most likely connected *through* it, and bouncing it would cut them off
# mid-deploy. A changed unit takes effect on the next boot, or on a manual
# `systemctl restart reverse-tunnel` from the console.
# ---------------------------------------------------------------------------
TUNNEL_UNIT=/etc/systemd/system/reverse-tunnel.service
TUNNEL_TEMPLATE="$SRC_DIR/reverse-tunnel.service.template"

sync_reverse_tunnel() {
  [[ -f "$TUNNEL_TEMPLATE" ]] || { echo "[SYNC] No tunnel template, skipping"; return 0; }

  local port="${TUNNEL_PORT:-}"
  [[ -z "$port" && -r /etc/media-button/env ]] &&
    port="$(grep -E '^[[:space:]]*TUNNEL_PORT=' /etc/media-button/env 2>/dev/null | tail -1 | cut -d= -f2 | tr -d '\"[:space:]')"
  # Adopt the port from an existing hand-made unit.
  [[ -z "$port" && -f "$TUNNEL_UNIT" ]] &&
    port="$(grep -oE ' -R [0-9]+:localhost:22' "$TUNNEL_UNIT" 2>/dev/null | grep -oE '[0-9]+' | head -1)"

  if [[ -z "$port" ]]; then
    # No port anywhere: a device that has never had a tunnel. Leave it be —
    # setup-reverse-tunnel.sh is how a new device gets one.
    return 0
  fi
  if ! [[ "$port" =~ ^[0-9]+$ ]] || (( port < 1024 || port > 65535 )); then
    echo "[SYNC] Ignoring implausible TUNNEL_PORT '$port' — leaving the tunnel alone"
    return 0
  fi

  local app_user label rendered
  app_user="${TUNNEL_APP_USER:-dazzletag}"
  id "$app_user" >/dev/null 2>&1 || { echo "[SYNC] No user $app_user, skipping tunnel"; return 0; }
  label="${TUNNEL_LABEL:-}"
  [[ -z "$label" && -r /etc/media-button/env ]] &&
    label="$(grep -E '^[[:space:]]*TUNNEL_LABEL=' /etc/media-button/env 2>/dev/null | tail -1 | cut -d= -f2- | sed -E 's/^"(.*)"$//' | sed -E "s/^'(.*)'\$//")"
  label="${label:-$(hostname)}"

  rendered="$(mktemp)" || return 0
  sed -e "s|@@PORT@@|${port}|g" -e "s|@@APP_USER@@|${app_user}|g"       -e "s|@@LABEL@@|${label}|g" "$TUNNEL_TEMPLATE" > "$rendered"

  if cmp -s "$rendered" "$TUNNEL_UNIT"; then
    rm -f "$rendered"
    return 0
  fi
  if install -m 0644 "$rendered" "$TUNNEL_UNIT"; then
    systemctl daemon-reload 2>/dev/null || true
    echo "[SYNC] Installed $TUNNEL_UNIT (port ${port}); takes effect on next boot"
  else
    echo "[SYNC] FAILED to install $TUNNEL_UNIT (continuing anyway)"
  fi
  rm -f "$rendered"
}

sync_reverse_tunnel

# Deliberately NOT synced here: media-button.service itself. install.sh
# rewrites the hardcoded UID 1000 in it to match the real account, so
# copying the repo copy over the installed one would undo that on any
# device whose app user is not UID 1000.

exit 0
