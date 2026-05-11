#!/usr/bin/env python3
"""
Backend sync for the Pi-local video cache.

NOTE: This module depends on cache_db.py and video_downloader.py,
which are added by the 'feature/local-video-cache' branch. When both
feature branches are merged to main (or co-deployed on a test
branch), this module activates the portal's view/control of each
Pi's cache.

Integration — add to media_button_pi.py:main() inside the existing
cache subsystem startup block (the one that calls cache_db.init_db()
and video_downloader.start()):

    import cache_sync  # at top of file
    ...
    cache_sync.start(
        api_base=API_BASE,
        device_id=DEVICE_ID,
        device_key=DEVICE_KEY,
        engine=ENGINE,
        is_online=wifi_healthy,
    )

Two cycles run on a single daemon thread:

  1. Snapshot push (default every 5 minutes): for each resident the Pi
     has cache rows for, POST the full inventory to
     /api/device/{deviceId}/cache/snapshot. The backend replaces its
     mirror of (this device, this resident) on each call so deletes the
     Pi has applied propagate automatically.

  2. Command poll (default every 30 seconds when online): GET pending
     commands from the backend, execute, and POST an ack with the
     result. Three command types are handled:
       - delete_video: removes file from disk + cache_db row.
       - play_video : starts a manual session with the cached file as a
                       single-item playlist.
       - force_term : registers/updates the term and kicks the
                       downloader so the worker picks it up promptly.

Throughput knobs are env-var-tunable so a flaky link can be slowed.
"""
import json
import os
import threading
import time
from datetime import datetime, timezone

import requests

import cache_db
import video_downloader

SYNC_INTERVAL = int(os.getenv("CACHE_SYNC_INTERVAL", "300"))            # 5 min
COMMAND_POLL_INTERVAL = int(os.getenv("CACHE_SYNC_COMMAND_INTERVAL", "30"))
REQUEST_TIMEOUT = int(os.getenv("CACHE_SYNC_REQUEST_TIMEOUT", "15"))


def _log(msg: str):
    try:
        ts = datetime.now().isoformat(timespec="seconds")
        print(f"[{ts}] {msg}")
    except Exception:
        print(msg)


def _iso_utc(value) -> str | None:
    """Coerce a sqlite TEXT timestamp (or None) to an ISO-8601 string the
    backend can parse as a DateTimeOffset. cached_videos stores
    `YYYY-MM-DD HH:MM:SS` in UTC (SQLite CURRENT_TIMESTAMP). We tag it with
    a Z so the server doesn't reinterpret as local time."""
    if not value:
        return None
    s = str(value).strip()
    if "T" not in s:
        s = s.replace(" ", "T", 1)
    if not s.endswith("Z") and "+" not in s[10:]:
        s = s + "Z"
    return s


def _resident_inventory(resident: str) -> list[dict]:
    """Build the snapshot list for one resident. Joins cached_videos to
    its first linked term (if any) so the portal can show what the video
    was downloaded for. Inlined SQL rather than living in cache_db so
    cache_db stays focused on cache CRUD."""
    sql = """
        SELECT v.id, v.source, v.source_id, v.title, v.filesize_bytes,
               v.duration_seconds, v.downloaded_at, v.last_played_at,
               v.play_count, v.protected,
               (SELECT t.term
                FROM term_videos tv
                JOIN playlist_terms t ON t.id = tv.term_id
                WHERE tv.video_id = v.id
                ORDER BY tv.added_at ASC
                LIMIT 1) AS term
        FROM cached_videos v
        WHERE v.resident = ?
        ORDER BY v.id
    """
    with cache_db._lock:
        rows = list(cache_db.get_conn().execute(sql, (resident,)))
    out = []
    for r in rows:
        out.append({
            "source": r["source"],
            "sourceId": r["source_id"],
            "title": r["title"] or r["source_id"],
            "term": r["term"],
            "filesizeBytes": r["filesize_bytes"],
            "durationSeconds": r["duration_seconds"],
            "downloadedAt": _iso_utc(r["downloaded_at"]) or _iso_utc(datetime.now(timezone.utc)),
            "lastPlayedAt": _iso_utc(r["last_played_at"]),
            "playCount": int(r["play_count"] or 0),
            "protected": bool(r["protected"]),
        })
    return out


def _residents_with_cache() -> list[str]:
    with cache_db._lock:
        rows = cache_db.get_conn().execute(
            "SELECT DISTINCT resident FROM cached_videos"
        ).fetchall()
    return [r["resident"] for r in rows if r["resident"]]


