#!/usr/bin/env python3
"""
Garbage collection for the Pi-local video cache.

Three sweeps:
  1. Orphan sweep — cached_videos with no term_videos link and protected=0
     get their file deleted and their row removed. Protects family uploads
     (protected=1) and the currently-playing file.
  2. Per-term cap eviction — terms with more than VIDEO_CACHE_PER_TERM_CAP
     linked videos shed their oldest/LRU surplus. A minimum-age guard of
     VIDEO_CACHE_MIN_AGE_DAYS prevents same-week churn.
  3. Disk consistency — files in VIDEO_CACHE_DIR not referenced by any DB
     row are deleted. Catches crashed-mid-download fragments and manual rms
     that left the DB out of sync.

Reconciling terms when a new playlist arrives is handled in cache_db
(reconcile_resident_terms). This module only owns the file-deletion side.
"""
import os
import threading
import time
from datetime import datetime

import cache_db


GC_INTERVAL_SECONDS = int(os.getenv("VIDEO_CACHE_GC_INTERVAL", "1800"))  # 30 min


def _log(msg: str):
    try:
        ts = datetime.now().isoformat(timespec="seconds")
        print(f"[{ts}] {msg}")
    except Exception:
        print(msg)


def _safe_delete_file(path: str) -> bool:
    try:
        if os.path.exists(path):
            os.remove(path)
        return True
    except Exception as e:
        _log(f"[GC] Failed to delete {path}: {e}")
        return False


# -------------------------------------------------------------------------
# Sweep 1: orphans (videos with no term link)
# -------------------------------------------------------------------------
def run_orphan_sweep(currently_playing_filepath: str | None = None) -> int:
    rows = cache_db.orphan_videos()
    removed = 0
    for r in rows:
        fp = r["filepath"]
        if currently_playing_filepath and fp == currently_playing_filepath:
            _log(f"[GC] Skipping orphan currently in playback: {os.path.basename(fp)}")
            continue
        _safe_delete_file(fp)
        cache_db.delete_video(int(r["id"]))
        removed += 1
    if removed:
        _log(f"[GC] Removed {removed} orphan video(s)")
    return removed


# -------------------------------------------------------------------------
# Sweep 2: per-term cap eviction
# -------------------------------------------------------------------------
def run_cap_eviction(currently_playing_filepath: str | None = None) -> int:
    cap = cache_db.VIDEO_CACHE_PER_TERM_CAP
    min_age = cache_db.VIDEO_CACHE_MIN_AGE_DAYS
    removed = 0
    for term in cache_db.all_active_terms():
        surplus = cache_db.videos_over_cap_for_term(int(term["id"]), cap, min_age)
        for v in surplus:
            fp = v["filepath"]
            if currently_playing_filepath and fp == currently_playing_filepath:
                continue
            _safe_delete_file(fp)
            cache_db.delete_video(int(v["id"]))
            removed += 1
    if removed:
        _log(f"[GC] Evicted {removed} over-cap video(s)")
    return removed


# -------------------------------------------------------------------------
# Sweep 3: disk consistency (file on disk but no DB row)
# -------------------------------------------------------------------------
def run_disk_consistency_sweep(currently_playing_filepath: str | None = None) -> int:
    cache_dir = cache_db.VIDEO_CACHE_DIR
    if not os.path.isdir(cache_dir):
        return 0
    known = cache_db.cached_filepaths()
    removed = 0
    for name in os.listdir(cache_dir):
        if not name.endswith(".mp4"):
            continue
        full = os.path.join(cache_dir, name)
        if full in known:
            continue
        if currently_playing_filepath and full == currently_playing_filepath:
            continue
        if _safe_delete_file(full):
            removed += 1
    if removed:
        _log(f"[GC] Removed {removed} unreferenced file(s) from disk")
    return removed


# -------------------------------------------------------------------------
# Full sweep
# -------------------------------------------------------------------------
def run_full_sweep(currently_playing_filepath: str | None = None) -> dict[str, int]:
    """Run all three sweeps. Returns counts per sweep for logging/tests."""
    return {
        "orphans": run_orphan_sweep(currently_playing_filepath),
        "evicted": run_cap_eviction(currently_playing_filepath),
        "dangling": run_disk_consistency_sweep(currently_playing_filepath),
    }


# -------------------------------------------------------------------------
# Slow timer
# -------------------------------------------------------------------------
_timer_stop = threading.Event()
_timer_thread: threading.Thread | None = None


def start_timer(get_currently_playing=lambda: None, interval: int = GC_INTERVAL_SECONDS):
    """
    Run a daemon thread that does run_full_sweep every `interval` seconds.

    `get_currently_playing` is a callable returning the filepath VLC is
    currently rendering (or None) so the sweep never deletes a file out
    from under playback.
    """
    global _timer_thread
    if _timer_thread and _timer_thread.is_alive():
        return _timer_thread

    def _loop():
        # Stagger the first tick so we don't fight engine startup.
        if _timer_stop.wait(min(interval, 60)):
            return
        while not _timer_stop.is_set():
            try:
                fp = None
                try:
                    fp = get_currently_playing()
                except Exception:
                    fp = None
                run_full_sweep(fp)
            except Exception as e:
                _log(f"[GC] Timer sweep error: {e}")
            if _timer_stop.wait(interval):
                return

    _timer_thread = threading.Thread(target=_loop, name="VideoCacheGC", daemon=True)
    _timer_thread.start()
    _log(f"[GC] Timer started (interval {interval}s)")
    return _timer_thread


def stop_timer():
    _timer_stop.set()
