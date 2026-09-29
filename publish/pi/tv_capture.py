"""Record television by driving the tuner directly.

TVHeadend keeps the programme guide well and schedules sensibly, but on this
hardware it cannot pull a stream off the busier multiplexes. Measured on Beech
House's tuner: BBC One's multiplex runs at 22 Mbit/s and TVHeadend gets not one
byte from it, while a 9 Mbit/s multiplex records fine. The tuner is not at
fault — dvbv5-zap captures that same multiplex at the full 22 Mbit/s — so the
capture is done here, and TVHeadend is left doing the part it does well.

There is one tuner, so a capture takes it exclusively: TVHeadend is stopped for
the length of the recording and started again afterwards, whatever happens.
That costs nothing the resident sees, because the guide is gathered on a six
hourly cycle and playback never touches the tuner.

Finished recordings are registered in the ordinary video cache, so the menu,
the beacon trigger and the garbage collector treat them exactly like a
downloaded video and nothing about playback needs to change.
"""

import os
import re
import shutil
import subprocess
import threading
import time
from datetime import datetime, timezone

import cache_db
from tv_recorder import TVH_URL, tvh

# Channel definitions must carry the video and audio PIDs or dvbv5-zap records
# an empty file, and only a scan resolves those. Scanning needs the tuner and
# takes a couple of minutes, so the result is kept and reused.
CHANNEL_CONF = os.path.join(cache_db.VIDEO_CACHE_DIR, "..", "dvb_channels.conf")
CHANNEL_CONF = os.path.abspath(CHANNEL_CONF)

# Broadcasters rarely start or finish on the advertised minute.
TV_PRE_ROLL = int(os.getenv("TV_PRE_ROLL", "60"))
TV_POST_ROLL = int(os.getenv("TV_POST_ROLL", "180"))
TV_KEEP_EPISODES = int(os.getenv("TV_KEEP_EPISODES", "3"))
# A programme already under way is still worth having if barely any of it has
# gone — a minute lost off forty is not a reason to record nothing. Past this
# share of the running time it is not the programme any more, and a resident
# sitting down to half a episode is worse than finding it absent.
TV_LATE_JOIN = float(os.getenv("TV_LATE_JOIN", "0.2"))
TV_MAX_HOURS = int(os.getenv("TV_MAX_HOURS", "4"))
# A recording needs room for itself and for the videos already on the disk.
TV_MIN_FREE_GB = float(os.getenv("TV_MIN_FREE_GB", "4"))

_capturing = threading.Lock()


def _log(msg: str):
    print(f"[{datetime.now().isoformat(timespec='seconds')}] {msg}", flush=True)


def _iso(ts) -> str:
    return datetime.fromtimestamp(int(ts), tz=timezone.utc).isoformat()


def _service(cmd: str, unit: str = "tvheadend") -> None:
    subprocess.run(["sudo", "-n", "systemctl", cmd, unit],
                   check=False, capture_output=True, timeout=90)


# ---------------------------------------------------------------------------
# channel definitions
# ---------------------------------------------------------------------------

def _conf_has(channel: str) -> bool:
    try:
        with open(CHANNEL_CONF, "r", encoding="utf-8", errors="replace") as f:
            return f"[{channel}]" in f.read()
    except OSError:
        return False


def _mux_frequency(channel: str) -> int | None:
    """The frequency carrying a channel, as TVHeadend already knows it."""
    try:
        services = tvh("/api/mpegts/service/grid", limit="800").get("entries", [])
        muxes = tvh("/api/mpegts/mux/grid", limit="100").get("entries", [])
    except Exception as e:
        _log(f"[TV] Could not read the mux list: {e}")
        return None
    by_name = {str(m.get("name")): m for m in muxes}
    for s in services:
        if str(s.get("svcname") or "") == channel:
            mux = by_name.get(str(s.get("multiplex") or ""))
            if mux and mux.get("frequency"):
                return int(mux["frequency"])
    return None


