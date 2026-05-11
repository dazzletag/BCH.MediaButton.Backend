# Local video cache — manual test plan

These tests cover the feature/local-video-cache branch end-to-end on a single
non-production Pi. Run them in order; later tests assume earlier state.

The Pi modules involved:
- `cache_db.py` — schema + CRUD
- `video_downloader.py` — background `yt-dlp` worker
- `cache_gc.py` — orphan / cap / disk-consistency sweeps
- `media_button_pi.py` — engine wiring, cache-first playback, connectivity guard

Useful inspection commands (run from the Pi, in the same dir as `media_button_pi.py`):

```bash
sqlite3 .data/video_cache.db ".tables"
sqlite3 .data/video_cache.db "SELECT id, resident, term, active FROM playlist_terms;"
sqlite3 .data/video_cache.db "SELECT id, resident, source, source_id, title, filepath, protected FROM cached_videos;"
sqlite3 .data/video_cache.db "SELECT * FROM term_videos;"
ls -lh .data/video_cache/
```

Tail the service log to see `[CACHE]`, `[DOWNLOADER]`, `[GC]`, `[CONNECTIVITY]` lines.

---

## 1. Fresh install

**Setup**: stop the service, `rm -rf .data/video_cache .data/video_cache.db*`, restart.

**Expected**:
- On first run, `[CACHE] DB ready at .../video_cache.db` appears in the log.
- On first session, `[CACHE] Reconciled N term(s) for <resident>` appears (N matches the cacheable items in the playlist — radio / photo items are excluded).
- Playback streams normally (no cache exists yet) — `play_youtube` runs as before.
- Within seconds the downloader logs `[DOWNLOADER] yt-dlp ... → yt_<id>.mp4`.
- `.data/video_cache/` starts filling with `yt_*.mp4` files; `cached_videos` rows appear in the DB.
- Second session for the same resident: `[CACHE] Hit for '<term>' (resident=<r>): yt_<id>.mp4` shows on at least some tracks.

---

## 2. WiFi loss mid-playback

**Setup**: trigger a session, wait for a YouTube track to start streaming. While VLC is playing, disable WiFi on the Pi (`sudo ifconfig wlan0 down` or unplug the AP).

**Expected**:
- The currently-streaming track may stall when its buffer drains (depends on the format), or finish if buffered.
- The next track is either: (a) served from cache and plays cleanly, or (b) gets `[CONNECTIVITY] No network and no usable cache for ... — skipping` and the engine moves on.
- No crash; no exceptions in the log.

**Cleanup**: re-enable WiFi.

---

## 3. WiFi loss before playback

**Setup**: with WiFi already disabled and a populated cache (run test 1 first), trigger a session.

**Expected**:
- `wifi_healthy()` returns False (cached for 30 s).
- Each track is either a cache hit (`[CACHE] Hit ...`) or gets `[CONNECTIVITY] ... skipping`.
- The session continues playing whatever it can serve from cache.
- No `play_youtube` calls are made.

---

## 4. Playlist term removal

**Setup**: note the current set of terms (`sqlite3 ... "SELECT term FROM playlist_terms WHERE resident='<r>' AND active=1;"`) and which terms have cached videos. Manually force a playlist rebuild for that resident — easiest is to bump the survey-row hash by editing `This_is_Me.xlsx` for that resident and triggering a session, or temporarily remove the cached playlist file at `.data/playlists/<resident>.json` and trigger a session so the LLM rebuilds.

**Expected**:
- `[CACHE] Reconciled N term(s) for <r>; M inactive term row(s) cleared` (M > 0 if terms dropped).
- `[GC] Removed K orphan video(s)` immediately after.
- File count under `.data/video_cache/` for that resident drops.
- The next 30 min slow GC tick also runs cleanly (`[GC] ...`) with no further orphans to remove.

---

## 5. Per-term cap exceeded

**Setup**: temporarily set `VIDEO_CACHE_MIN_AGE_DAYS=0` and `VIDEO_CACHE_PER_TERM_CAP=3` in the systemd unit and restart. Pick a single term; insert eight `cached_videos` rows linked to it via the SQLite CLI (or wait for the downloader to fetch eight over time):

