#!/usr/bin/env python3
"""
SQLite schema and helpers for the Pi-local video cache.

The cache is used by video_downloader.py (writes) and the Engine playback loop
(reads + last-played updates). One shared connection is held for the process,
guarded by an RLock; SQLite WAL mode keeps reads from blocking writes. Every
connection has PRAGMA foreign_keys = ON so the term_videos cascade works.

Phase 1 only: this module does not alter runtime playback. It just owns the
schema and the CRUD surface other modules will use.
"""
import json
import os
import sqlite3
import threading
import time
from datetime import datetime, timedelta

# Honour the host script's .data layout. If imported before media_button_pi.py
# has set DATA_DIR, fall back to a sibling .data directory next to this file.
_BASE_DIR = os.path.dirname(os.path.abspath(__file__))
_DEFAULT_DATA_DIR = os.path.join(_BASE_DIR, ".data")

VIDEO_CACHE_DB_PATH = os.getenv(
    "VIDEO_CACHE_DB_PATH",
    os.path.join(_DEFAULT_DATA_DIR, "video_cache.db"),
)
VIDEO_CACHE_DIR = os.getenv(
    "VIDEO_CACHE_DIR",
    os.path.join(_DEFAULT_DATA_DIR, "video_cache"),
)

# Per-term cap is read by both downloader (when deciding whether to enqueue
# more) and GC (when evicting). Keep the env var name aligned with the brief.
VIDEO_CACHE_PER_TERM_CAP = int(os.getenv("VIDEO_CACHE_PER_TERM_CAP", "5"))
VIDEO_CACHE_MIN_AGE_DAYS = int(os.getenv("VIDEO_CACHE_MIN_AGE_DAYS", "7"))

# Minimum file size for a download to count as a real video. yt-dlp's /b
# fallback in the format string can yield image-only storyboards or audio-only
# m4a tracks when YouTube's PO-token gate blocks the real video streams; those
# are typically a few tens of KB and would otherwise occupy a cache slot,
# masquerade as a cache hit, fail VLC playback, and force a stream fallback.
# 500 KB comfortably exceeds any storyboard but stays below even very short
# real clips.
VIDEO_CACHE_MIN_FILE_SIZE_BYTES = int(os.getenv("VIDEO_CACHE_MIN_FILE_SIZE_BYTES", "512000"))


def _log(msg: str):
    try:
        ts = datetime.now().isoformat(timespec="seconds")
        print(f"[{ts}] {msg}")
    except Exception:
        print(msg)


# -------------------------------------------------------------------------
# Connection management — single shared connection, RLock for writes
# -------------------------------------------------------------------------
_conn: sqlite3.Connection | None = None
_lock = threading.RLock()


def _connect() -> sqlite3.Connection:
    os.makedirs(os.path.dirname(VIDEO_CACHE_DB_PATH), exist_ok=True)
    os.makedirs(VIDEO_CACHE_DIR, exist_ok=True)
    conn = sqlite3.connect(
        VIDEO_CACHE_DB_PATH,
        check_same_thread=False,
        isolation_level=None,  # autocommit; explicit BEGIN where we need a txn
        timeout=10.0,
    )
    conn.row_factory = sqlite3.Row
    conn.execute("PRAGMA foreign_keys = ON;")
    conn.execute("PRAGMA journal_mode = WAL;")
    conn.execute("PRAGMA synchronous = NORMAL;")
    return conn


def get_conn() -> sqlite3.Connection:
    global _conn
    with _lock:
        if _conn is None:
            _conn = _connect()
            _init_schema(_conn)
        return _conn