def ensure_channel(channel: str) -> bool:
    """Make sure the channel is defined, scanning its multiplex if it is not.

    The tuner is needed for the scan, so TVHeadend stands aside for it — the
    same arrangement as a capture, and for the same reason.
    """
    if _conf_has(channel):
        return True
    freq = _mux_frequency(channel)
    if not freq:
        _log(f"[TV] No multiplex known for '{channel}' — cannot record it")
        return False

    _log(f"[TV] Learning the channels on {freq // 1000000} MHz "
         f"(needed for '{channel}')")
    initial = CHANNEL_CONF + ".initial"
    found = CHANNEL_CONF + ".new"
    try:
        with open(initial, "w", encoding="utf-8") as f:
            f.write("[scan]\n"
                    "\tDELIVERY_SYSTEM = DVBT\n"
                    f"\tFREQUENCY = {freq}\n"
                    "\tBANDWIDTH_HZ = 8000000\n"
                    "\tCODE_RATE_HP = AUTO\n"
                    "\tCODE_RATE_LP = AUTO\n"
                    "\tMODULATION = QAM/AUTO\n"
                    "\tTRANSMISSION_MODE = AUTO\n"
                    "\tGUARD_INTERVAL = AUTO\n"
                    "\tHIERARCHY = AUTO\n"
                    "\tINVERSION = AUTO\n")
        _service("stop")
        time.sleep(3)
        subprocess.run(
            ["dvbv5-scan", "-F", "-T", "2", "-o", found, initial],
            check=False, capture_output=True, timeout=300)
    except Exception as e:
        _log(f"[TV] Scan of {freq} failed: {e}")
        return False
    finally:
        _service("start")

    if not os.path.exists(found) or os.path.getsize(found) == 0:
        _log(f"[TV] Scan of {freq // 1000000} MHz found nothing")
        return False

    # Merge rather than replace, so channels learned from other multiplexes
    # are not lost each time a new one is needed.
    try:
        existing = ""
        if os.path.exists(CHANNEL_CONF):
            with open(CHANNEL_CONF, "r", encoding="utf-8", errors="replace") as f:
                existing = f.read()
        with open(found, "r", encoding="utf-8", errors="replace") as f:
            fresh = f.read()
        keep = [b for b in re.split(r"(?=^\[)", existing, flags=re.M)
                if b.strip() and f"[{channel}]" not in b]
        with open(CHANNEL_CONF, "w", encoding="utf-8") as f:
            f.write("".join(keep) + ("\n" if keep else "") + fresh)
        os.remove(found)
        os.remove(initial)
    except OSError as e:
        _log(f"[TV] Could not store the channel list: {e}")
        return False

    ok = _conf_has(channel)
    _log(f"[TV] {'Learned' if ok else 'Did not find'} '{channel}' "
         f"on {freq // 1000000} MHz")
    return ok


# ---------------------------------------------------------------------------
# what to record
# ---------------------------------------------------------------------------

def _wanted(playlist) -> tuple[list[str], list[tuple[str, str]]]:
    """The series CRIDs and the (channel, ISO start) one-offs a playlist asks for."""
    series, one_offs = [], []
    for item in playlist or []:
        val = (item.get("url") or item.get("query") or "") if isinstance(item, dict) else str(item)
        val = (val or "").strip()
        low = val.lower()
        if low.startswith("series:"):
            crid = val.split(":", 1)[1].strip()
            if crid and crid not in series:
                series.append(crid)
        elif low.startswith("programme:"):
            body = val.split(":", 1)[1]
            if "@" not in body:
                continue
            channel, _, start = body.rpartition("@")
            if channel.strip() and start.strip():
                one_offs.append((channel.strip(), start.strip()))
    return series, one_offs