```bash
sqlite3 .data/video_cache.db <<SQL
INSERT INTO cached_videos (resident, source, source_id, title, filepath)
VALUES ('Alice','yt','TEST1','t1','/tmp/yt_TEST1.mp4'),
       ('Alice','yt','TEST2','t2','/tmp/yt_TEST2.mp4'),
       ('Alice','yt','TEST3','t3','/tmp/yt_TEST3.mp4'),
       ('Alice','yt','TEST4','t4','/tmp/yt_TEST4.mp4'),
       ('Alice','yt','TEST5','t5','/tmp/yt_TEST5.mp4'),
       ('Alice','yt','TEST6','t6','/tmp/yt_TEST6.mp4'),
       ('Alice','yt','TEST7','t7','/tmp/yt_TEST7.mp4'),
       ('Alice','yt','TEST8','t8','/tmp/yt_TEST8.mp4');
SQL
# then link them to a known term_id from playlist_terms.
```

Then trigger a session (which runs orphan sweep + cap eviction via `_register_session_terms`) or wait for the next 30-min GC tick.

**Expected**:
- `[GC] Evicted 5 over-cap video(s)` (8 - cap of 3 = 5).
- `cached_videos` count for that term is exactly 3.
- The three survivors are the *most recently played* ones (or newest downloads on tie).

**Cleanup**: restore `VIDEO_CACHE_MIN_AGE_DAYS` and `VIDEO_CACHE_PER_TERM_CAP` to defaults.

---

## 6. Family video preservation

**Setup**: insert a protected row directly with no term link:

```bash
sqlite3 .data/video_cache.db <<SQL
INSERT INTO cached_videos (resident, source, source_id, title, filepath, protected)
VALUES ('Alice','fam','fam-1','Auntie Ivy birthday','/home/pi/media-button/.data/video_cache/fam_1.mp4', 1);
SQL
touch .data/video_cache/fam_1.mp4
```

Force a GC sweep by triggering a new session for Alice (which runs `_register_session_terms` → orphan sweep), or wait 30 min.

**Expected**:
- `cached_videos` row with `protected=1` is **not** deleted, even though it has no `term_videos` link.
- The file on disk is **not** removed.

---

## 7. Throttle verification

**Setup**: enable verbose downloader output by running `yt-dlp` manually with the same flags the worker uses while a session is playing. To verify the live behaviour, tail the service log and check the `[DOWNLOADER] yt-dlp (throttled ...)` line during playback vs `[DOWNLOADER] yt-dlp (unthrottled ...)` between sessions.

**Expected**:
- During active playback: log line reads `yt-dlp (throttled) ...`.
- Between sessions / when nothing is playing: `yt-dlp (unthrottled) ...`.
- Override-test: set `VIDEO_CACHE_THROTTLE_RATE=200K` and verify the log line still says `throttled` (the rate is internal, the log just confirms the branch).

---

## 8. Concurrent download + playback (Pi 4 / 4 GB)

**Setup**: ensure the cache is partially populated. Trigger a session and let it play YouTube videos back-to-back for ~10 minutes while the downloader continues to fetch (check `top` for `yt-dlp` and `vlc` running concurrently).

**Expected**:
- No VLC crashes or stutters attributable to the downloader.
- CPU stays under ~80 % per core on the Pi 4.
- Memory headroom remains; no OOM kills in `dmesg`.
- `[DOWNLOADER] Cached '<title>' for term '<t>' ...` lines continue to appear during playback.

---

## Post-test smoke

After all tests, verify the system is in a sane state:

```bash
sqlite3 .data/video_cache.db <<'SQL'
SELECT 'terms', COUNT(*) FROM playlist_terms WHERE active = 1
UNION ALL SELECT 'videos', COUNT(*) FROM cached_videos
UNION ALL SELECT 'links', COUNT(*) FROM term_videos
UNION ALL SELECT 'orphans', COUNT(*) FROM cached_videos v
         LEFT JOIN term_videos tv ON tv.video_id = v.id
         WHERE tv.video_id IS NULL AND v.protected = 0;
SQL
```

Orphan count should be 0 immediately after any session ends. Disk consumption under `.data/video_cache/` should be roughly `(number of terms) × VIDEO_CACHE_PER_TERM_CAP × ~150 MB`.
