#!/usr/bin/env python3
"""Set up the resident's television so the media button can switch it over.

Run on the device, with the television on and in the same room:

    sudo /opt/media-button/.venv/bin/python \
         /opt/media-button/publish/pi/setup-tv-display.py

It finds compatible sets on the network, pairs with the one chosen (which puts
a prompt on the television for someone to accept), works out which input the Pi
is plugged into by trying them while someone watches, and writes the result
into the device configuration.

Nothing is guessed. The input in particular is confirmed by eye, because the
only thing worse than a button that does nothing is a button that confidently
switches the television to the wrong socket.
"""

import datetime
import json
import os
import shutil
import socket
import ssl
import sys
import time
import urllib.request
from concurrent.futures import ThreadPoolExecutor

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import tv_display  # noqa: E402

try:
    import yaml
except ImportError:
    print("PyYAML is missing. Run this with the device's own interpreter:")
    print("  sudo /opt/media-button/.venv/bin/python %s" % sys.argv[0])
    sys.exit(1)

CONFIG_PATH = "/etc/media-button/config.yaml"
INPUT_CHOICES = ["KEY_HDMI", "KEY_HDMI1", "KEY_HDMI2", "KEY_HDMI3", "KEY_HDMI4"]


def ask(prompt: str, default: str | None = None) -> str:
    suffix = f" [{default}]" if default else ""
    while True:
        try:
            answer = input(f"{prompt}{suffix}: ").strip()
        except EOFError:
            print("\n(no input available — run this from a terminal)")
            sys.exit(1)
        if answer:
            return answer
        if default is not None:
            return default


def yes(prompt: str) -> bool:
    return ask(f"{prompt} (y/n)", "n").lower().startswith("y")


# ---------------------------------------------------------------------------
# 1. find the televisions
# ---------------------------------------------------------------------------

def local_subnet() -> str:
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        s.connect(("8.8.8.8", 53))
        return s.getsockname()[0].rsplit(".", 1)[0] + "."
    finally:
        s.close()


def looks_like_tv(host: str) -> dict | None:
    try:
        with socket.create_connection((host, 8001), timeout=1.2):
            pass
    except Exception:
        return None
    info = tv_display.device_info(host, timeout=4)
    if not info:
        return None
    device = info.get("device") or {}
    if not str(device.get("OS") or "").lower().startswith("tizen"):
        return None
    return device


def discover() -> list[dict]:
    base = local_subnet()
    print(f"Looking for compatible televisions on {base}0/24 ...")
    hosts = [f"{base}{i}" for i in range(1, 255)]
    found = []
    with ThreadPoolExecutor(max_workers=120) as pool:
        for host, device in zip(hosts, pool.map(looks_like_tv, hosts)):
            if device:
                device["_host"] = host
                found.append(device)
                print(f"  found {host}  {device.get('name')} "
                      f"({device.get('modelName')})")
    return found


# ---------------------------------------------------------------------------
# 2. pair
# ---------------------------------------------------------------------------

def pair(host: str) -> str | None:
    """Connect once so the set issues a token. Someone must accept on screen."""
    print("\nConnecting to the television.")
    print("A box will appear on screen asking whether to allow this device.")
    print("Someone needs to choose Allow — it gives up after about a minute.")
    sock = None
    try:
        sock = tv_display._ws_connect(host, None, timeout=70)
        deadline = time.time() + 70
        while time.time() < deadline:
            msg = tv_display._ws_recv(sock)
            event = msg.get("event")
            if event == "ms.channel.connect":
                data = msg.get("data") or {}
                token = data.get("token")
                for client in (data.get("clients") or []):
                    token = token or (client.get("attributes") or {}).get("token")
                return str(token) if token else ""
            if event == "ms.channel.unauthorized":
                print("  The television refused — the prompt was declined or missed.")
                return None
    except Exception as e:
        print(f"  Could not pair: {e}")
        return None
    finally:
        if sock is not None:
            try:
                sock.close()
            except Exception:
                pass
    print("  No answer from the television.")
    return None


# ---------------------------------------------------------------------------
# 3. find the input the Pi is on
# ---------------------------------------------------------------------------