def planned(playlist) -> list[dict]:
    """Everything the playlist asks for that is still worth recording.

    A programme that has only just begun is caught from where it is, and says
    so in its title, so nobody is told they have the whole thing. One too far
    gone is left alone: a fragment presented as the programme is worse than
    nothing, and one already finished would capture nothing at all while
    looking to staff as though it had worked.
    """
    series, one_offs = _wanted(playlist)
    if not series and not one_offs:
        return []
    try:
        # The guide runs to nearly nine thousand events over a week or
        # more. A short window would hide next week's episodes and make
        # a series look like it had only a couple left.
        events = tvh("/api/epg/events/grid", limit="20000").get("entries", [])
    except Exception as e:
        _log(f"[TV] Could not read the guide: {e}")
        return []

    now = time.time()
    out, seen = [], set()

    def add(e, series_key=None) -> bool:
        # Keyed on the advertised start even when joining late, so catching the
        # rest of a programme does not record it again from the guide.
        start, stop = int(e.get("start") or 0), int(e.get("stop") or 0)
        uid = f"{e.get('channelName')}@{_iso(start)}"
        if uid in seen:
            return False
        title = str(e.get("title") or "Recording").strip()

        joined = 0
        if start <= now:
            gone, length = now - start, max(stop - start, 1)
            if gone > length * TV_LATE_JOIN:
                _log(f"[TV] Too late for '{title}' — {gone / 60:.0f} of its "
                     f"{length // 60:.0f} minutes have gone")
                return False
            joined = max(1, int(gone // 60))
            start = int(now)

        seen.add(uid)
        out.append({
            "uid": uid,
            "channel": str(e.get("channelName") or ""),
            "title": title,
            "subtitle": str(e.get("subtitle") or "").strip(),
            "start": start,
            "stop": stop,
            "joined": joined,
            # What this belongs to, so retention can tell one soap's backlog
            # from another's. A one-off is its own series of one.
            "series_key": series_key or f"{e.get('channelName')}:{title}",
        })
        return True

    for crid in series:
        matches = sorted((e for e in events
                          if str(e.get("serieslinkUri") or "") == crid
                          and int(e.get("stop") or 0) > now),
                         key=lambda e: e.get("start") or 0)
        taken = 0
        for e in matches:
            if taken >= TV_KEEP_EPISODES:
                break
            if add(e, series_key=crid):
                taken += 1

    for channel, start_iso in one_offs:
        try:
            want = int(datetime.fromisoformat(start_iso.replace("Z", "+00:00")).timestamp())
        except ValueError:
            _log(f"[TV] Ignoring unparsable start time: {start_iso}")
            continue
        # A minute either way: the guide is revised as broadcast data firms up,
        # so demanding an exact match would drop legitimate recordings.
        e = next((x for x in events
                  if str(x.get("channelName") or "") == channel
                  and abs(int(x.get("start") or 0) - want) <= 60), None)
        if e:
            add(e)
        else:
            _log(f"[TV] No listing for '{channel}' at {start_iso} — it may have moved")

    return sorted(out, key=lambda p: p["start"])


# ---------------------------------------------------------------------------
# capture
# ---------------------------------------------------------------------------

def _already_have(resident: str, uid: str) -> bool:
    try:
        conn = cache_db.get_conn()
        row = conn.execute(
            "SELECT 1 FROM cached_videos WHERE resident = ? AND source = 'tv' AND source_id = ?",
            (resident, uid)).fetchone()
        return row is not None
    except Exception:
        return False


def capture(plan: dict, resident: str) -> bool:
    """Record one programme, giving the tuner back to TVHeadend afterwards."""
    channel, uid = plan["channel"], plan["uid"]
    if _already_have(resident, uid):
        return False
    if not ensure_channel(channel):
        return False

    joined = int(plan.get("joined") or 0)
    pre = 0 if joined else TV_PRE_ROLL
    seconds = plan["stop"] - plan["start"] + TV_POST_ROLL + pre
    if seconds <= 0 or seconds > TV_MAX_HOURS * 3600:
        _log(f"[TV] Refusing a {seconds // 60} minute recording of '{plan['title']}'")
        return False

    free_gb = shutil.disk_usage(cache_db.VIDEO_CACHE_DIR).free / (1024 ** 3)
    if free_gb < TV_MIN_FREE_GB:
        _log(f"[TV] Only {free_gb:.1f} GB free — not recording '{plan['title']}'")
        return False

    safe = re.sub(r"[^A-Za-z0-9._-]+", "_", f"{plan['title']}_{uid}")[:120]
    path = os.path.join(cache_db.VIDEO_CACHE_DIR, f"tv_{safe}.ts")

    title = plan["title"]
    if plan["subtitle"]:
        # Broadcasters put a whole paragraph of billing in the subtitle. A menu
        # read from an armchair shows a line, so keep enough to tell one
        # episode from another and drop the rest.
        sub = plan["subtitle"]
        if len(sub) > 60:
            sub = sub[:57].rstrip(" ,.;:—-") + "…"
        title = f"{title} — {sub}"
    if joined:
        # On the end, where a carer reading the menu will see it, rather than
        # buried after a long subtitle.
        title = f"{title} (from {joined} min in)"

    _log(f"[TV] Recording '{title}' on {channel} for {seconds // 60} min")
    _service("stop")
    try:
        time.sleep(3)
        subprocess.run(
            ["dvbv5-zap", "-c", CHANNEL_CONF, "-o", path, "-t", str(seconds), channel],
            check=False, capture_output=True, timeout=seconds + 120)
    except subprocess.TimeoutExpired:
        _log(f"[TV] Recording of '{title}' overran and was stopped")
    except Exception as e:
        _log(f"[TV] Recording of '{title}' failed: {e}")
    finally:
        _service("start")

    try:
        size = os.path.getsize(path)
    except OSError:
        _log(f"[TV] Nothing was written for '{title}'")
        return False

    if size < cache_db.VIDEO_CACHE_MIN_FILE_SIZE_BYTES:
        _log(f"[TV] '{title}' captured only {size} bytes — discarding")
        try:
            os.remove(path)
        except OSError:
            pass
        return False

    try:
        cache_db.register_video(
            resident, "tv", uid, title, path,
            filesize_bytes=size,
            duration_seconds=plan["stop"] - plan["start"],
            series_key=plan.get("series_key"),
        )
    except Exception as e:
        _log(f"[TV] Could not register '{title}': {e}")
        return False

    _log(f"[TV] Recorded '{title}' ({size / (1024 ** 2):.0f} MB) — ready to play")
    return True


# ---------------------------------------------------------------------------

def _run(get_playlist, get_resident):
    time.sleep(150)          # let TVHeadend settle and gather a guide first
    while True:
        try:
            resident = get_resident()
            if resident:
                upcoming = planned(get_playlist() or [])
                now = time.time()
                due = [p for p in upcoming
                       if p["start"] - now <= 60 + TV_PRE_ROLL
                       and p["stop"] - now > 60
                       and not _already_have(resident, p["uid"])]
                if due and _capturing.acquire(blocking=False):
                    try:
                        capture(due[0], resident)
                    finally:
                        _capturing.release()
                elif upcoming and int(now) % 3600 < 60:
                    nxt = upcoming[0]
                    _log(f"[TV] Next recording: '{nxt['title']}' on {nxt['channel']} "
                         f"at {_iso(nxt['start'])} ({len(upcoming)} planned)")
        except Exception as e:
            _log(f"[TV] Scheduler error: {e}")
        time.sleep(60)


def start(get_playlist, get_resident):
    """Begin recording. Inert without a tuner, so other devices are unaffected."""
    if not TVH_URL:
        return False
    threading.Thread(target=_run, args=(get_playlist, get_resident),
                     name="TvCapture", daemon=True).start()
    _log("[TV] Capture scheduler started (checks every minute)")
    return True