_SCHEMA = """
CREATE TABLE IF NOT EXISTS cached_videos (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    resident            TEXT NOT NULL,
    source              TEXT NOT NULL,
    source_id           TEXT NOT NULL,
    title               TEXT NOT NULL,
    filepath            TEXT NOT NULL,
    filesize_bytes      INTEGER,
    duration_seconds    INTEGER,
    downloaded_at       TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    last_played_at      TIMESTAMP,
    play_count          INTEGER NOT NULL DEFAULT 0,
    protected           INTEGER NOT NULL DEFAULT 0,
    central_curated_id  TEXT,
    UNIQUE (resident, source, source_id)
);

CREATE TABLE IF NOT EXISTS playlist_terms (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    resident        TEXT NOT NULL,
    term            TEXT NOT NULL,
    item_blob       TEXT,
    first_seen_at   TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    last_seen_at    TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    active          INTEGER NOT NULL DEFAULT 1,
    UNIQUE (resident, term)
);

CREATE TABLE IF NOT EXISTS term_videos (
    term_id         INTEGER NOT NULL,
    video_id        INTEGER NOT NULL,
    added_at        TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (term_id, video_id),
    FOREIGN KEY (term_id)  REFERENCES playlist_terms(id) ON DELETE CASCADE,
    FOREIGN KEY (video_id) REFERENCES cached_videos(id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_cached_videos_resident   ON cached_videos(resident);
CREATE INDEX IF NOT EXISTS idx_cached_videos_lastplayed ON cached_videos(last_played_at);
CREATE INDEX IF NOT EXISTS idx_term_videos_video        ON term_videos(video_id);
CREATE INDEX IF NOT EXISTS idx_playlist_terms_resident  ON playlist_terms(resident, active);
"""


def _init_schema(conn: sqlite3.Connection):
    with _lock:
        conn.executescript(_SCHEMA)
    _log(f"[CACHE] DB ready at {VIDEO_CACHE_DB_PATH}")


def init_db():
    """Public entry point: call once at startup to ensure the DB exists."""
    get_conn()


# -------------------------------------------------------------------------
# Term canonicalisation
# -------------------------------------------------------------------------
def canonical_term(item) -> str | None:
    """
    Reduce a normalized playlist item to a stable string we can key on.

    Mirrors how normalize_playlist_items() shapes entries:
      - youtube items carry a 'query'
      - video items carry a 'url'
      - radio/photo items are not cacheable (returns None)
    """
    if item is None:
        return None
    if isinstance(item, str):
        s = item.strip()
        return s or None
    if isinstance(item, dict):
        t = (item.get("type") or item.get("kind") or "").lower()
        if t in {"radio", "photo", "image", "picture"}:
            return None
        q = item.get("query")
        if isinstance(q, str) and q.strip():
            return q.strip()
        u = item.get("url") or item.get("mediaUrl")
        if isinstance(u, str) and u.strip():
            return u.strip()
    return None


def is_cacheable(item) -> bool:
    if not isinstance(item, dict):
        return canonical_term(item) is not None
    t = (item.get("type") or item.get("kind") or "").lower()
    return t in {"youtube", "video"}


# -------------------------------------------------------------------------
# Term CRUD
# -------------------------------------------------------------------------
def register_term(resident: str, term: str, item_blob: dict | None = None) -> int:
    """Insert-or-update a (resident, term) pair; returns its row id and marks it active."""
    blob_json = json.dumps(item_blob, ensure_ascii=False) if item_blob is not None else None
    with _lock:
        conn = get_conn()
        cur = conn.execute(
            """
            INSERT INTO playlist_terms (resident, term, item_blob, active)
            VALUES (?, ?, ?, 1)
            ON CONFLICT(resident, term) DO UPDATE SET
                last_seen_at = CURRENT_TIMESTAMP,
                active = 1,
                item_blob = COALESCE(excluded.item_blob, playlist_terms.item_blob)
            """,
            (resident, term, blob_json),
        )
        # lastrowid is only set on insert; fetch the id either way.
        row = conn.execute(
            "SELECT id FROM playlist_terms WHERE resident = ? AND term = ?",
            (resident, term),
        ).fetchone()
        return int(row["id"]) if row else int(cur.lastrowid)


