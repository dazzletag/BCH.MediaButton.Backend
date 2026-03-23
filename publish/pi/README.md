# Media Button Pi deployment

## Quick install (recommended)

Run this on a fresh Raspberry Pi. The admin generates the command from the dashboard — only `--api` and `--key` are needed. The device ID is automatically detected from the Pi's hardware serial number.

```bash
curl -sSL https://raw.githubusercontent.com/dazzletag/BCH.MediaButton.Backend/main/publish/pi/install.sh \
  | sudo bash -s -- \
      --api https://bch-media.azurewebsites.net \
      --key "REPLACE_WITH_DEVICE_KEY"
```

The installer will:
1. Install system packages (git, Python, VLC, Bluetooth tools)
2. Create the `dazzletag` user
3. Auto-detect the Pi's hardware serial number as the device ID
4. Clone this repo to `/opt/media-button`
5. Create a Python venv and install dependencies
6. Write credentials to `/etc/media-button/env`
7. Install and enable the `media-button` systemd service
8. Launch the setup wizard (resident & beacon selection)

### Installer options

| Flag | Required | Description |
|------|----------|-------------|
| `--api URL` | Yes | Backend API base URL |
| `--key KEY` | Yes | Device secret key |
| `--device ID` | No | Override device ID (default: Pi hardware serial) |
| `--branch NAME` | No | Git branch to track (default: `main`) |
| `--no-wizard` | No | Skip the setup wizard (run it later) |

### Finding the Pi serial (for pre-registering in the backend)

```bash
cat /sys/firmware/devicetree/base/serial-number
# or
grep Serial /proc/cpuinfo
```

### TP-Link Archer T2U Plus WiFi adapter

The installer auto-detects the adapter (USB ID `2357:0120`) and installs the RTL8821AU driver immediately if it's plugged in. It also installs a `media-button-wifi` systemd service that runs on every boot — so if the adapter is added to an existing Pi later, the driver will be installed automatically on the next restart.

The driver uses [morrownr/8821au-20210708](https://github.com/morrownr/8821au-20210708) via DKMS, so it survives kernel updates. Subsequent boots are fast — the service exits immediately if the driver is already loaded.

To install the driver manually on an existing Pi:

```bash
sudo bash /opt/media-button/publish/pi/install_wifi_adapter.sh
sudo reboot
```

---

### Re-running the setup wizard

```bash
sudo -u dazzletag /opt/media-button/.venv/bin/python3 \
  /opt/media-button/publish/pi/setup_wizard.py
```

---

## Manual setup

Suggested layout on the Pi:

```
/opt/media-button          # git clone of this repo
  publish/pi/media_button_pi.py
  publish/pi/ui_display.py
  publish/pi/requirements.txt
```

### Systemd service

1) Copy the unit file:
   ```
   sudo cp publish/pi/media-button.service /etc/systemd/system/media-button.service
   ```
2) Create an env file with your secrets/config:
   ```
   sudo mkdir -p /etc/media-button
   sudo tee /etc/media-button/env >/dev/null <<'EOF'
   API_BASE=...
   DEVICE_ID=...
   DEVICE_KEY=...
   OPENAI_API_KEY=...
   # optional:
   # YT_FORCE_IPV4=1
   # YT_EXTRACTOR_ARGS=youtube:player_client=android
   EOF
   sudo chmod 600 /etc/media-button/env
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
