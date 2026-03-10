#!/usr/bin/env python3
"""
BCH Media Button — New Unit Setup Wizard
=========================================
Run once on a freshly deployed Pi to configure the resident and beacon mapping.

  sudo python3 /opt/media-button/publish/pi/setup_wizard.py

Reads API credentials from /etc/media-button/env (same file the service uses).
Writes the result to publish/pi/config.yaml next to this script.
"""

import asyncio
import os
import sys
import time
import urllib.parse
from pathlib import Path

import requests
import yaml
from bleak import BleakScanner

# ─── Paths ────────────────────────────────────────────────────────────────────

BASE_DIR    = Path(__file__).parent
CONFIG_PATH = BASE_DIR / "config.yaml"
ENV_FILE    = Path("/etc/media-button/env")

EDDYSTONE_UUID = "feaa"

# ─── Beacon key parsers (mirror of media_button_pi.py) ───────────────────────

def _parse_eddystone(service_data: dict) -> str | None:
    try:
        for uuid, payload in service_data.items():
            if uuid.lower().replace("-", "") == EDDYSTONE_UUID:
                b = bytes(payload)
                if len(b) >= 18 and b[0] == 0x00:
                    ns   = b[2:12].hex()
                    inst = b[12:18].hex()
                    return f"eddystone:{ns}{inst}"
    except Exception:
        pass
    return None


def _parse_ibeacon(manufacturer_data: dict) -> str | None:
    try:
        payload = manufacturer_data.get(76) or manufacturer_data.get(0x004C)
        if payload and len(payload) >= 23 and payload[0] == 0x02 and payload[1] == 0x15:
            uuid     = payload[2:18].hex()
            uuid_fmt = f"{uuid[0:8]}-{uuid[8:12]}-{uuid[12:16]}-{uuid[16:20]}-{uuid[20:32]}"
            major    = int.from_bytes(payload[18:20], "big")
            minor    = int.from_bytes(payload[20:22], "big")
            return f"ibeacon:{uuid_fmt}-{major:04d}-{minor:04d}"
    except Exception:
        pass
    return None


# ─── Resident fetch ───────────────────────────────────────────────────────────

def fetch_residents(api_base: str, device_id: str, device_key: str) -> list[dict]:
    url = (
        f"{api_base}/api/device/{urllib.parse.quote(device_id)}"
        "/setup/mobizio-residents"
    )
    try:
        r = requests.get(url, headers={"X-DEVICE-KEY": device_key}, timeout=30)
        r.raise_for_status()
        return r.json()          # [{name, caseId, dob}, ...]
    except requests.HTTPError as e:
        print(f"\n[ERROR] API returned {e.response.status_code}: {e.response.text}")
    except Exception as e:
        print(f"\n[ERROR] Failed to reach API: {e}")
    return []


# ─── BLE scan ─────────────────────────────────────────────────────────────────

async def scan_beacons(duration: int = 15) -> dict[str, int]:
    """
    Scan for iBeacon / Eddystone-UID beacons.
    Returns {beacon_key: best_rssi_seen}.
    """
    seen: dict[str, int] = {}

    def _cb(device, adv_data):
        key = (
            _parse_eddystone(adv_data.service_data or {})
            or _parse_ibeacon(adv_data.manufacturer_data or {})
        )
        if not key:
            return
        rssi = device.rssi
        if rssi is not None:
            if key not in seen or rssi > seen[key]:
                seen[key] = rssi

    async with BleakScanner(detection_callback=_cb):
        for i in range(duration):
            sys.stdout.write(
                f"\r  Scanning... {i + 1}/{duration}s  "
                f"({len(seen)} beacon(s) found)   "
            )
            sys.stdout.flush()
            await asyncio.sleep(1)

    print()
    return seen


# ─── Config helpers ───────────────────────────────────────────────────────────

def _load_config() -> dict:
    if CONFIG_PATH.exists():
        with open(CONFIG_PATH) as f:
            return yaml.safe_load(f) or {}
    return {}


def _write_config(config: dict) -> None:
    with open(CONFIG_PATH, "w") as f:
        yaml.dump(config, f, default_flow_style=False, allow_unicode=True)


def _apply_setup(
    config: dict,
    resident_name: str,
    beacon_key: str | None,
) -> dict:
    # Beacon → resident mapping
    if beacon_key:
        config.setdefault("beacons", {})[beacon_key] = {"resident": resident_name}

    # Resident entry (preserve existing, only create if missing)
    if resident_name not in config.setdefault("residents", {}):
        config["residents"][resident_name] = {
            "mode": "music",
            "query": "",
        }

    # remote_control resident (only if not already set)
    rc = config.get("remote_control")
    if isinstance(rc, dict) and not rc.get("resident"):
        rc["resident"] = resident_name

    return config


# ─── Terminal UI helpers ──────────────────────────────────────────────────────

def _print_banner() -> None:
    print()
    print("╔══════════════════════════════════════════════╗")
    print("║   BCH Media Button — New Unit Setup Wizard   ║")
    print("╚══════════════════════════════════════════════╝")
    print()


def _pick(prompt: str, items: list, display_fn=None) -> int:
    """Show a numbered list and return the 0-based index of the user's choice."""
    for i, item in enumerate(items):
        label = display_fn(item) if display_fn else str(item)
        print(f"  {i + 1:4}. {label}")
    print()
    while True:
        raw = input(f"{prompt} [1–{len(items)}]: ").strip()
        try:
            idx = int(raw) - 1
            if 0 <= idx < len(items):
                return idx
        except ValueError:
            pass
        print(f"  Please enter a number between 1 and {len(items)}.")


