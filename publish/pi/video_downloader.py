#!/usr/bin/env python3
"""
Background downloader for the Pi-local video cache.

Owns a worker thread that picks the (resident, term) pair with the fewest
cached videos, resolves a YouTube candidate via yt-dlp, downloads it to
.data/video_cache/yt_<videoid>.mp4 and records it in cache_db. Pauses
completely while VLC is actively playing or the remote menu is open;
throttles to VIDEO_CACHE_THROTTLE_RATE if a download was already in-flight
when playback began.

==========================================================================
Future extension point — central curated content (post-merge)
==========================================================================
The module is deliberately split into two stages so a future central
service that serves pre-curated videos from Azure Blob can be added in
one place without touching anything else:

  resolve_download_candidates(term, resident, item_blob) -> [Candidate ...]
      decides *what* to download. Returns one or more Candidate records,
      tried in order.

  download_candidate(candidate, out_path, throttle=...) -> (ok, title, dur)
      decides *how* to fetch a Candidate's bytes onto disk.

To plug in the central system later:

  1. Extend resolve_download_candidates so that if item_blob carries a
     'central_curated_id' (or a hit against the central API), it returns
     a Candidate(source='azure', source_id=<curated_id>,
     download_url=<blob_sas_url>) *first*, ahead of any YouTube fallback.

  2. Extend download_candidate to handle source=='azure' by streaming
     the blob via requests instead of yt-dlp.

The schema already carries central_curated_id on cached_videos and the
source field accepts arbitrary strings, so no DB migration is needed.
No other module in this branch reads cached_videos.source, so adding new
values is forward-compatible.
==========================================================================
"""
import json
import os
import random
import subprocess
import sys
import threading
import time
from dataclasses import dataclass
from datetime import datetime

import cache_db

# -------------------------------------------------------------------------
# Config — mirrors media_button_pi.py defaults; resolved at import time
# -------------------------------------------------------------------------
YT_DLP_BIN = os.getenv("YT_DLP_BIN", os.path.join(os.path.dirname(sys.executable), "yt-dlp"))
YT_FORMAT = os.getenv("YT_FORMAT", "bv*[ext=mp4][height<=720][fps<=30][vcodec^=avc1]+ba/b")
YT_EXTRACTOR_ARGS = os.getenv("YT_EXTRACTOR_ARGS", "youtube:player_client=web")
YT_FORCE_IPV4 = os.getenv("YT_FORCE_IPV4", "0") == "1"
YT_COOKIES_FILE = os.getenv("YT_COOKIES_FILE", "")
YT_COOKIES_BROWSER = os.getenv("YT_COOKIES_BROWSER", "")

VIDEO_CACHE_THROTTLE_RATE = os.getenv("VIDEO_CACHE_THROTTLE_RATE", "500K")
DOWNLOADER_SLEEP_BETWEEN = float(os.getenv("VIDEO_CACHE_DOWNLOAD_SLEEP", "5"))
DOWNLOADER_IDLE_SLEEP = float(os.getenv("VIDEO_CACHE_DOWNLOAD_IDLE_SLEEP", "60"))
DOWNLOADER_FAILURE_COOLDOWN = int(os.getenv("VIDEO_CACHE_FAILURE_COOLDOWN", "3600"))
DOWNLOADER_FAILURE_THRESHOLD = int(os.getenv("VIDEO_CACHE_FAILURE_THRESHOLD", "3"))
DOWNLOADER_MAX_CANDIDATES = int(os.getenv("VIDEO_CACHE_MAX_CANDIDATES", "25"))


def _log(msg: str):
    try:
        ts = datetime.now().isoformat(timespec="seconds")
        print(f"[{ts}] {msg}")
    except Exception:
        print(msg)


# -------------------------------------------------------------------------
# Candidate resolution
# -------------------------------------------------------------------------
@dataclass
class Candidate:
    source: str          # 'yt' (Phase 2 only; 'azure' reserved for Phase 5+)
    source_id: str       # YouTube video id, or future curated id
    download_url: str    # what gets handed to the fetcher
    title_hint: str | None = None


