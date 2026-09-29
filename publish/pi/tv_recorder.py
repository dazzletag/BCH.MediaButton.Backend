"""Turn a resident's TV choices into recordings, and recordings into playable files.

The premise of the whole feature is that the resident can no longer choose a
programme themselves, and presses the button when *they* want something —
which will not be when the programme is broadcast. So the default is to
record: Emmerdale is then waiting whenever they press, rather than only if
they happen to be in front of the screen at seven o'clock.

Two playlist item types drive this, both plain strings alongside the existing
"media:" and "radio:" forms:

    series:crid://fp.bbc.co.uk/xyz        follow every episode
    programme:<channel>@<ISO start>       one specific showing

A "series:" item becomes a TVHeadend autorec rule keyed on the broadcast
series identifier, which is a native TVHeadend capability rather than
something reimplemented here. Completed recordings are registered in the
local video cache, so they play through exactly the same path as a downloaded
video: the menu, the beacon trigger and the garbage collector all work
unchanged, and TV is another source of content rather than a second player.
"""

import json
import os
import threading
import time
import urllib.parse
import urllib.request
from datetime import datetime

import cache_db

TVH_URL = os.getenv("TVH_URL", "")
TVH_USER = os.getenv("TVH_USER", "admin")
TVH_PASS = os.getenv("TVH_PASS", "")
TV_SYNC_INTERVAL = int(os.getenv("TV_SYNC_INTERVAL", "900"))      # 15 min
TV_KEEP_EPISODES = int(os.getenv("TV_KEEP_EPISODES", "3"))
# One tuner: it can record several channels on one multiplex at once, but not
# across multiplexes. Keeping the number of standing series rules modest makes
# clashes far less likely than letting them accumulate unbounded.
TV_MAX_SERIES = int(os.getenv("TV_MAX_SERIES", "6"))


def _log(msg: str):
    try:
        print(f"[{datetime.now().isoformat(timespec='seconds')}] {msg}", flush=True)
    except Exception:
        print(msg, flush=True)


def _opener():
    mgr = urllib.request.HTTPPasswordMgrWithDefaultRealm()
    mgr.add_password(None, TVH_URL, TVH_USER, TVH_PASS)
    return urllib.request.build_opener(
        urllib.request.HTTPDigestAuthHandler(mgr),
        urllib.request.HTTPBasicAuthHandler(mgr),
    )


def tvh(path: str, **fields):
    data = urllib.parse.urlencode(fields).encode() if fields else None
    req = urllib.request.Request(TVH_URL + path, data=data)
    with _opener().open(req, timeout=45) as r:
        body = r.read().decode("utf-8", "replace")
    return json.loads(body) if body.strip().startswith(("{", "[")) else body


# ---------------------------------------------------------------------------
# playlist -> recording rules
# ---------------------------------------------------------------------------

def series_crids(playlist) -> list[str]:
    """The series CRIDs a playlist asks to follow."""
    out = []
    for item in playlist or []:
        val = item.get("url") or item.get("query") or "" if isinstance(item, dict) else str(item)
        val = (val or "").strip()
        if val.lower().startswith("series:"):
            crid = val.split(":", 1)[1].strip()
            if crid and crid not in out:
                out.append(crid)
    return out


def one_off_requests(playlist) -> list[tuple[str, str]]:
    """(channel, ISO start) pairs the playlist asks to record once.

    The channel and start time are carried in the item itself rather than an
    id, because the portal's event ids mean nothing on this device — TVHeadend
    numbers events in its own space, and the two never meet. Channel plus start
    time is the one identity both ends already agree on.
    """
    out = []
    for item in playlist or []:
        val = item.get("url") or item.get("query") or "" if isinstance(item, dict) else str(item)
        val = (val or "").strip()
        if not val.lower().startswith("programme:"):
            continue
        body = val.split(":", 1)[1]
        if "@" not in body:
            _log(f"[TV] Ignoring malformed one-off request: {val[:60]}")
            continue
        channel, _, start = body.rpartition("@")
        if channel.strip() and start.strip():
            out.append((channel.strip(), start.strip()))
    return out


def sync_one_offs(playlist) -> None:
    """Schedule single showings, refusing anything already begun.

    A recording set for a programme that has already started would capture
    only its tail, and one already finished would capture nothing at all while
    looking to staff as though it had worked.
    """
    wanted = one_off_requests(playlist)
    if not wanted:
        return
    try:
        events = tvh("/api/epg/events/grid", limit="3000").get("entries", [])
        scheduled = tvh("/api/dvr/entry/grid_upcoming", limit="300").get("entries", [])
    except Exception as e:
        _log(f"[TV] Could not read guide or schedule: {e}")
        return

    already = {(str(r.get("channelname") or ""), int(r.get("start") or 0)) for r in scheduled}
    now = time.time()

    for channel, start_iso in wanted:
        try:
            want_start = int(datetime.fromisoformat(start_iso.replace("Z", "+00:00")).timestamp())
        except Exception:
            _log(f"[TV] Ignoring unparsable start time: {start_iso}")
            continue

        if want_start <= now:
            _log(f"[TV] Not recording '{channel}' at {start_iso} — it has already started")
            continue
        if (channel, want_start) in already:
            continue

        # Match on channel and start, allowing a minute either way: the guide
        # is revised as broadcast data is refined, so an exact match is brittle.
        match = next((e for e in events
                      if str(e.get("channelName") or "") == channel
                      and abs(int(e.get("start") or 0) - want_start) <= 60), None)
        if not match:
            _log(f"[TV] No listing for '{channel}' at {start_iso} — it may have been rescheduled")
            continue

        try:
            tvh("/api/dvr/entry/create_by_event",
                event_id=str(match.get("eventId")), config_uuid="")
            _log(f"[TV] Recording '{match.get('title')}' on {channel} at {start_iso}")
        except Exception as e:
            _log(f"[TV] Could not schedule '{match.get('title')}': {e}")


