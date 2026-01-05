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
SKIP_RESTART="${SKIP_RESTART:-0}"
PY_BIN="${PY_BIN:-$REPO_DIR/.venv/bin/python3}"
PIP_BIN="${PIP_BIN:-$REPO_DIR/.venv/bin/pip3}"
# Fallbacks if venv not found
[ -x "$PY_BIN" ] || PY_BIN="$(command -v python3 || true)"
[ -x "$PIP_BIN" ] || PIP_BIN="$(command -v pip3 || true)"

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
if [ -f "$REPO_DIR/publish/pi/config.yaml" ] && [ -f "$SNAP" ] && [ -n "$PY_BIN" ]; then
  echo "[update] Merging local config values back into config.yaml..."
  "$PY_BIN" - <<'PY'
import sys, os
try:
    import yaml
except ImportError:
    print("[update] PyYAML not available; skipping config merge.")
    sys.exit(0)
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
if [ -n "$PIP_BIN" ]; then
  "$PIP_BIN" install -r publish/pi/requirements.txt --upgrade
else
  echo "[update] WARNING: pip3 not found; skipping dependency install."
fi

if [ "$SKIP_RESTART" = "1" ]; then
  echo "[update] SKIP_RESTART=1 set; not restarting service."
else
  echo "[update] Restarting service '$SERVICE'..."
  systemctl restart "$SERVICE"
fi

echo "[update] Done."