def _pick_random_youtube_id(query: str, avoid_ids: set[str]) -> str | None:
    """Local copy of pick_random_youtube_id from media_button_pi — same shape,
    duplicated to avoid pulling in tkinter/vlc/bleak when this module is imported."""
    search = f"ytsearch{DOWNLOADER_MAX_CANDIDATES}:{query}"
    cmd = [YT_DLP_BIN, "--get-id", "--flat-playlist", search]
    try:
        ids = subprocess.check_output(cmd, text=True, timeout=60).splitlines()
    except subprocess.TimeoutExpired:
        _log("[DOWNLOADER] yt-dlp search timed out")
        return None
    except subprocess.CalledProcessError as e:
        _log(f"[DOWNLOADER] yt-dlp search failed: {e}")
        return None
    ids = [i.strip() for i in ids if i.strip()]
    random.shuffle(ids)
    for vid in ids:
        if vid not in avoid_ids:
            return vid
    return ids[0] if ids else None


def resolve_download_candidates(term: str, resident: str, item_blob: dict | None) -> list[Candidate]:
    """
    Given a playlist term, return zero or more Candidate descriptors to attempt.

    Phase 2 produces YouTube candidates only. Phase 5+ extension point: if
    item_blob carries a central_curated_id and the central system serves a
    media_url, prepend an Azure Candidate here and return it first. No other
    module needs to change to add a new source.
    """
    avoid = cache_db.cached_source_ids_for_term(
        cache_db.term_id_for(resident, term) or -1
    )

    # Some playlist items are direct video URLs rather than search queries.
    if isinstance(item_blob, dict):
        url = item_blob.get("url") or item_blob.get("mediaUrl")
        item_type = (item_blob.get("type") or "").lower()
        if url and item_type == "video":
            # Direct video URL — for now we only handle YouTube URLs the same
            # way play_youtube does; non-YouTube URLs are skipped (no cache).
            if "youtube.com" in url or "youtu.be" in url:
                vid = _extract_youtube_id_from_url(url)
                if vid and vid not in avoid:
                    return [Candidate(source="yt", source_id=vid,
                                      download_url=f"https://www.youtube.com/watch?v={vid}")]
            return []

    # If the term itself is a URL (e.g. a live radio stream), there's nothing
    # to cache — skip rather than searching YouTube for the URL string.
    if term.startswith("http://") or term.startswith("https://"):
        if "youtube.com" in term or "youtu.be" in term:
            vid = _extract_youtube_id_from_url(term)
            if vid and vid not in avoid:
                return [Candidate(source="yt", source_id=vid,
                                  download_url=f"https://www.youtube.com/watch?v={vid}")]
        return []

    # Default: treat the term string as a YouTube search query.
    vid = _pick_random_youtube_id(term, avoid)
    if not vid:
        return []
    return [Candidate(source="yt", source_id=vid,
                      download_url=f"https://www.youtube.com/watch?v={vid}")]


def _extract_youtube_id_from_url(url: str) -> str | None:
    """Cheap parser for the youtube.com/watch?v=... and youtu.be/... forms."""
    try:
        from urllib.parse import urlparse, parse_qs
        p = urlparse(url)
        if p.netloc.endswith("youtu.be"):
            return p.path.lstrip("/") or None
        if "youtube.com" in p.netloc:
            return (parse_qs(p.query).get("v") or [None])[0]
    except Exception:
        return None
    return None


# -------------------------------------------------------------------------
# Candidate download (decoupled from resolution; matches Phase 5 brief)
# -------------------------------------------------------------------------
def _extractor_chain() -> list[str | None]:
    """Same fallback order play_youtube uses, mirrored here for downloads."""
    chain: list[str | None] = []
    if YT_EXTRACTOR_ARGS:
        chain.append(YT_EXTRACTOR_ARGS)
    chain.extend([
        "youtube:player_client=web_creator",
        "youtube:player_client=ios",
        "youtube:player_client=mweb",
        "youtube:player_client=android",
        None,
    ])
    # Dedup while preserving order
    seen: set = set()
    out: list[str | None] = []
    for x in chain:
        if x in seen:
            continue
        seen.add(x)
        out.append(x)
    return out