def existing_autorecs() -> dict[str, str]:
    """Series CRID -> autorec uuid, for rules this code created."""
    try:
        rows = tvh("/api/dvr/autorec/grid", limit="200").get("entries", [])
    except Exception as e:
        _log(f"[TV] Could not list recording rules: {e}")
        return {}
    found = {}
    for r in rows:
        crid = (r.get("serieslink") or r.get("comment") or "").strip()
        if crid.startswith("crid://"):
            found[crid] = r.get("uuid")
    return found


def sync_series_rules(playlist, resident: str) -> None:
    """Make TVHeadend's standing rules match the playlist — add what is new,
    remove what the resident is no longer following."""
    wanted = series_crids(playlist)
    if len(wanted) > TV_MAX_SERIES:
        _log(f"[TV] {len(wanted)} series requested; keeping the first {TV_MAX_SERIES} "
             f"— one tuner cannot honour more without clashes")
        wanted = wanted[:TV_MAX_SERIES]

    have = existing_autorecs()

    for crid in wanted:
        if crid in have:
            continue
        conf = {
            "enabled": True,
            "name": f"{resident}: {crid.rsplit('/', 1)[-1]}",
            "serieslink": crid,
            "comment": crid,
            "maxcount": TV_KEEP_EPISODES,
            "removal": 0,
        }
        try:
            tvh("/api/dvr/autorec/create", conf=json.dumps(conf))
            _log(f"[TV] Following series {crid} for {resident} "
                 f"(keeping {TV_KEEP_EPISODES} episodes)")
        except Exception as e:
            _log(f"[TV] Could not create rule for {crid}: {e}")

    for crid, uuid in have.items():
        if crid not in wanted:
            try:
                tvh("/api/idnode/delete", uuid=uuid)
                _log(f"[TV] Stopped following {crid} — no longer on the playlist")
            except Exception as e:
                _log(f"[TV] Could not remove rule {crid}: {e}")


# ---------------------------------------------------------------------------
# finished recordings -> the video cache
# ---------------------------------------------------------------------------

def register_finished(resident: str) -> int:
    """Register completed recordings so they play like any cached video.

    A finished recording is just a file on disk, and the device already knows
    how to play those, so nothing about playback needs to change.
    """
    try:
        rows = tvh("/api/dvr/entry/grid_finished", limit="200").get("entries", [])
    except Exception as e:
        _log(f"[TV] Could not list recordings: {e}")
        return 0

    added = 0
    for r in rows:
        path = r.get("filename")
        if not path or not os.path.exists(path):
            continue
        title = (r.get("disp_title") or "Recording").strip()
        sub = (r.get("disp_subtitle") or "").strip()
        if sub:
            title = f"{title} — {sub}"
        # uuid is stable for the life of the recording, so re-running this does
        # not create duplicates.
        source_id = r.get("uuid")
        try:
            size = os.path.getsize(path)
        except OSError:
            continue
        if size < cache_db.VIDEO_CACHE_MIN_FILE_SIZE_BYTES:
            continue
        try:
            vid = cache_db.register_video(
                resident, "tv", source_id, title, path,
                filesize_bytes=size,
                duration_seconds=int(r.get("duration") or 0) or None,
            )
            if vid:
                added += 1
        except Exception as e:
            _log(f"[TV] Could not register '{title}': {e}")
    if added:
        _log(f"[TV] Registered {added} recording(s) for {resident}")
    return added


def sync_once(get_playlist, resident: str):
    if not TVH_URL:
        return
    try:
        playlist = get_playlist() or []
    except Exception as e:
        _log(f"[TV] Could not read playlist: {e}")
        return
    sync_series_rules(playlist, resident)
    sync_one_offs(playlist)
    register_finished(resident)


def start(get_playlist, get_resident):
    """Begin syncing. Inert without a tuner, so devices without one are unaffected."""
    if not TVH_URL:
        return False

    def _run():
        time.sleep(120)   # let TVHeadend settle after boot
        while True:
            try:
                resident = get_resident()
                if resident:
                    sync_once(get_playlist, resident)
            except Exception as e:
                _log(f"[TV] Sync error: {e}")
            time.sleep(TV_SYNC_INTERVAL)

    threading.Thread(target=_run, name="TvRecorder", daemon=True).start()
    _log(f"[TV] Recorder sync started (every {TV_SYNC_INTERVAL // 60} min)")
    return True