# -------------------------------------------------------------------------
# Command executors
# -------------------------------------------------------------------------
def _exec_delete_video(payload: dict, engine) -> tuple[str, dict]:
    source = payload.get("source") or "yt"
    source_id = payload.get("sourceId") or payload.get("source_id")
    resident = payload.get("resident")
    if not source_id or not resident:
        return "failed", {"reason": "missing source_id/resident in payload"}

    with cache_db._lock:
        row = cache_db.get_conn().execute(
            "SELECT id, filepath, protected FROM cached_videos "
            "WHERE resident = ? AND source = ? AND source_id = ?",
            (resident, source, source_id),
        ).fetchone()
    if not row:
        return "applied", {"note": "row already absent on Pi"}
    if int(row["protected"]):
        return "failed", {"reason": "protected (family) row cannot be deleted"}
    fp = row["filepath"]
    if engine is not None and getattr(engine, "currently_playing_filepath", None) == fp:
        return "failed", {"reason": "file is currently playing"}
    try:
        if fp and os.path.exists(fp):
            os.remove(fp)
    except Exception as e:
        return "failed", {"reason": f"unlink: {e}"}
    cache_db.delete_video(int(row["id"]))
    return "applied", {"deleted_id": int(row["id"]), "deleted_path": fp}


def _exec_play_video(payload: dict, engine) -> tuple[str, dict]:
    if engine is None:
        return "failed", {"reason": "no engine available"}
    source = payload.get("source") or "yt"
    source_id = payload.get("sourceId") or payload.get("source_id")
    resident = payload.get("resident")
    if not source_id or not resident:
        return "failed", {"reason": "missing source_id/resident in payload"}

    with cache_db._lock:
        row = cache_db.get_conn().execute(
            "SELECT id, filepath, title FROM cached_videos "
            "WHERE resident = ? AND source = ? AND source_id = ?",
            (resident, source, source_id),
        ).fetchone()
    if not row:
        return "failed", {"reason": "video no longer cached"}
    fp = row["filepath"]
    if not fp or not os.path.exists(fp):
        return "failed", {"reason": "file missing on disk"}

    item = {
        "type": "cached",
        "id": int(row["id"]),
        "filepath": fp,
        "name": row["title"] or source_id,
    }
    try:
        engine.start_manual_session(
            resident,
            playlist_override=[item],
            start_index=0,
            ordered=True,
        )
    except Exception as e:
        return "failed", {"reason": f"engine: {e}"}
    return "applied", {"started": True, "filepath": fp}


def _exec_force_term(payload: dict, engine) -> tuple[str, dict]:
    """Register the term for the resident (or refresh its last_seen) and
    kick the downloader. The worker will pick up the under-cap term on
    its next iteration. If the term is already at its cap this command
    is a no-op until cap-eviction frees a slot; that's by design."""
    resident = payload.get("resident")
    term = payload.get("term")
    if not resident or not term:
        return "failed", {"reason": "missing resident/term in payload"}
    try:
        cache_db.register_term(resident, term, {"type": "youtube", "query": term})
        cache_db.record_term_success(cache_db.term_id_for(resident, term) or -1)
        video_downloader.kick()
    except Exception as e:
        return "failed", {"reason": f"register/kick: {e}"}
    return "applied", {"term": term}


_DISPATCH = {
    "delete_video": _exec_delete_video,
    "play_video": _exec_play_video,
    "force_term": _exec_force_term,
}


