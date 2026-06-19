#!/usr/bin/env python3
"""
BCH Media Button — Quick Setup Wizard
======================================
Slim version of setup_wizard.py for units where the FLIRC keys are already
mapped and no Stability AI key is needed. Steps:

  1. Load credentials from /etc/media-button/env
  2. Pick a resident from Mobizio
  3. Pick control mode (switch beacon / remote menu)
  4. If switch mode: scan and pick a BLE beacon
  5. Write /etc/media-button/config.yaml
  6. Register the resident with the backend

Run:
  sudo python3 /opt/media-button/publish/pi/setup_wizard_quick.py
"""

import asyncio
import sys

from setup_wizard import (
    CONFIG_PATH,
    ENV_FILE,
    _apply_setup,
    _load_config,
    _pick,
    _print_banner,
    _read_env_file,
    _register_resident,
    _write_config,
    fetch_residents,
    scan_beacons,
)


def main() -> None:
    _print_banner()

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

    config = _load_config()

    print(f"  Device ID   : {device_id}")
    print(f"  Config file : {CONFIG_PATH}")
    print()

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
    chosen          = residents[resident_idx]
    resident_name   = chosen["name"]
    case_id         = chosen["caseId"]
    tenant_case_id  = chosen.get("tenantCaseId")
    print(f"\n  ✓ Selected: {resident_name}  (Case ID: {case_id})\n")

    print("How is this Media Button activated?")
    print("  1. Switch  — a physical switch triggers via a nearby BLE beacon")
    print("  2. Remote  — a TV remote control (FLIRC already mapped)\n")
    while True:
        mode_choice = input("Select control type [1/2]: ").strip()
        if mode_choice in ("1", "2"):
            break
        print("  Please enter 1 or 2.")

    control_mode = "beacon" if mode_choice == "1" else "remote_menu"
    print(f"\n  ✓ Control mode: {control_mode}\n")

    beacon_key: str | None = None
    if control_mode == "beacon":
        print("Scanning for nearby BLE beacons (15 seconds)...")
        print("Make sure the resident's beacon is switched on and nearby.\n")

        beacons = asyncio.run(scan_beacons(duration=15))

        if not beacons:
            print(
                "[WARN] No beacons detected. You can add the beacon key manually "
                "to config.yaml later.\n"
            )
        else:
            sorted_beacons = sorted(beacons.items(), key=lambda x: x[1], reverse=True)
            print(f"\n  Found {len(sorted_beacons)} beacon(s):\n")
            beacon_idx = _pick(
                "Select beacon for this unit (nearest beacon should be at the top)",
                sorted_beacons,
                display_fn=lambda b: f"{b[0]}   (RSSI {b[1]} dBm)",
            )
            beacon_key = sorted_beacons[beacon_idx][0]
            print(f"\n  ✓ Selected beacon: {beacon_key}\n")

    print("-" * 60)
    print("Summary")
    print("-" * 60)
    print(f"  Resident     : {resident_name}")
    print(f"  Case ID      : {case_id}")
    print(f"  Control mode : {control_mode}")
    if beacon_key:
        print(f"  Beacon       : {beacon_key}")
    elif control_mode == "beacon":
        print(f"  Beacon       : (none -- add manually to config.yaml)")
    print(f"  Config       : {CONFIG_PATH}")
    print("-" * 60)

    confirm = input("\nWrite to config.yaml? [y/N]: ").strip().lower()
    if confirm != "y":
        print("Aborted — no changes written.")
        sys.exit(0)

    config = _apply_setup(config, resident_name, control_mode, beacon_key)
    _write_config(config)

    print(f"\n  ✓ Config written to {CONFIG_PATH}\n")

    _register_resident(api_base, device_id, device_key, resident_name, case_id, tenant_case_id)

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