def mark_terms_inactive(resident: str, active_terms: list[str]):
    """Set active=0 for any term belonging to this resident that's not in active_terms."""
    with _lock:
        conn = get_conn()
        if not active_terms:
            conn.execute(
                "UPDATE playlist_terms SET active = 0 WHERE resident = ?",
                (resident,),
            )
            return
        placeholders = ",".join("?" * len(active_terms))
        params = [resident, *active_terms]
        conn.execute(
            f"""
            UPDATE playlist_terms SET active = 0
            WHERE resident = ? AND term NOT IN ({placeholders})
            """,
            params,
        )


def delete_inactive_terms(resident: str | None = None) -> int:
    """
    Hard-delete inactive playlist_terms rows; cascades to term_videos via FK.
    Returns the number of term rows deleted.
    """
    with _lock:
        conn = get_conn()
        if resident is None:
            cur = conn.execute("DELETE FROM playlist_terms WHERE active = 0")
        else:
            cur = conn.execute(
                "DELETE FROM playlist_terms WHERE active = 0 AND resident = ?",
                (resident,),
            )
        return cur.rowcount or 0


def active_terms_for_resident(resident: str) -> list[sqlite3.Row]:
    with _lock:
        return list(
            get_conn().execute(
                "SELECT * FROM playlist_terms WHERE resident = ? AND active = 1",
                (resident,),
            )
        )


def all_active_terms() -> list[sqlite3.Row]:
    with _lock:
        return list(
            get_conn().execute("SELECT * FROM playlist_terms WHERE active = 1")
        )


def term_id_for(resident: str, term: str) -> int | None:
    with _lock:
        row = get_conn().execute(
            "SELECT id FROM playlist_terms WHERE resident = ? AND term = ?",
            (resident, term),
        ).fetchone()
        return int(row["id"]) if row else None


# -------------------------------------------------------------------------
# Video CRUD
# -------------------------------------------------------------------------
def register_video(
    resident: str,
    source: str,
    source_id: str,
    title: str,
    filepath: str,
    *,
    filesize_bytes: int | None = None,
    duration_seconds: int | None = None,
    protected: bool = False,
    central_curated_id: str | None = None,
) -> int:
    """Insert a cached video row, or return the existing id if the (resident, source, source_id) already exists."""
    with _lock:
        conn = get_conn()
        existing = conn.execute(
            "SELECT id FROM cached_videos WHERE resident = ? AND source = ? AND source_id = ?",
            (resident, source, source_id),
        ).fetchone()
        if existing:
            return int(existing["id"])
        cur = conn.execute(
            """
            INSERT INTO cached_videos
                (resident, source, source_id, title, filepath,
                 filesize_bytes, duration_seconds, protected, central_curated_id)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
            """,
            (
                resident, source, source_id, title, filepath,
                filesize_bytes, duration_seconds,
                1 if protected else 0,
                central_curated_id,
            ),
        )
        return int(cur.lastrowid)


def link_video_to_term(term_id: int, video_id: int):
    with _lock:
        get_conn().execute(
            "INSERT OR IGNORE INTO term_videos (term_id, video_id) VALUES (?, ?)",
            (term_id, video_id),
        )


def mark_video_played(video_id: int):
    with _lock:
        get_conn().execute(
            """
            UPDATE cached_videos
            SET last_played_at = CURRENT_TIMESTAMP,
                play_count = play_count + 1
            WHERE id = ?
            """,
            (video_id,),
        )


def delete_video(video_id: int) -> str | None:
    """Delete a cached_video row (cascades to term_videos); returns its filepath if the row existed."""
    with _lock:
        conn = get_conn()
        row = conn.execute("SELECT filepath FROM cached_videos WHERE id = ?", (video_id,)).fetchone()
        if not row:
            return None
        conn.execute("DELETE FROM cached_videos WHERE id = ?", (video_id,))
        return row["filepath"]


