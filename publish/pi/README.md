# Media Button Pi deployment

Suggested layout on the Pi:

```
/opt/media-button          # git clone of this repo
  publish/pi/media_button_pi.py
  publish/pi/ui_display.py
  publish/pi/requirements.txt
```

## Systemd service

1) Copy the unit file:
   ```
   sudo cp publish/pi/media-button.service /etc/systemd/system/media-button.service
   ```
2) Create an env file with your secrets/config:
   ```
   sudo tee /etc/media-button/env >/dev/null <<'EOF'
   API_BASE=...
   DEVICE_ID=...
   DEVICE_KEY=...
   OPENAI_API_KEY=...
   # optional:
   # YT_FORCE_IPV4=1
   # YT_EXTRACTOR_ARGS=youtube:player_client=android
   EOF
   ```
3) Enable and start:
   ```
   sudo systemctl daemon-reload
   sudo systemctl enable media-button
   sudo systemctl start media-button
   ```

## Updating

- One-off update:
  ```
  sudo /opt/media-button/publish/pi/update.sh
  ```
  (Override branch with `BRANCH=release-2025-01` if needed.)

- To apply updates on every restart, the service already runs:
  ```
  ExecStartPre=/usr/bin/git fetch --all
  ExecStartPre=/usr/bin/git reset --hard origin/main
  ```
  Adjust to a specific branch/tag if you prefer pinned releases.

Dependencies are installed via `publish/pi/requirements.txt`; the update script will `pip3 install -r` automatically.
