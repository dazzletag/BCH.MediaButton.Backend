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

## Remote access (reverse SSH tunnel)

Each Pi dials home to the `bch-app` VM and forwards its own port 22, so it
can be reached from a laptop even behind a care home's NAT:

```
ssh brianpi     # -> 127.0.0.1:2224 on the VM -> Brian's Pi :22
```

Ports are allocated one per device (2222, 2223, ... ). Check what is already
taken before picking one:

```
netstat -an | findstr LISTENING | findstr :222     # on the VM (Windows)
```

### A new device

```bash
sudo bash setup-reverse-tunnel.sh --port 2227 --label "Quarry media button"
```

That installs autossh, generates `~/.ssh/tunnel_key`, records `TUNNEL_PORT`
and `TUNNEL_LABEL` in `/etc/media-button/env`, enables the Pi's SSH server,
accepts the VM host key, and starts the unit. It then prints a public key
which must be added to the VM's `authorized_keys` **from an elevated
PowerShell** — `field-nursecall` is a standard user and cannot write its own
`authorized_keys`.

The Pi also needs the laptop's key in `dazzletag`'s `~/.ssh/authorized_keys`,
or the tunnel will connect and every login will still be refused.

### Keeping it in step

`/etc/systemd/system/reverse-tunnel.service` is rendered from
`reverse-tunnel.service.template` by `sync-system-files.sh` on every
media-button start, so the unit tracks the repo across the fleet and a
damaged one repairs itself. Local edits to the installed unit are
overwritten — change the template, or the per-device values in
`/etc/media-button/env`.

The port is taken from `TUNNEL_PORT`; if that is unset it is adopted from
the unit already installed, so devices built by hand need no change. A
device with no port anywhere is left alone.

Syncing deliberately does **not** restart the tunnel: whoever is watching is
usually connected through it. A changed unit takes effect on the next boot,
or on a manual `systemctl restart reverse-tunnel` from the console.

### Two traps

- Raspberry Pi OS ships with its SSH **server** disabled. Without
  `systemctl enable --now ssh` the tunnel forwards a port to nothing, and
  connections fail with `kex_exchange_identification: Connection closed`.
- `autossh` will sit in a retry loop forever if it has never accepted the
  VM's host key, despite `StrictHostKeyChecking=accept-new`. The setup
  script does one authenticated connection as the app user to settle it.

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