# -------------------------------------------------------------------------
# Queries used by downloader / GC / playback
# -------------------------------------------------------------------------
def videos_for_term(term_id: int) -> list[sqlite3.Row]:
    """
    Cached videos linked to a term, ordered cache-friendliest first:
    least-recently played (nulls first → never-played first), tiebreak oldest download.
    """
    with _lock:
        return list(get_conn().execute(
            """
            SELECT v.*
            FROM cached_videos v
            JOIN term_videos tv ON tv.video_id = v.id
            WHERE tv.term_id = ?
            ORDER BY (v.last_played_at IS NULL) DESC,
                     v.last_played_at ASC,
                     v.downloaded_at ASC
            """,
            (term_id,),
        ))


def cached_source_ids_for_term(term_id: int) -> set[str]:
    """Source IDs already cached for this term — feed into yt-dlp avoid set."""
    with _lock:
        rows = get_conn().execute(
            """
            SELECT v.source_id
            FROM cached_videos v
            JOIN term_videos tv ON tv.video_id = v.id
            WHERE tv.term_id = ?
            """,
            (term_id,),
        ).fetchall()
    return {r["source_id"] for r in rows}


def terms_below_cap(cap: int, resident: str | None = None) -> list[sqlite3.Row]:
    """
    Active terms with fewer than `cap` linked videos, ordered by gap descending
    (terms with zero videos first). Used by the download worker to pick the
    next term to fetch.
    """
    sql = """
        SELECT t.id AS term_id,
               t.resident AS resident,
               t.term AS term,
               t.item_blob AS item_blob,
               COALESCE(c.cnt, 0) AS cached_count
        FROM playlist_terms t
        LEFT JOIN (
            SELECT term_id, COUNT(*) AS cnt
            FROM term_videos
            GROUP BY term_id
        ) c ON c.term_id = t.id
        WHERE t.active = 1
          AND COALESCE(c.cnt, 0) < ?
    """
    params: list = [cap]
    if resident is not None:
        sql += " AND t.resident = ?"
        params.append(resident)
    sql += " ORDER BY cached_count ASC, t.last_seen_at DESC"
    with _lock:
        return list(get_conn().execute(sql, params))


def orphan_videos(resident: str | None = None) -> list[sqlite3.Row]:
    """
    Cached videos with no active term_videos association and protected = 0.
    Inactive-term links don't count because their parent term row is deleted
    via the reconcile step before GC runs (cascade clears the join).
    """
    sql = """
        SELECT v.*
        FROM cached_videos v
        LEFT JOIN term_videos tv ON tv.video_id = v.id
        WHERE v.protected = 0
          AND tv.video_id IS NULL
    """
    params: list = []
    if resident is not None:
        sql += " AND v.resident = ?"
        params.append(resident)
    with _lock:
        return list(get_conn().execute(sql, params))


def videos_over_cap_for_term(term_id: int, cap: int, min_age_days: int) -> list[sqlite3.Row]:
    """
    Returns the surplus videos for a term that are eligible for eviction:
    older than the minimum age, ordered worst-first (LRU + oldest download).
    Caller deletes them until the cap is met or the list is exhausted.
    """
    cutoff = (datetime.utcnow() - timedelta(days=min_age_days)).strftime("%Y-%m-%d %H:%M:%S")
    with _lock:
        rows = list(get_conn().execute(
            """
            SELECT v.*
            FROM cached_videos v
            JOIN term_videos tv ON tv.video_id = v.id
            WHERE tv.term_id = ?
              AND v.protected = 0
              AND v.downloaded_at < ?
            ORDER BY (v.last_played_at IS NULL) DESC,
                     v.last_played_at ASC,
                     v.downloaded_at ASC
            """,
            (term_id, cutoff),
        ))
    # Trim to whatever exceeds the cap, but only after the age filter has
    # already been applied — videos younger than min_age_days are protected
    # regardless of how full the cache is.
    total_for_term = video_count_for_term(term_id)
    surplus = max(0, total_for_term - cap)
    return rows[:surplus]