def choose_input(host: str, token: str | None) -> str | None:
    print("\nNow to find which socket the media button is plugged into.")
    print("Watch the television. I will try each input in turn.")
    print("Say yes when the Pi's own picture appears — its menu or a video.\n")
    for key in INPUT_CHOICES:
        print(f"  trying {key} ...", end=" ", flush=True)
        ok = tv_display.send_keys(host, token, [key], gap=0.4)
        if not ok:
            print("not accepted by this set")
            continue
        print("sent")
        time.sleep(3)
        if yes("    Is the Pi's picture on screen now?"):
            return key
    print("\nNone of the inputs showed the Pi.")
    print("Check the HDMI lead is in the television and that the Pi is awake,")
    print("then run this again. Nothing has been saved.")
    return None


# ---------------------------------------------------------------------------
# 4. save
# ---------------------------------------------------------------------------

def save(host: str, token: str, mac: str, input_key: str, device: dict) -> None:
    config = {}
    if os.path.exists(CONFIG_PATH):
        with open(CONFIG_PATH) as f:
            config = yaml.safe_load(f) or {}

    mode = str(config.get("control_mode") or "")
    if mode != "beacon":
        print(f"\nNote: this device is in '{mode or 'unset'}' mode, not beacon mode.")
        print("Television switching only applies to beacon devices — a resident")
        print("using the remote can change input themselves. The settings will be")
        print("saved, and will take effect if the device is moved to beacon mode.")

    config["tv_display"] = {
        "enabled": True,
        "host": host,
        "mac": mac,
        "token": token,
        "input_key": input_key,
        "model": device.get("modelName") or "",
        "name": device.get("name") or "",
        # Repeated beacon triggers should not keep switching input underneath
        # whoever is watching.
        "resettle_seconds": 90,
        "settle_seconds": 3,
    }

    if os.path.exists(CONFIG_PATH):
        backup = CONFIG_PATH + ".bak-" + datetime.datetime.now().strftime("%Y%m%d%H%M%S")
        shutil.copy2(CONFIG_PATH, backup)
        print(f"\nPrevious configuration kept at {backup}")
    os.makedirs(os.path.dirname(CONFIG_PATH), exist_ok=True)
    with open(CONFIG_PATH, "w") as f:
        yaml.safe_dump(config, f, default_flow_style=False, sort_keys=True)
    print(f"Saved to {CONFIG_PATH}")


# ---------------------------------------------------------------------------

def main() -> int:
    print("Media button — television setup\n")

    host = None
    if len(sys.argv) > 1:
        host = sys.argv[1]
        print(f"Using the address given: {host}")
        device = looks_like_tv(host)
        if not device:
            print("  That address did not answer as a compatible television.")
            print("  Make sure it is switched on, then try again.")
            return 1
        device["_host"] = host
    else:
        found = discover()
        if not found:
            print("\nNo compatible television found.")
            print("The set must be switched on, on this same network, and be a")
            print("Tizen model. If you know its address, pass it as an argument.")
            return 1
        if len(found) == 1:
            device = found[0]
            print(f"\nOne television found: {device.get('name')} "
                  f"({device.get('modelName')}) at {device['_host']}")
            if not yes("Set this one up?"):
                return 1
        else:
            print("\nSeveral televisions found:")
            for i, d in enumerate(found, 1):
                print(f"  {i}. {d['_host']}  {d.get('name')} ({d.get('modelName')})")
            while True:
                choice = ask("Which one (number)")
                if choice.isdigit() and 1 <= int(choice) <= len(found):
                    device = found[int(choice) - 1]
                    break
        host = device["_host"]

    mac = str(device.get("wifiMac") or "").lower()
    print(f"\n  model     {device.get('modelName')}")
    print(f"  name      {device.get('name')}")
    print(f"  address   {host}")
    print(f"  hardware  {mac or 'unknown'}")
    if not mac:
        print("  (no hardware address reported — the set cannot be woken from")
        print("   standby, so it will need to be left on)")

    token = pair(host)
    if token is None:
        return 1
    print(f"  paired{' (token ' + token + ')' if token else ' (no token needed)'}")

    input_key = choose_input(host, token)
    if not input_key:
        return 1
    print(f"\n  the Pi is on {input_key}")

    save(host, token or "", mac, input_key, device)

    print("\nChecking it works from a standing start ...")
    tv_display.forget_prepared()
    config = {"tv_display": {"enabled": True, "host": host, "mac": mac,
                             "token": token or "", "input_key": input_key}}
    ok = tv_display.prepare(config, reason="setup check")
    print("  %s" % ("television switched over as expected"
                    if ok else "could not switch the television — see the message above"))

    print("\nRestart the media button for this to take effect:")
    print("  sudo systemctl restart media-button")
    return 0 if ok else 2


if __name__ == "__main__":
    sys.exit(main())
