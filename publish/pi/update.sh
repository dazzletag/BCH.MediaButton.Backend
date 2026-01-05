#!/usr/bin/env bash
set -euo pipefail

# Update helper for the Pi. Pulls latest from the configured branch (default: main)
# and restarts the media-button systemd service.
#
# Usage:
#   sudo /opt/media-button/publish/pi/update.sh          # pull + restart
#   sudo BRANCH=release-2025-01 /opt/media-button/publish/pi/update.sh
#
# Place this repo at /opt/media-button on the Pi for the paths below.

BRANCH="${BRANCH:-main}"
REPO_DIR="${REPO_DIR:-/opt/media-button}"
SERVICE="${SERVICE:-media-button}"

cd "$REPO_DIR"
SNAP="$(mktemp -t config.yaml.XXXXXX)"
export SNAP REPO_DIR
if [ -f "$REPO_DIR/publish/pi/config.yaml" ]; then
  cp "$REPO_DIR/publish/pi/config.yaml" "$SNAP"
fi
echo "[update] Fetching latest for branch '$BRANCH'..."
git fetch origin "$BRANCH"
echo "[update] Resetting to origin/$BRANCH..."
git reset --hard "origin/$BRANCH"

# Restore config values for existing keys (preserve local overrides across resets)
if [ -f "$REPO_DIR/publish/pi/config.yaml" ] && [ -f "$SNAP" ]; then
  echo "[update] Merging local config values back into config.yaml..."
  python3 - <<'PY'
import sys, yaml, os
new_path = os.path.join(os.environ["REPO_DIR"], "publish/pi/config.yaml")
snap_path = os.path.join(os.environ["SNAP"])
with open(new_path, "r", encoding="utf-8") as f:
    new_cfg = yaml.safe_load(f) or {}
with open(snap_path, "r", encoding="utf-8") as f:
    old_cfg = yaml.safe_load(f) or {}

def merge(old, new):
    if isinstance(old, dict) and isinstance(new, dict):
        for k, old_v in old.items():
            if k in new:
                new[k] = merge(old_v, new[k])
        return new
    # If key existed before, keep its previous value
    return old

merged = merge(old_cfg, new_cfg)
with open(new_path, "w", encoding="utf-8") as f:
    yaml.safe_dump(merged, f, sort_keys=False)
print("[update] Config merged.")
PY
else
  echo "[update] No config merge needed."
fi

echo "[update] Installing dependencies..."
if command -v python3 >/dev/null 2>&1 && command -v pip3 >/dev/null 2>&1; then
  pip3 install -r publish/pi/requirements.txt --upgrade
else
  echo "[update] WARNING: python3/pip3 not found; skipping dependency install."
fi

echo "[update] Restarting service '$SERVICE'..."
systemctl restart "$SERVICE"

echo "[update] Done."
