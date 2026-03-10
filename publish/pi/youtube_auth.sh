#!/usr/bin/env bash
# One-time YouTube OAuth2 authorisation for the media-button service.
#
# Run this ONCE on the Pi to link a Google account to yt-dlp.
# After it completes, set YT_OAUTH=1 in /etc/media-button/env and restart.
#
#   sudo /opt/media-button/publish/pi/youtube_auth.sh
#
# The OAuth token is saved in the yt-dlp cache and refreshes automatically —
# no manual re-export ever needed.

set -euo pipefail

REPO_DIR="${REPO_DIR:-/opt/media-button}"
YT_DLP="${REPO_DIR}/.venv/bin/yt-dlp"

if [ ! -x "$YT_DLP" ]; then
  echo "[ERROR] yt-dlp not found at $YT_DLP"
  echo "        Run update.sh first to install dependencies."
  exit 1
fi

echo ""
echo "╔══════════════════════════════════════════════╗"
echo "║   BCH Media Button — YouTube Authorisation   ║"
echo "╚══════════════════════════════════════════════╝"
echo ""
echo "yt-dlp will now start the Google device authorisation flow."
echo "You will see a short URL and a code — open the URL on your"
echo "phone or computer, sign in to a Google account, and enter"
echo "the code. The Pi will detect approval automatically."
echo ""

"$YT_DLP" \
  --username "oauth2" \
  --password "" \
  --no-download \
  "https://www.youtube.com/watch?v=jNQXAC9IVRw"

echo ""
echo "✓ Authorisation complete. Token saved to yt-dlp cache."
echo ""
echo "Next steps:"
echo "  1. Add this line to /etc/media-button/env:"
echo "       YT_OAUTH=1"
echo ""
echo "  2. Restart the service:"
echo "       sudo systemctl restart media-button"
echo ""