def _read_env_file(path: Path) -> dict[str, str]:
    """Parse KEY=VALUE lines from a plain env file (no shell expansion)."""
    env: dict[str, str] = {}
    try:
        for line in path.read_text().splitlines():
            line = line.strip()
            if not line or line.startswith("#"):
                continue
            if "=" in line:
                k, _, v = line.partition("=")
                env[k.strip()] = v.strip().strip('"').strip("'")
    except Exception:
        pass
    return env


# ─── Main ─────────────────────────────────────────────────────────────────────

def main() -> None:
    _print_banner()

    # ── Load credentials ──────────────────────────────────────────────────────
    env: dict[str, str] = {}
    if ENV_FILE.exists():
        env = _read_env_file(ENV_FILE)
    else:
        print(f"[WARN] Environment file not found at {ENV_FILE}")

    api_base   = env.get("API_BASE", "").rstrip("/")
    device_id  = env.get("DEVICE_ID", "")
    device_key = env.get("DEVICE_KEY", "")

    if not api_base:
        api_base = input(
            "API base URL (e.g. https://yourapp.azurewebsites.net): "
        ).strip().rstrip("/")
    if not device_id:
        device_id = input("Device ID: ").strip()
    if not device_key:
        device_key = input("Device key: ").strip()

    # ── Load current config ───────────────────────────────────────────────────
    config       = _load_config()
    control_mode = config.get("control_mode", "beacon")

    print(f"  Device ID    : {device_id}")
    print(f"  Control mode : {control_mode}")
    print(f"  Config file  : {CONFIG_PATH}")
    print()

    # ── Fetch residents from Mobizio ──────────────────────────────────────────
    print("Fetching active residents from Mobizio care planning…")
    residents = fetch_residents(api_base, device_id, device_key)
    if not residents:
        print(
            "\n[ERROR] No residents returned. "
            "Check API connectivity and that this device ID/key are registered."
        )
        sys.exit(1)

    print(f"  Found {len(residents)} active resident(s).\n")

    resident_idx = _pick(
        "Select resident for this unit",
        residents,
        display_fn=lambda r: f"{r['name']}  (Case ID: {r['caseId']})",
    )
    chosen        = residents[resident_idx]
    resident_name = chosen["name"]
    case_id       = chosen["caseId"]
    print(f"\n  ✓ Selected: {resident_name}  (Case ID: {case_id})\n")

    # ── Beacon scan ───────────────────────────────────────────────────────────
    beacon_key: str | None = None

    if control_mode == "beacon":
        print("Scanning for nearby BLE beacons (15 seconds)…")
        print("Make sure the resident's beacon is switched on and nearby.\n")

        beacons = asyncio.run(scan_beacons(duration=15))

        if not beacons:
            print(
                "[WARN] No beacons detected. You can add the beacon key manually "
                "to config.yaml later.\n"
            )
        else:
            # Strongest RSSI first
            sorted_beacons = sorted(beacons.items(), key=lambda x: x[1], reverse=True)
            print(f"\n  Found {len(sorted_beacons)} beacon(s):\n")
            beacon_idx = _pick(
                "Select beacon for this unit (nearest beacon should be at the top)",
                sorted_beacons,
                display_fn=lambda b: f"{b[0]}   (RSSI {b[1]} dBm)",
            )
            beacon_key = sorted_beacons[beacon_idx][0]
            print(f"\n  ✓ Selected beacon: {beacon_key}\n")
    else:
        print(
            "[INFO] control_mode is not 'beacon' — skipping beacon scan.\n"
            "       Set control_mode: beacon in config.yaml if you need beacon support.\n"
        )

    # ── Confirm ───────────────────────────────────────────────────────────────
    print("─" * 60)
    print("Summary")
    print("─" * 60)
    print(f"  Resident  : {resident_name}")
    print(f"  Case ID   : {case_id}")
    if beacon_key:
        print(f"  Beacon    : {beacon_key}")
    else:
        print(f"  Beacon    : (none — add manually if needed)")
    print(f"  Config    : {CONFIG_PATH}")
    print("─" * 60)

    confirm = input("\nWrite to config.yaml? [y/N]: ").strip().lower()
    if confirm != "y":
        print("Aborted — no changes written.")
        sys.exit(0)

    # ── Write ─────────────────────────────────────────────────────────────────
    config = _apply_setup(config, resident_name, beacon_key)
    _write_config(config)

    print(f"\n  ✓ Config written to {CONFIG_PATH}\n")

    # ── Next steps ────────────────────────────────────────────────────────────
    resident_cfg = config["residents"].get(resident_name, {})
    print("Next steps:")
    if not resident_cfg.get("query"):
        print(
            f"  1. Edit config.yaml — add a music search query for {resident_name}:\n"
            f'\n     residents:\n'
            f'       "{resident_name}":\n'
            f'         mode: music\n'
            f'         query: "Artist name greatest hits"\n'
        )
    print("  • Restart the service:  sudo systemctl restart media-button")
    print("  • View logs:            sudo journalctl -u media-button -f\n")


if __name__ == "__main__":
    main()
