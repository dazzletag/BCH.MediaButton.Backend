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

# Deliberately NOT synced here: media-button.service itself. install.sh
# rewrites the hardcoded UID 1000 in it to match the real account, so
# copying the repo copy over the installed one would undo that on any
# device whose app user is not UID 1000.

exit 0
