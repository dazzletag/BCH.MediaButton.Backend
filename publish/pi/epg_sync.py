"""Push the programme guide this device receives up to the portal.

Only devices with a TV HAT have an aerial, and only they can see what is
actually broadcast in that building, so the guide has to originate here and
travel up rather than the other way round. The portal has no route into a
care home network; this mirrors how cache_sync already pushes cache snapshots.

Reads TVHeadend's EPG over loopback and POSTs it to
/api/device/<id>/epg. The upload is idempotent server-side, so this can send
everything it has on every pass without tracking what went last time — which
matters because TVHeadend rewrites events as the broadcast data is refined.

Disabled unless TVH_URL is configured, so devices without a tuner are
unaffected.
"""

import base64
import json
import os
import threading
import time
import urllib.parse
import urllib.request
from datetime import datetime, timezone

TVH_URL = os.getenv("TVH_URL", "")                       # e.g. http://127.0.0.1:9981
TVH_USER = os.getenv("TVH_USER", "admin")
TVH_PASS = os.getenv("TVH_PASS", "")
EPG_SYNC_INTERVAL = int(os.getenv("EPG_SYNC_INTERVAL", str(6 * 3600)))
EPG_PAGE_SIZE = int(os.getenv("EPG_PAGE_SIZE", "1000"))
EPG_MAX_EVENTS = int(os.getenv("EPG_MAX_EVENTS", "20000"))


def _log(msg: str):
    try:
        print(f"[{datetime.now().isoformat(timespec='seconds')}] {msg}", flush=True)
    except Exception:
        print(msg, flush=True)


def _opener():
    """TVHeadend defaults to digest auth; basic is rejected with a 401 that
    looks exactly like wrong credentials."""
    mgr = urllib.request.HTTPPasswordMgrWithDefaultRealm()
    mgr.add_password(None, TVH_URL, TVH_USER, TVH_PASS)
    return urllib.request.build_opener(
        urllib.request.HTTPDigestAuthHandler(mgr),
        urllib.request.HTTPBasicAuthHandler(mgr),
    )


def _iso(ts) -> str | None:
    try:
        return datetime.fromtimestamp(int(ts), tz=timezone.utc).isoformat()
    except Exception:
        return None


def _as_int(v):
    try:
        return int(v)
    except Exception:
        return None


def fetch_epg() -> list[dict]:
    """Every event TVHeadend currently holds, as portal-shaped records."""
    op = _opener()
    out: list[dict] = []
    start = 0
    while start < EPG_MAX_EVENTS:
        url = (f"{TVH_URL}/api/epg/events/grid?start={start}&limit={EPG_PAGE_SIZE}")
        with op.open(urllib.request.Request(url), timeout=60) as r:
            page = json.loads(r.read().decode("utf-8", "replace"))
        entries = page.get("entries") or []
        if not entries:
            break
        for e in entries:
            start_iso, stop_iso = _iso(e.get("start")), _iso(e.get("stop"))
            title = (e.get("title") or "").strip()
            channel = (e.get("channelName") or "").strip()
            if not (start_iso and stop_iso and title and channel):
                continue
            out.append({
                "channelName": channel,
                "channelNumber": _as_int(e.get("channelNumber")),
                "channelUuid": e.get("channelUuid"),
                "title": title,
                "subtitle": (e.get("subtitle") or None),
                "description": (e.get("description") or e.get("summary") or None),
                "startUtc": start_iso,
                "stopUtc": stop_iso,
                "seriesCrid": e.get("serieslinkUri") or None,
                "episodeCrid": e.get("episodeUri") or None,
                "genre": _genre(e.get("genre")),
                "isHd": bool(e.get("hd")),
                "isSubtitled": bool(e.get("subtitled")),
                "isAudioDescribed": bool(e.get("audiodesc")),
            })
        total = page.get("totalCount") or 0
        start += len(entries)
        if start >= total:
            break
    return out


def _genre(g):
    """TVHeadend reports genre as a list of numeric codes; keep it readable
    and let the portal decide what to do with it."""
    if not g:
        return None
    if isinstance(g, list):
        return ",".join(str(x) for x in g[:4]) or None
    return str(g)[:200]


def push(api_base: str, device_id: str, device_key: str, events: list[dict]) -> dict | None:
    if not events:
        return None
    url = f"{api_base.rstrip('/')}/api/device/{urllib.parse.quote(device_id)}/epg"
    body = json.dumps({"events": events}).encode()
    req = urllib.request.Request(url, data=body, method="POST")
    req.add_header("Content-Type", "application/json")
    req.add_header("X-DEVICE-KEY", device_key)
    with urllib.request.urlopen(req, timeout=180) as r:
        return json.loads(r.read().decode("utf-8", "replace") or "{}")


def sync_once(api_base: str, device_id: str, device_key: str) -> bool:
    try:
        events = fetch_epg()
    except Exception as e:
        _log(f"[EPG-SYNC] Could not read TVHeadend: {e}")
        return False
    if not events:
        _log("[EPG-SYNC] TVHeadend has no events yet — nothing to send")
        return False
    try:
        # Send in batches: a full week across 60 channels is tens of thousands
        # of rows, and one enormous request is a poor thing to retry.
        sent = 0
        result = None
        for i in range(0, len(events), 2000):
            result = push(api_base, device_id, device_key, events[i:i + 2000])
            sent += len(events[i:i + 2000])
        _log(f"[EPG-SYNC] Uploaded {sent} event(s); last batch: {result}")
        return True
    except Exception as e:
        _log(f"[EPG-SYNC] Upload failed: {e}")
        return False


def start(api_base: str, device_id: str, device_key: str):
    """Begin periodic syncing. No-op when there is no tuner configured."""
    if not TVH_URL:
        return False
    if not (api_base and device_id and device_key):
        _log("[EPG-SYNC] API credentials missing — EPG upload disabled")
        return False

    def _run():
        # Let TVHeadend finish starting and collect something worth sending.
        time.sleep(90)
        while True:
            sync_once(api_base, device_id, device_key)
            time.sleep(EPG_SYNC_INTERVAL)

    threading.Thread(target=_run, name="EpgSync", daemon=True).start()
    _log(f"[EPG-SYNC] Worker started (every {EPG_SYNC_INTERVAL // 3600}h from {TVH_URL})")
    return True


if __name__ == "__main__":
    import sys
    api = os.getenv("API_BASE", "")
    dev = os.getenv("DEVICE_ID", "")
    key = os.getenv("DEVICE_KEY", "")
    if "--dry-run" in sys.argv:
        evs = fetch_epg()
        print(f"read {len(evs)} event(s) from TVHeadend")
        for e in evs[:5]:
            print("  ", e["startUtc"][11:16], e["channelName"], "|", e["title"],
                  "| series:", e["seriesCrid"])
    else:
        sync_once(api, dev, key)