def _yt_dlp_metadata(url: str) -> tuple[str | None, int | None]:
    """Fetch title + duration without downloading. Returns (title, duration_seconds)."""
    for extractor in _extractor_chain():
        cmd = [YT_DLP_BIN, "--print", "%(title)s", "--print", "%(duration)s", "--skip-download"]
        if extractor:
            cmd += ["--extractor-args", extractor]
        if YT_FORCE_IPV4:
            cmd.append("--force-ipv4")
        if YT_COOKIES_BROWSER:
            cmd += ["--cookies-from-browser", YT_COOKIES_BROWSER]
        elif YT_COOKIES_FILE and os.path.isfile(YT_COOKIES_FILE):
            cmd += ["--cookies", YT_COOKIES_FILE]
        cmd.append(url)
        try:
            lines = subprocess.check_output(cmd, text=True, timeout=60).splitlines()
            title = lines[0].strip() if lines else None
            dur = None
            if len(lines) > 1 and lines[1].strip().isdigit():
                dur = int(lines[1].strip())
            return title, dur
        except Exception:
            continue
    return None, None


def download_candidate(
    candidate: Candidate,
    out_path: str,
    *,
    throttle: bool,
) -> tuple[bool, str | None, int | None]:
    """
    Fetch a single Candidate to out_path. Tries the extractor fallback chain
    in order. Returns (success, title, duration_seconds).
    """
    if candidate.source != "yt":
        # Phase 5+ would add: source == 'azure' → blob fetch via requests.
        _log(f"[DOWNLOADER] Unsupported source: {candidate.source}")
        return False, None, None

    title, duration = _yt_dlp_metadata(candidate.download_url)

    os.makedirs(os.path.dirname(out_path), exist_ok=True)

    for extractor in _extractor_chain():
        # yt-dlp uses <name>.<ext>.part for in-progress files by default and
        # renames to the final name on success. We rely on that so the GC's
        # disk-consistency sweep doesn't match an in-flight download as a
        # dangling .mp4 and delete it mid-write.
        cmd = [
            YT_DLP_BIN,
            "-f", YT_FORMAT,
            "--merge-output-format", "mp4",
            "-o", out_path,
            "--no-progress",
            "--no-playlist",
            "--no-mtime",
        ]
        if throttle and VIDEO_CACHE_THROTTLE_RATE:
            cmd += ["--limit-rate", VIDEO_CACHE_THROTTLE_RATE]
        if extractor:
            cmd += ["--extractor-args", extractor]
        if YT_FORCE_IPV4:
            cmd.append("--force-ipv4")
        if YT_COOKIES_BROWSER:
            cmd += ["--cookies-from-browser", YT_COOKIES_BROWSER]
        elif YT_COOKIES_FILE and os.path.isfile(YT_COOKIES_FILE):
            cmd += ["--cookies", YT_COOKIES_FILE]
        cmd.append(candidate.download_url)

        _log(f"[DOWNLOADER] yt-dlp ({'throttled' if throttle else 'unthrottled'}) "
             f"extractor={extractor or 'default'} → {os.path.basename(out_path)}")
        try:
            subprocess.check_call(
                cmd, stdout=subprocess.DEVNULL, stderr=subprocess.STDOUT, timeout=1800,
            )
            if os.path.exists(out_path):
                size = os.path.getsize(out_path)
                if size >= cache_db.VIDEO_CACHE_MIN_FILE_SIZE_BYTES:
                    return True, title or candidate.title_hint or candidate.source_id, duration
                # Real video formats weren't available; yt-dlp's /b fallback
                # grabbed a storyboard or audio-only stub. Treat as failure.
                _log(f"[DOWNLOADER] yt-dlp produced an undersize file "
                     f"({size} B < {cache_db.VIDEO_CACHE_MIN_FILE_SIZE_BYTES} B); "
                     f"discarding and trying next extractor")
        except subprocess.TimeoutExpired:
            _log("[DOWNLOADER] yt-dlp timed out — trying next extractor")
        except subprocess.CalledProcessError as e:
            _log(f"[DOWNLOADER] yt-dlp failed (exit {e.returncode}) — trying next extractor")
        # Clean up the rejected/partial output (and any leftover .part) before
        # the next attempt.
        for path in (out_path, out_path + ".part"):
            try:
                if os.path.exists(path):
                    os.remove(path)
            except Exception:
                pass

    return False, None, None