def video_count_for_term(term_id: int) -> int:
    with _lock:
        row = get_conn().execute(
            "SELECT COUNT(*) AS n FROM term_videos WHERE term_id = ?",
            (term_id,),
        ).fetchone()
    return int(row["n"]) if row else 0


def cached_videos_for_resident(resident: str) -> list[sqlite3.Row]:
    """All cached videos for this resident, ordered for menu display.
    Sort by title (case-insensitive) so the remote-control list is stable
    between renders and easy for staff to scan."""
    with _lock:
        return list(get_conn().execute(
            """
            SELECT * FROM cached_videos
            WHERE resident = ?
            ORDER BY LOWER(title) ASC, downloaded_at ASC
            """,
            (resident,),
        ))


def random_cached_video_for_resident(
    resident: str,
    exclude_source_ids: set[str] | None = None,
) -> sqlite3.Row | None:
    """
    Pick a random cached_videos row for this resident. Used by the offline
    playback fallback — when WiFi is down and the engine's chosen term has
    no term-specific cache, we still serve *something* the resident knows
    rather than skipping the slot.

    exclude_source_ids lets the caller pass in recent video IDs (typically
    from recent_for(resident)) so we don't replay the same video back-to-back.
    Falls back to ignoring the exclusion if every cached video is excluded.
    """
    exclude = set(exclude_source_ids or ())
    with _lock:
        rows = list(get_conn().execute(
            "SELECT * FROM cached_videos WHERE resident = ?", (resident,)
        ))
    if not rows:
        return None
    candidates = [r for r in rows if r["source_id"] not in exclude] or rows
    import random
    return random.choice(candidates)


def cached_filepaths() -> set[str]:
    """All known cached_videos.filepath values — used by GC to spot dangling files on disk."""
    with _lock:
        rows = get_conn().execute("SELECT filepath FROM cached_videos").fetchall()
    return {r["filepath"] for r in rows}


# -------------------------------------------------------------------------
# Reconciliation: take a fresh set of (term, blob) and align the DB
# -------------------------------------------------------------------------
def reconcile_resident_terms(resident: str, terms_with_blobs: list[tuple[str, dict | None]]):
    """
    Make playlist_terms for `resident` match the supplied list:
      - upsert each term (first_seen_at preserved, last_seen_at bumped, active=1)
      - mark every other term for this resident as active=0
      - hard-delete the now-inactive rows (cascade clears term_videos)
    Returns the number of rows whose link table was cleared.
    """
    active_terms = [t for t, _ in terms_with_blobs]
    with _lock:
        for term, blob in terms_with_blobs:
            register_term(resident, term, blob)
        mark_terms_inactive(resident, active_terms)
        return delete_inactive_terms(resident)


# -------------------------------------------------------------------------
# Cooldown tracking for repeatedly-failing terms (in-memory; not persisted)
# -------------------------------------------------------------------------
_term_cooldown: dict[int, float] = {}
_term_failure_counts: dict[int, int] = {}
_cooldown_lock = threading.Lock()


def record_term_failure(term_id: int, cooldown_seconds: int = 3600, threshold: int = 3) -> bool:
    """
    Increment a per-term failure counter. After `threshold` consecutive failures
    the term is parked for `cooldown_seconds`. Returns True if the term is now
    in cooldown.
    """
    with _cooldown_lock:
        n = _term_failure_counts.get(term_id, 0) + 1
        _term_failure_counts[term_id] = n
        if n >= threshold:
            _term_cooldown[term_id] = time.time() + cooldown_seconds
            _term_failure_counts[term_id] = 0
            return True
        return False


def record_term_success(term_id: int):
    with _cooldown_lock:
        _term_failure_counts.pop(term_id, None)
        _term_cooldown.pop(term_id, None)


def term_in_cooldown(term_id: int) -> bool:
    with _cooldown_lock:
        until = _term_cooldown.get(term_id)
        if not until:
            return False
        if time.time() >= until:
            _term_cooldown.pop(term_id, None)
            return False
        return True
