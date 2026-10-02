"""Put the resident's television on the right input before anything plays.

The failure this removes is a mundane one. A resident presses their button, the
Pi starts playing, and nothing appears — because the television is off, or on
the input the DVD player used to be on. They cannot fix that themselves; that
inability is the whole reason the button exists.

So when a beacon triggers, the set is woken and switched to the input the Pi is
plugged into, before the first frame plays.

Three rules this module holds to, because it runs on devices where people are
already being looked after:

  - Without configuration it does nothing at all. No `tv_display` section
    means every function here returns immediately, so a device that has never
    heard of a television behaves exactly as it did before.
  - It never raises into playback. A television that is unplugged, renamed or
    swapped must not stop a resident's video from playing; the video on the
    Pi's own screen is still better than a blank one.
  - It is bounded. Every wait has a ceiling, because a resident standing in
    front of a screen will not wait while we retry a network handshake.

Beacon mode only. A resident driving a menu with a remote can see what they
are doing and change input themselves; taking that over would be rude and is
not what was asked for.
"""

import base64
import json
import os
import socket
import ssl
import struct
import threading
import time
from datetime import datetime

# How long we are willing to make a resident wait, in total, for a television.
PREPARE_BUDGET = float(os.getenv("TV_DISPLAY_BUDGET", "12"))
WAKE_ATTEMPTS = int(os.getenv("TV_DISPLAY_WAKE_ATTEMPTS", "3"))

_lock = threading.Lock()
_last_prepared = 0.0


def _log(msg: str):
    print(f"[{datetime.now().isoformat(timespec='seconds')}] {msg}", flush=True)


# ---------------------------------------------------------------------------
# configuration
# ---------------------------------------------------------------------------

def settings(config: dict | None) -> dict:
    """The tv_display section, or an empty dict if there isn't one."""
    if not isinstance(config, dict):
        return {}
    cfg = config.get("tv_display") or {}
    return cfg if isinstance(cfg, dict) else {}


def available(config: dict | None) -> bool:
    """True only when a television has actually been set up on this device."""
    cfg = settings(config)
    return bool(cfg.get("host")) and bool(cfg.get("enabled", True))


# ---------------------------------------------------------------------------
# the websocket Samsung uses for its remote
# ---------------------------------------------------------------------------

def _ws_connect(host: str, token: str | None, port: int = 8002, timeout: float = 8.0):
    name = base64.b64encode(b"Media Button").decode()
    path = f"/api/v2/channels/samsung.remote.control?name={name}"
    if token:
        path += f"&token={token}"
    ctx = ssl.create_default_context()
    ctx.check_hostname = False
    ctx.verify_mode = ssl.CERT_NONE
    sock = ctx.wrap_socket(socket.create_connection((host, port), timeout=timeout),
                           server_hostname=host)
    sock.settimeout(timeout)
    key = base64.b64encode(os.urandom(16)).decode()
    sock.sendall(f"GET {path} HTTP/1.1\r\nHost: {host}:{port}\r\n"
                 f"Upgrade: websocket\r\nConnection: Upgrade\r\n"
                 f"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n\r\n".encode())
    head = b""
    while b"\r\n\r\n" not in head:
        chunk = sock.recv(1)
        if not chunk:
            raise RuntimeError("closed during handshake")
        head += chunk
        if len(head) > 8192:
            raise RuntimeError("handshake too long")
    if b"101" not in head.split(b"\r\n", 1)[0]:
        raise RuntimeError(head.split(b"\r\n", 1)[0].decode("utf-8", "replace"))
    return sock


def _ws_send(sock, obj: dict):
    payload = json.dumps(obj).encode()
    mask = os.urandom(4)
    n = len(payload)
    if n < 126:
        header = b"\x81" + bytes([0x80 | n])
    else:
        header = b"\x81\xfe" + struct.pack(">H", n)
    sock.sendall(header + mask + bytes(b ^ mask[i % 4] for i, b in enumerate(payload)))


def _ws_recv(sock) -> dict:
    def exact(n):
        buf = b""
        while len(buf) < n:
            part = sock.recv(n - len(buf))
            if not part:
                raise RuntimeError("closed")
            buf += part
        return buf

    b0, b1 = exact(2)
    length = b1 & 0x7F
    if length == 126:
        length = struct.unpack(">H", exact(2))[0]
    elif length == 127:
        length = struct.unpack(">Q", exact(8))[0]
    data = exact(length)
    if (b0 & 0x0F) == 0x08:
        raise RuntimeError("closed by the television")
    try:
        return json.loads(data.decode("utf-8", "replace"))
    except Exception:
        return {}


def send_keys(host: str, token: str | None, keys: list[str],
              gap: float = 1.0, timeout: float = 8.0) -> bool:
    """Send remote keys. Returns whether they were accepted, never raises."""
    sock = None
    try:
        sock = _ws_connect(host, token, timeout=timeout)
        try:
            _ws_recv(sock)          # the connect acknowledgement
        except Exception:
            pass
        for k in keys:
            _ws_send(sock, {"method": "ms.remote.control",
                            "params": {"Cmd": "Click", "DataOfCmd": k,
                                       "Option": "false",
                                       "TypeOfRemote": "SendRemoteKey"}})
            time.sleep(gap)
        return True
    except Exception as e:
        _log(f"[TVSET] Could not send {','.join(keys)} to {host}: {e}")
        return False
    finally:
        if sock is not None:
            try:
                sock.close()
            except Exception:
                pass