# -------------------------------------------------------------------------
# Worker thread
# -------------------------------------------------------------------------
class DownloadWorker:
    def __init__(
        self,
        *,
        playback_active: threading.Event,
        menu_active: threading.Event | None = None,
        get_active_residents,           # callable -> list[str]
        is_online=None,                 # optional callable -> bool
        per_term_cap: int = cache_db.VIDEO_CACHE_PER_TERM_CAP,
    ):
        self._stop = threading.Event()
        self._wake = threading.Event()
        self.playback_active = playback_active
        self.menu_active = menu_active
        self.get_active_residents = get_active_residents
        # When provided, the worker checks this before each pick and stays
        # idle if it returns False. Spares the log from a parade of
        # yt-dlp failures while the Pi has no network.
        self.is_online = is_online
        self.per_term_cap = per_term_cap
        self._thread: threading.Thread | None = None

    def start(self):
        if self._thread and self._thread.is_alive():
            return
        cache_db.init_db()
        self._thread = threading.Thread(target=self._run, name="VideoCacheDownloader", daemon=True)
        self._thread.start()
        _log("[DOWNLOADER] Worker started")

    def stop(self):
        self._stop.set()
        self._wake.set()

    def kick(self):
        """Optional: signal the worker to recheck the queue immediately."""
        self._wake.set()

    def _pick_next_term(self) -> dict | None:
        """Pick the (resident, term) that most needs a download.
        Active residents (button currently held) get priority. Returns None
        if there's nothing to do or the Pi is currently offline."""
        if self.is_online is not None:
            try:
                if not self.is_online():
                    return None
            except Exception:
                # If the probe itself errors, treat as "unknown" and attempt
                # the pick — better to try and fail loudly than to stall.
                pass

        active = []
        try:
            active = self.get_active_residents() or []
        except Exception:
            active = []

        for resident in active:
            rows = cache_db.terms_below_cap(self.per_term_cap, resident=resident)
            for r in rows:
                if cache_db.term_in_cooldown(int(r["term_id"])):
                    continue
                return _row_to_dict(r)

        # Global pass for everyone else
        rows = cache_db.terms_below_cap(self.per_term_cap)
        for r in rows:
            if cache_db.term_in_cooldown(int(r["term_id"])):
                continue
            return _row_to_dict(r)
        return None

    def _run(self):
        while not self._stop.is_set():
            try:
                target = self._pick_next_term()
                if not target:
                    # Nothing to do — sleep longer; can be woken by kick()
                    self._wake.wait(DOWNLOADER_IDLE_SLEEP)
                    self._wake.clear()
                    continue

                # Hold off while video is playing or the remote menu is open
                while not self._stop.is_set():
                    if not self.playback_active.is_set() and not (self.menu_active and self.menu_active.is_set()):
                        break
                    self._stop.wait(timeout=1.0)
                if self._stop.is_set():
                    break

                self._download_one(target)
            except Exception as e:
                _log(f"[DOWNLOADER] Worker loop error: {e}")
                time.sleep(DOWNLOADER_SLEEP_BETWEEN)
                continue

            # Small gap between downloads so we don't hammer the network
            self._wake.wait(DOWNLOADER_SLEEP_BETWEEN)
            self._wake.clear()

    def _download_one(self, target: dict):
        term_id = int(target["term_id"])
        resident = target["resident"]
        term = target["term"]
        blob_raw = target.get("item_blob")
        try:
            blob = json.loads(blob_raw) if isinstance(blob_raw, str) else None
        except Exception:
            blob = None

        candidates = resolve_download_candidates(term, resident, blob)
        if not candidates:
            cooled = cache_db.record_term_failure(
                term_id, DOWNLOADER_FAILURE_COOLDOWN, DOWNLOADER_FAILURE_THRESHOLD
            )
            if cooled:
                _log(f"[DOWNLOADER] No candidates for term '{term}' (resident={resident}); "
                     f"parking term for {DOWNLOADER_FAILURE_COOLDOWN}s")
            return

        for cand in candidates:
            out_path = os.path.join(cache_db.VIDEO_CACHE_DIR, f"{cand.source}_{cand.source_id}.mp4")
            if os.path.exists(out_path):
                size = os.path.getsize(out_path)
                if size >= cache_db.VIDEO_CACHE_MIN_FILE_SIZE_BYTES:
                    # File already on disk but somehow not in DB — record it.
                    title = cand.title_hint or cand.source_id
                    vid_id = cache_db.register_video(
                        resident, cand.source, cand.source_id, title, out_path,
                        filesize_bytes=size,
                    )
                    cache_db.link_video_to_term(term_id, vid_id)
                    cache_db.record_term_success(term_id)
                    _log(f"[DOWNLOADER] Recovered existing file for term '{term}': {os.path.basename(out_path)}")
                    return
                # Undersize leftover from a previous failed attempt; clear it
                # so the download below isn't blocked by a stale file.
                try:
                    os.remove(out_path)
                except Exception:
                    pass

            throttle = self.playback_active.is_set() or bool(self.menu_active and self.menu_active.is_set())
            ok, title, duration = download_candidate(cand, out_path, throttle=throttle)
            if not ok:
                cooled = cache_db.record_term_failure(
                    term_id, DOWNLOADER_FAILURE_COOLDOWN, DOWNLOADER_FAILURE_THRESHOLD
                )
                if cooled:
                    _log(f"[DOWNLOADER] 3 consecutive failures for term '{term}' "
                         f"(resident={resident}); cooldown {DOWNLOADER_FAILURE_COOLDOWN}s")
                    return
                continue

            filesize = os.path.getsize(out_path)
            vid_id = cache_db.register_video(
                resident, cand.source, cand.source_id,
                title or cand.source_id,
                out_path,
                filesize_bytes=filesize,
                duration_seconds=duration,
            )
            cache_db.link_video_to_term(term_id, vid_id)
            cache_db.record_term_success(term_id)
            _log(f"[DOWNLOADER] Cached '{title or cand.source_id}' for term '{term}' "
                 f"(resident={resident}, {filesize // 1024} KB)")
            threading.Thread(target=_extract_thumb, args=(out_path,), daemon=True).start()
            return


def _extract_thumb(filepath: str) -> None:
    """Extract a JPEG thumbnail at ~5 s. No-op if already present or ffmpeg unavailable."""
    thumb = filepath + ".thumb.jpg"
    if os.path.exists(thumb):
        return
    try:
        subprocess.run(
            ["ffmpeg", "-ss", "5", "-i", filepath,
             "-vframes", "1", "-q:v", "3", "-vf", "scale=320:180", "-y", thumb],
            capture_output=True, timeout=20,
        )
    except Exception:
        pass


def _row_to_dict(r):
    if hasattr(r, "keys"):
        return {k: r[k] for k in r.keys()}
    return dict(r)


# Module-level singleton for convenience — main() constructs and starts.
_worker: DownloadWorker | None = None


def start(
    playback_active: threading.Event,
    get_active_residents,
    menu_active: threading.Event | None = None,
    is_online=None,
) -> DownloadWorker:
    global _worker
    if _worker is None:
        _worker = DownloadWorker(
            playback_active=playback_active,
            menu_active=menu_active,
            get_active_residents=get_active_residents,
            is_online=is_online,
        )
    _worker.start()
    return _worker


def kick():
    if _worker is not None:
        _worker.kick()
