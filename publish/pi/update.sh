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
echo "[update] Fetching latest for branch '$BRANCH'..."
git fetch origin "$BRANCH"
echo "[update] Resetting to origin/$BRANCH..."
git reset --hard "origin/$BRANCH"

echo "[update] Installing dependencies..."
if command -v python3 >/dev/null 2>&1 && command -v pip3 >/dev/null 2>&1; then
  pip3 install -r publish/pi/requirements.txt --upgrade
else
  echo "[update] WARNING: python3/pip3 not found; skipping dependency install."
fi

echo "[update] Restarting service '$SERVICE'..."
systemctl restart "$SERVICE"

echo "[update] Done."