# -------------------------------------------------------------------------
# Worker
# -------------------------------------------------------------------------
class CacheSync:
    def __init__(
        self,
        *,
        api_base: str,
        device_id: str,
        device_key: str,
        engine,
        is_online=None,
    ):
        self.api_base = api_base.rstrip("/")
        self.device_id = device_id
        self.device_key = device_key
        self.engine = engine
        self.is_online = is_online
        self._stop = threading.Event()
        self._wake = threading.Event()
        self._thread: threading.Thread | None = None
        self._last_snapshot_at = 0.0

    def start(self):
        if self._thread and self._thread.is_alive():
            return
        if not (self.api_base and self.device_id and self.device_key):
            _log("[CACHE-SYNC] API_BASE / DEVICE_ID / DEVICE_KEY not configured; not starting")
            return
        self._thread = threading.Thread(target=self._run, name="CacheSync", daemon=True)
        self._thread.start()
        _log("[CACHE-SYNC] Worker started")

    def stop(self):
        self._stop.set()
        self._wake.set()

    def kick(self):
        self._wake.set()

    def _headers(self) -> dict:
        return {"X-DEVICE-KEY": self.device_key, "Content-Type": "application/json"}

    def _online(self) -> bool:
        if self.is_online is None:
            return True
        try:
            return bool(self.is_online())
        except Exception:
            return True  # be lenient if the probe itself fails

    def _push_one_resident(self, resident: str):
        videos = _resident_inventory(resident)
        payload = {
            "snapshotAt": _iso_utc(datetime.now(timezone.utc)),
            "resident": resident,
            "videos": videos,
        }
        url = f"{self.api_base}/api/device/{self.device_id}/cache/snapshot"
        try:
            r = requests.post(url, json=payload, headers=self._headers(),
                              timeout=REQUEST_TIMEOUT)
            if r.status_code >= 400:
                _log(f"[CACHE-SYNC] Snapshot push failed for {resident} "
                     f"({r.status_code}): {r.text[:200]}")
            else:
                _log(f"[CACHE-SYNC] Snapshot pushed for {resident}: "
                     f"{len(videos)} video(s)")
        except Exception as e:
            _log(f"[CACHE-SYNC] Snapshot push error for {resident}: {e}")

    def _push_snapshots(self):
        for resident in _residents_with_cache():
            if self._stop.is_set():
                return
            self._push_one_resident(resident)
        self._last_snapshot_at = time.time()

    def _poll_commands(self):
        url = f"{self.api_base}/api/device/{self.device_id}/cache/commands"
        try:
            r = requests.get(url, headers=self._headers(), timeout=REQUEST_TIMEOUT)
        except Exception as e:
            _log(f"[CACHE-SYNC] Command poll error: {e}")
            return
        if r.status_code >= 400:
            _log(f"[CACHE-SYNC] Command poll failed ({r.status_code}): {r.text[:200]}")
            return
        try:
            cmds = r.json() or []
        except Exception:
            _log("[CACHE-SYNC] Command poll: non-JSON response")
            return

        for cmd in cmds:
            cmd_id = cmd.get("id")
            cmd_type = cmd.get("commandType") or cmd.get("CommandType")
            payload = cmd.get("payload") or cmd.get("Payload") or {}
            if isinstance(payload, str):
                try:
                    payload = json.loads(payload)
                except Exception:
                    payload = {}
            handler = _DISPATCH.get(cmd_type)
            if not handler:
                _log(f"[CACHE-SYNC] Unknown command type: {cmd_type}")
                self._ack(cmd_id, "failed", {"reason": f"unknown type: {cmd_type}"})
                continue
            try:
                status, result = handler(payload, self.engine)
            except Exception as e:
                status, result = "failed", {"reason": f"handler crash: {e}"}
            _log(f"[CACHE-SYNC] Command {cmd_type} → {status}: {result}")
            self._ack(cmd_id, status, result)
            # Successful state-changing commands invalidate our snapshot,
            # so push fresh state right after rather than waiting up to
            # SYNC_INTERVAL for the next cycle.
            if status == "applied" and cmd_type in {"delete_video", "play_video"}:
                resident = payload.get("resident")
                if resident:
                    self._push_one_resident(resident)

    def _ack(self, command_id, status: str, result: dict):
        if not command_id:
            return
        url = f"{self.api_base}/api/device/{self.device_id}/cache/commands/{command_id}/ack"
        try:
            requests.post(
                url,
                json={"status": status, "result": result},
                headers=self._headers(),
                timeout=REQUEST_TIMEOUT,
            )
        except Exception as e:
            _log(f"[CACHE-SYNC] Ack failed for {command_id}: {e}")

    def _run(self):
        # Stagger the first snapshot so we don't fight engine startup.
        self._stop.wait(15)
        while not self._stop.is_set():
            online = self._online()
            now = time.time()
            try:
                if online:
                    if now - self._last_snapshot_at >= SYNC_INTERVAL:
                        self._push_snapshots()
                    self._poll_commands()
            except Exception as e:
                _log(f"[CACHE-SYNC] Loop error: {e}")
            self._wake.wait(COMMAND_POLL_INTERVAL)
            self._wake.clear()


_worker: CacheSync | None = None


def start(api_base: str, device_id: str, device_key: str, engine, is_online=None) -> CacheSync:
    global _worker
    if _worker is None:
        _worker = CacheSync(
            api_base=api_base,
            device_id=device_id,
            device_key=device_key,
            engine=engine,
            is_online=is_online,
        )
    _worker.start()
    return _worker


def kick():
    if _worker is not None:
        _worker.kick()