# ---------------------------------------------------------------------------
# state
# ---------------------------------------------------------------------------

def device_info(host: str, timeout: float = 4.0) -> dict | None:
    """What the set says about itself, or None when it is asleep or absent."""
    import urllib.request
    ctx = ssl.create_default_context()
    ctx.check_hostname = False
    ctx.verify_mode = ssl.CERT_NONE
    for scheme, port in (("https", 8002), ("http", 8001)):
        try:
            req = urllib.request.Request(f"{scheme}://{host}:{port}/api/v2/",
                                         headers={"User-Agent": "media-button"})
            with urllib.request.urlopen(req, timeout=timeout, context=ctx) as r:
                return json.loads(r.read().decode("utf-8", "replace"))
        except Exception:
            continue
    return None


def is_awake(host: str, timeout: float = 2.5) -> bool:
    """A set in standby stops answering, so reachability is the test."""
    try:
        with socket.create_connection((host, 8001), timeout=timeout):
            return True
    except Exception:
        return False


def wake(host: str, mac: str | None) -> None:
    """Wake-on-LAN. Standby takes the remote API down with it, so this is the
    only way back — and plenty of sets ignore it over wireless, which is why
    every caller treats failure as ordinary."""
    if not mac:
        return
    try:
        raw = bytes.fromhex(mac.replace(":", "").replace("-", ""))
    except ValueError:
        _log(f"[TVSET] '{mac}' is not a hardware address")
        return
    if len(raw) != 6:
        return
    packet = b"\xff" * 6 + raw * 16
    net = host.rsplit(".", 1)[0] + ".255"
    for dest in ("255.255.255.255", net, host):
        for port in (9, 7):
            try:
                s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
                s.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
                s.settimeout(1)
                s.sendto(packet, (dest, port))
                s.close()
            except Exception:
                pass


# ---------------------------------------------------------------------------
# the one call playback makes
# ---------------------------------------------------------------------------

def prepare(config: dict | None, reason: str = "") -> bool:
    """Wake the set and put it on the Pi's input. Safe to call at any time.

    Returns True when the television should now be showing the Pi. False means
    it could not be reached — playback carries on regardless, because a video
    playing on a screen nobody switched over is still better than no video.
    """
    global _last_prepared
    cfg = settings(config)
    if not available(config):
        return False

    host = str(cfg["host"])
    token = cfg.get("token") or None
    mac = cfg.get("mac") or None
    input_key = str(cfg.get("input_key") or "KEY_HDMI")

    # A resident may trigger the beacon repeatedly in a few minutes. Switching
    # input under them each time would be worse than not bothering.
    with _lock:
        if time.time() - _last_prepared < float(cfg.get("resettle_seconds", 90)):
            return True
        _last_prepared = time.time()

    started = time.time()
    deadline = started + PREPARE_BUDGET
    _log(f"[TVSET] Preparing {host}{' for ' + reason if reason else ''}")

    def left() -> float:
        return deadline - time.time()

    # Every wait below is measured against one deadline. Checking the budget
    # only between attempts let a set that was merely slow to refuse overrun it
    # several times over, which defeats the point of having a budget.
    awake = is_awake(host, timeout=min(2.0, max(0.3, left())))
    attempt = 0
    while not awake and left() > 1.0 and attempt < WAKE_ATTEMPTS:
        attempt += 1
        wake(host, mac)
        while left() > 0.5:
            time.sleep(min(0.75, max(0.1, left())))
            if is_awake(host, timeout=min(1.0, max(0.3, left()))):
                awake = True
                break
        if awake:
            _log(f"[TVSET] Woke {host} on attempt {attempt} ({time.time() - started:.1f}s)")

    if not awake:
        _log(f"[TVSET] {host} did not answer in {time.time() - started:.1f}s — "
             f"playing anyway, the screen may be on the wrong input")
        return False

    # A set that has just woken drops keys sent too early.
    time.sleep(max(0.0, min(float(cfg.get("settle_seconds", 3)), left())))

    ok = send_keys(host, token, [input_key], gap=0.4,
                   timeout=max(2.0, min(8.0, left() + 2.0)))
    _log(f"[TVSET] {'Switched' if ok else 'Could not switch'} {host} to "
         f"{input_key} ({time.time() - started:.1f}s)")
    return ok


def prepare_async(config: dict | None, reason: str = "") -> None:
    """Prepare without making the resident wait for the network.

    Playback and the television come up alongside each other. The first second
    or two may land on the old input, which is a far smaller problem than a
    button that feels broken because it does nothing for ten seconds.
    """
    if not available(config):
        return
    threading.Thread(target=lambda: prepare(config, reason),
                     name="TvDisplayPrepare", daemon=True).start()


def forget_prepared() -> None:
    """Allow the next prepare() to act immediately. For tests and for a
    deliberate re-run from the setup routine."""
    global _last_prepared
    with _lock:
        _last_prepared = 0.0
