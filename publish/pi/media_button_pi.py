#!/usr/bin/env python3
import asyncio, os, re, shlex, subprocess, sys, time, json, random, threading, hashlib, urllib.parse
from collections import deque
from datetime import datetime

import pandas as pd
import yaml
import requests
from dotenv import load_dotenv
from bleak import BleakScanner
import vlc

# UI (from ui_display.py)
from ui_display import MediaUI, ResidentIdentity
load_dotenv()

def _log(msg: str):
    """Lightweight timestamped logger."""
    try:
        ts = datetime.now().isoformat(timespec="seconds")
        print(f"[{ts}] {msg}")
    except Exception:
        print(msg)

def _require_env(name: str) -> str:
    val = os.environ.get(name, "")
    if not val:
        print(f"[CONFIG] Missing environment variable: {name}")
    return val

API_BASE = _require_env("API_BASE").rstrip("/")
DEVICE_ID = _require_env("DEVICE_ID")
DEVICE_KEY = _require_env("DEVICE_KEY")
POLL_SECONDS = int(os.environ.get("POLL_SECONDS", "60"))

# =========================
# Environment & constants
# =========================
BASE_DIR = os.path.dirname(__file__)
DATA_DIR = os.path.join(BASE_DIR, ".data")
PLAYLIST_DIR = os.path.join(DATA_DIR, "playlists")
os.makedirs(DATA_DIR, exist_ok=True)
os.makedirs(PLAYLIST_DIR, exist_ok=True)
MANUAL_PLAYLIST_DIR = os.path.join(DATA_DIR, "manual_playlists")
os.makedirs(MANUAL_PLAYLIST_DIR, exist_ok=True)
PHOTO_CACHE_DIR = os.path.join(DATA_DIR, "photo_cache")
os.makedirs(PHOTO_CACHE_DIR, exist_ok=True)

RECENT_PATH = os.path.join(DATA_DIR, "recent.json")
RECENT_DEPTH = 8

AUDIO_DEVICE = os.getenv("AUDIO_DEVICE", "pulse")   # "pulse" ALSA device routes through PipeWire (handles all format conversion)
AUDIO_OUT    = os.getenv("AUDIO_OUT", "alsa")        # alsa + device=pulse = ALSA pulse plugin → PipeWire

PLAYLIST_FLOW_URL = os.getenv("PLAYLIST_FLOW_URL", "")
TENANT_ID = os.environ.get("TENANT_ID", "")
CLIENT_ID = os.getenv("CLIENT_ID", "")
CLIENT_SECRET = os.getenv("CLIENT_SECRET", "")
DRIVE_ID = os.getenv("DRIVE_ID", "")
ITEM_ID = os.getenv("ITEM_ID", "")
ITEM_PATH = os.getenv("ITEM_PATH", "")
LOCAL_SURVEY_XLSX = os.getenv("LOCAL_SURVEY_XLSX", "/home/dazzletag/media-button/survey.xlsx")
THIS_IS_ME_XLSX   = os.getenv("THIS_IS_ME_XLSX", "/home/dazzletag/media-button/This_is_Me.xlsx")
GRAPH_POLL_SECONDS = int(os.getenv("GRAPH_POLL_SECONDS", "600"))
DEFAULT_RADIO_URL = os.getenv("DEFAULT_RADIO_URL", "")
VOLUME_PERCENT = int(os.getenv("VOLUME_PERCENT", "80"))
LLM_MODEL = os.getenv("LLM_MODEL", "gpt-41-mini-2025-04-14")
YT_DLP_BIN = os.getenv("YT_DLP_BIN", os.path.join(os.path.dirname(sys.executable), "yt-dlp"))
YT_EXTRACTOR_ARGS = os.getenv("YT_EXTRACTOR_ARGS", "youtube:player_client=web")
YT_FORCE_IPV4 = os.getenv("YT_FORCE_IPV4", "0") == "1"
YT_FORMAT = os.getenv("YT_FORMAT", "bv*[ext=mp4][height<=720][fps<=30][vcodec^=avc1]+ba/b")
YT_COOKIES_FILE    = os.getenv("YT_COOKIES_FILE", "")     # e.g. /etc/media-button/youtube-cookies.txt
YT_COOKIES_BROWSER = os.getenv("YT_COOKIES_BROWSER", "")  # e.g. chromium  (reads cookies directly from browser profile)
# External config survives 'git reset --hard'; fall back to bundled template
_EXTERNAL_CONFIG = os.getenv("MEDIA_BUTTON_CONFIG", "/etc/media-button/config.yaml")
_TEMPLATE_CONFIG = os.path.join(BASE_DIR, "config.yaml")
CONFIG_PATH = _EXTERNAL_CONFIG if os.path.exists(_EXTERNAL_CONFIG) else _TEMPLATE_CONFIG

EDDYSTONE_UUID = "feaa"

# Presence & timing knobs (overridable in config.yaml)
LAST_SEEN = {}  # resident -> ts
LAST_OFF   = {}  # resident -> ts when session last stopped
RADIO_FIRST_THRESHOLD = 30  # seconds off before starting with radio + photos
GONE_TIMEOUT = 6               # seconds unseen before stopping
DETECTION_COOLDOWN = 20         # seconds between trigger actions per beacon
AVOID_RECENT_N = 6              # when drawing randomly, avoid last N items
BIG_PLAYLIST_SIZE = 80          # target size from LLM
# ----- Pulse sink helpers (auto Bluetooth -> HDMI -> default) -----
def _pulse_sinks():
    try:
        out = subprocess.check_output(["pactl", "list", "short", "sinks"], text=True)
        # rows: <index>\t<name>\t<driver>\t<state>\t...
        return [ln.split("\t")[1] for ln in out.strip().splitlines() if "\t" in ln]
    except Exception:
        return []

def _first_match(names, patterns):
    for n in names:
        for p in patterns:
            if re.search(p, n, re.IGNORECASE):
                return n
    return None

def pick_sink():
    pref = os.getenv("PREFERRED_SINK", "").strip()
    fall = os.getenv("FALLBACK_SINK", "").strip()
    if pref:
        return pref  # manual demo override

    sinks = _pulse_sinks()
    bt   = _first_match(sinks, [r"bluez_output", r"bluetooth"])
    if bt:
        return bt
    hdmi = _first_match(sinks, [r"hdmi", r"bcm2835.*hdmi", r"alsa_output.*hdmi"])
    if hdmi:
        return hdmi
    return fall or ""   # empty -> use Pulse default

# =========================
# Recent memory helpers
# =========================
def _load_recent():
    try:
        with open(RECENT_PATH, "r", encoding="utf-8") as f:
            raw = json.load(f)
        return {k: deque(v, maxlen=RECENT_DEPTH) for k, v in raw.items()}
    except Exception:
        return {}

def _save_recent(recent):
    try:
        serial = {k: list(v) for k, v in recent.items()}
        with open(RECENT_PATH, "w", encoding="utf-8") as f:
            json.dump(serial, f, indent=2)
    except Exception as e:
        print(f"[MEMORY] Failed to save recent: {e}")

RECENT = _load_recent()

def recent_for(resident):
    return RECENT.setdefault(resident, deque(maxlen=RECENT_DEPTH))

def remember(resident, video_id):
    dq = recent_for(resident)
    if video_id in dq:
        dq.remove(video_id)
    dq.append(video_id)
    _save_recent(RECENT)

# =========================
# Utils
# =========================
def _detect_alsa_device():
    """
    Try to pick a sensible ALSA device when AUDIO_OUT=alsa and no AUDIO_DEVICE
    is provided. Prefers HDMI devices using stable CARD= names (not numeric
    indices which change when USB devices are added/removed).
    """
    try:
        out = subprocess.check_output(["aplay", "-L"], text=True)
        lines = [ln.strip() for ln in out.splitlines() if ln.strip()]

        def _is_hdmi(s):
            return "hdmi" in s.lower() or "vc4hdmi" in s.lower()

        def _is_named(s):  # CARD= format is stable across reboots
            return "card=" in s.lower()

        # 0. Always prefer 'pulse' — routes through PipeWire, handles any format
        if "pulse" in lines:
            return "pulse"

        # 1. Named HDMI (e.g. plughw:CARD=vc4hdmi0,DEV=0)  ← most stable
        named_hdmi = [ln for ln in lines if ln.lower().startswith("plughw:") and _is_named(ln) and _is_hdmi(ln)]
        if named_hdmi:
            return named_hdmi[0]

        # 2. Any named device
        named_any = [ln for ln in lines if ln.lower().startswith("plughw:") and _is_named(ln)]
        if named_any:
            return named_any[0]

        # 3. Numeric HDMI (fallback — index can drift)
        num_hdmi = [ln for ln in lines if ln.lower().startswith("plughw:") and not _is_named(ln) and _is_hdmi(ln)]
        if num_hdmi:
            return num_hdmi[0]

        # 4. Any plughw
        plughw_any = [ln for ln in lines if ln.lower().startswith("plughw:") and not _is_named(ln)]
        if plughw_any:
            return plughw_any[0]

    except Exception as e:
        _log(f"[AUDIO] ALSA probe failed: {e}")
    return None

def pick_random_youtube_id(query, yt_dlp_bin, avoid_ids, max_candidates=20):
    # Search is public — no auth needed (oauth2/cookies only apply to stream resolution)
    search = f"ytsearch{max_candidates}:{query}"
    cmd = [yt_dlp_bin, "--get-id", "--flat-playlist", search]
    ids = subprocess.check_output(cmd, text=True).splitlines()
    ids = [i.strip() for i in ids if i.strip()]
    random.shuffle(ids)
    for vid in ids:
        if vid not in avoid_ids:
            return vid
    return random.choice(ids) if ids else None

def sha256_of(obj) -> str:
    blob = json.dumps(obj, sort_keys=True, ensure_ascii=False)
    return hashlib.sha256(blob.encode("utf-8")).hexdigest()

# =========================
# Media helpers
# =========================
def _is_image_url(url: str) -> bool:
    try:
        lower = url.lower()
    except Exception:
        return False
    return lower.startswith("http") and any(lower.split("?")[0].endswith(ext) for ext in (".jpg", ".jpeg", ".png", ".gif", ".webp"))


def _is_radio_url(url: str) -> bool:
    try:
        lower = url.lower()
    except Exception:
        return False
    if not lower.startswith("http"):
        return False
    radio_exts = (".m3u", ".m3u8", ".pls", ".aac", ".mp3", ".ogg", ".opus", ".flac")
    return any(lower.split("?")[0].endswith(ext) for ext in radio_exts) or "stream" in lower


def _is_youtube_url(url: str) -> bool:
    try:
        lower = url.lower()
    except Exception:
        return False
    return lower.startswith("http") and ("youtube.com" in lower or "youtu.be" in lower)


def _maybe_add_scheme(url: str) -> str:
    """
    Normalise URLs that are missing a scheme (e.g. 'youtube.com/...' or 'www...').
    Keeps original value if no change is needed.
    """
    try:
        s = url.strip()
    except Exception:
        return url
    low = s.lower()
    if low.startswith(("http://", "https://")):
        return s
    if low.startswith(("www.", "youtube.com/", "youtu.be/")):
        return f"https://{s}"
    return s


def _escape_spaces(url: str) -> str:
    """Azure SAS URLs can contain spaces in blob names; VLC needs them percent-encoded."""
    try:
        return url.replace(" ", "%20") if " " in url else url
    except Exception:
        return url


# =========================
# Graph client
# =========================
class GraphClient:
    def __init__(self, tenant_id, client_id, client_secret):
        self.token = None
        self.tenant_id = tenant_id
        self.client_id = client_id
        self.client_secret = client_secret

    def _token(self):
        if self.token and self.token.get("expires_on") and int(self.token["expires_on"]) - int(time.time()) > 120:
            return self.token["access_token"]
        token_url = f"https://login.microsoftonline.com/{self.tenant_id}/oauth2/v2.0/token"
        data = {
            "grant_type": "client_credentials",
            "client_id": self.client_id,
            "client_secret": self.client_secret,
            "scope": "https://graph.microsoft.com/.default",
        }
        r = requests.post(token_url, data=data, timeout=30)
        r.raise_for_status()
        payload = r.json()
        payload["expires_on"] = int(time.time()) + int(payload.get("expires_in", 3600))
        self.token = payload
        return payload["access_token"]

    def _headers(self):
        return {"Authorization": f"Bearer {self._token()}"}

    def download_excel(self, dest_path, drive_id=None, item_id=None, item_path=None):
        if item_id:
            url = f"https://graph.microsoft.com/v1.0/drives/{drive_id}/items/{item_id}/content"
        elif item_path:
            if drive_id:
                url = f"https://graph.microsoft.com/v1.0/drives/{drive_id}/root:{item_path}:/content"
            else:
                url = f"https://graph.microsoft.com/v1.0/me/drive/root:{item_path}:/content"
        else:
            raise ValueError("Provide either item_id or item_path.")
        with requests.get(url, headers=self._headers(), stream=True, timeout=120) as r:
            r.raise_for_status()
            with open(dest_path, "wb") as f:
                for chunk in r.iter_content(chunk_size=8192):
                    f.write(chunk)
        return dest_path

    def get_item_last_modified(self, drive_id=None, item_id=None, item_path=None):
        if item_id:
            meta_url = f"https://graph.microsoft.com/v1.0/drives/{drive_id}/items/{item_id}"
        elif item_path:
            if drive_id:
                meta_url = f"https://graph.microsoft.com/v1.0/drives/{drive_id}/root:{item_path}"
            else:
                meta_url = f"https://graph.microsoft.com/v1.0/me/drive/root:{item_path}"
        else:
            raise ValueError("Provide either item_id or item_path.")
        r = requests.get(meta_url, headers=self._headers(), timeout=30)
        r.raise_for_status()
        data = r.json()
        return data.get("lastModifiedDateTime")

# =========================
# Survey (whole-row capture)
# =========================
class Survey:
    def __init__(self, xlsx_path):
        self.xlsx_path = xlsx_path
        self._rows = {}      # resident_lower -> raw dict of row
        self._hashes = {}    # resident_lower -> sha256(row)

    def load(self):
        if not os.path.exists(self.xlsx_path):
            print(f"[SURVEY] File not found: {self.xlsx_path} — survey data unavailable")
            self._rows = {}
            self._hashes = {}
            return {}
        xls = pd.ExcelFile(self.xlsx_path)
        rows = {}
        for sheet in xls.sheet_names:
            df = pd.read_excel(self.xlsx_path, sheet_name=sheet)
            # Normalise headers
            df.columns = [re.sub(r"\s+", " ", str(c)).strip() for c in df.columns]
            # Try to find a name column
            name_col = next((c for c in df.columns if c.lower().startswith("resident") and "name" in c.lower()), None)
            if not name_col:
                continue
            for _, row in df.iterrows():
                name = str(row.get(name_col, "")).strip()
                if not name:
                    continue
                key = name.lower()
                raw = {str(k): (None if pd.isna(v) else (str(v) if not isinstance(v, (dict, list)) else v))
                       for k, v in row.to_dict().items()}
                rows[key] = raw
        self._rows = rows
        self._hashes = {k: sha256_of(v) for k, v in rows.items()}
        return rows

    def row_for(self, resident_name: str):
        if not self._rows:
            self.load()
        return self._rows.get(resident_name.lower())

    def hash_for(self, resident_name: str) -> str | None:
        if not self._hashes:
            self.load()
        return self._hashes.get(resident_name.lower())

class ThisIsMe:
    """
    Load the 'This is Me' profiles and expose them by fullName (case-insensitive).
    """
    def __init__(self, xlsx_path):
        self.xlsx_path = xlsx_path
        self._rows = {}

    def load(self):
        try:
            xls = pd.ExcelFile(self.xlsx_path)
        except Exception as e:
            print(f"[THIS_IS_ME] Failed to open {self.xlsx_path}: {e}")
            self._rows = {}
            return self._rows

        rows = {}
        for sheet in xls.sheet_names:
            df = pd.read_excel(self.xlsx_path, sheet_name=sheet)
            # Normalise headers
            df.columns = [re.sub(r"\s+", " ", str(c)).strip() for c in df.columns]

            # Try to find a full name column
            name_col = next(
                (
                    c for c in df.columns
                    if c.lower() in {"fullname", "full name"} or (
                        "full" in c.lower() and "name" in c.lower()
                    )
                ),
                None,
            )
            # Fallback: column literally called "fullName" in your sample
            if not name_col and "fullName" in df.columns:
                name_col = "fullName"

            if not name_col:
                continue

            for _, row in df.iterrows():
                name = str(row.get(name_col, "")).strip()
                if not name:
                    continue
                key = name.lower()
                raw = {
                    str(k): (
                        None
                        if pd.isna(v)
                        else (str(v) if not isinstance(v, (dict, list)) else v)
                    )
                    for k, v in row.to_dict().items()
                }
                rows[key] = raw

        self._rows = rows
        print(f"[THIS_IS_ME] Loaded {len(self._rows)} profiles from {self.xlsx_path}")
        return self._rows

    def row_for(self, resident_name: str) -> dict | None:
        if not self._rows:
            self.load()
        return self._rows.get(resident_name.lower())


# =========================
# LLM big playlist builder
# =========================
BANWORDS = {
    "meeting", "session", "group", "workshop", "class",
    "bingo", "gardening club", "exercise class",
    "shopping trip", "staff meeting", "training"
}

def llm_build_big_playlist(
    resident_name: str,
    survey_row: dict,
    profile: dict | None = None,
    want: int = BIG_PLAYLIST_SIZE,
) -> list[str]:

    """
    Send the resident data to the Power Automate flow and return a playlist.
    """
    dob = None
    gender = None
    if profile:
        dob = profile.get("dob") or profile.get("DOB") or profile.get("Dob")
        gender = profile.get("gender") or profile.get("Gender")

    payload = {
        "residentName": resident_name,
        "surveyRow": survey_row,
        "thisIsMeProfile": profile or {},
        "demographics": {
            "dob": dob,
            "gender": gender,
            "age_hint": "Use dob to infer approximate age and life era.",
        },
    }

    try:
        resp = requests.post(
            PLAYLIST_FLOW_URL,
            json=payload,
            timeout=120,
        )
        resp.raise_for_status()
        data = resp.json()
        items = data.get("playlist") or []
        cleaned = []
        for q in items:
            if not isinstance(q, str):
                continue
            s = " ".join(q.strip().split())
            if not s:
                continue
            low = s.lower()
            if any(b in low for b in BANWORDS):
                continue
            if not re.search(r"[a-zA-Z]", s) or " " not in s:
                continue
            cleaned.append(s[:60])
        seen = set(); uniq = []
        for s in cleaned:
            key = s.lower()
            if key not in seen:
                seen.add(key); uniq.append(s)
        if len(uniq) > want:
            uniq = uniq[:want]
        return uniq
    except Exception as e:
        print(f"[LLM] big playlist failed: {e}")
        return []

# =========================
# Disk playlist cache
# =========================
def playlist_path_for(resident: str) -> str:
    safe = re.sub(r"[^A-Za-z0-9_. -]", "_", resident).strip() or "resident"
    return os.path.join(PLAYLIST_DIR, f"{safe}.json")

def load_cached_playlist(resident: str) -> dict | None:
    path = playlist_path_for(resident)
    if not os.path.exists(path):
        return None
    try:
        with open(path, "r", encoding="utf-8") as f:
            return json.load(f)
    except Exception as e:
        print(f"[CACHE] Failed to read {path}: {e}")
        return None

def save_cached_playlist(resident: str, survey_hash: str, playlist: list[str], meta: dict | None = None):
    path = playlist_path_for(resident)
    payload = {
        "resident": resident,
        "survey_hash": survey_hash,
        "built_at": datetime.now().isoformat(timespec="seconds"),
        "model": LLM_MODEL,
        "playlist": playlist,
    }
    if meta:
        payload["meta"] = meta
    try:
        with open(path, "w", encoding="utf-8") as f:
            json.dump(payload, f, indent=2, ensure_ascii=False)
        print(f"[CACHE] Saved playlist: {path} ({len(playlist)} items)")
    except Exception as e:
        print(f"[CACHE] Failed to write {path}: {e}")
        
def manual_playlist_path_for(resident: str) -> str:
    safe = re.sub(r"[^A-Za-z0-9_. -]", "_", resident).strip() or "resident"
    # Simple .txt file, one line per query
    return os.path.join(MANUAL_PLAYLIST_DIR, f"{safe}.txt")

def manual_playlist_json_path_for(resident: str) -> str:
    safe = re.sub(r"[^A-Za-z0-9_. -]", "_", resident).strip() or "resident"
    return os.path.join(MANUAL_PLAYLIST_DIR, f"{safe}.json")

def manual_playlist_debug_path_for(resident: str) -> str:
    safe = re.sub(r"[^A-Za-z0-9_. -]", "_", resident).strip() or "resident"
    return os.path.join(MANUAL_PLAYLIST_DIR, f"{safe}.last.json")

def _persist_debug_playlist(resident: str, items):
    """
    Always writes a debug snapshot of the resolved playlist so we can inspect it.
    Does not affect the actual playlist files used elsewhere.
    """
    path = manual_playlist_debug_path_for(resident)
    try:
        with open(path, "w", encoding="utf-8") as f:
            json.dump(items, f, indent=2, ensure_ascii=False)
        print(f"[DEBUG] Wrote playlist snapshot to {path}")
    except Exception as e:
        print(f"[DEBUG] Failed to write playlist snapshot: {e}")


def load_manual_playlist(resident: str) -> list[str] | None:
    """
    Load manual playlist. Prefers JSON manifest (array of strings/objects). Falls back to legacy .txt.
    """
    json_path = manual_playlist_json_path_for(resident)
    if os.path.exists(json_path):
        try:
            with open(json_path, "r", encoding="utf-8") as f:
                data = json.load(f)
            if isinstance(data, list) and data:
                print(f"[MANUAL] Loaded JSON manual playlist for {resident}: {len(data)} items")
                return data
        except Exception as e:
            print(f"[MANUAL] Failed to read {json_path}: {e}")

    path = manual_playlist_path_for(resident)
    if not os.path.exists(path):
        return None

    try:
        items: list[str] = []
        with open(path, "r", encoding="utf-8") as f:
            for line in f:
                s = line.strip()
                if not s:
                    continue
                if s.startswith("#"):
                    # allow comments
                    continue
                # If line is JSON, parse it (object or array)
                if s.startswith("{") or s.startswith("["):
                    try:
                        parsed = json.loads(s)
                        if isinstance(parsed, list):
                            items.extend(parsed)
                        else:
                            items.append(parsed)
                    except Exception as e:
                        print(f"[MANUAL] JSON parse failed for line: {e}")
                else:
                    items.append(s)

        if items:
            print(f"[MANUAL] Loaded manual playlist for {resident}: {len(items)} items")
            return items
    except Exception as e:
        print(f"[MANUAL] Failed to read {path}: {e}")

    return None

def persist_manual_playlist(resident: str, items: list[str]):
    """
    Persist manual playlist. If any entry is a dict/list, write JSON manifest; otherwise keep legacy txt.
    """
    has_structured = any(isinstance(x, (dict, list)) for x in items)
    if has_structured:
        path = manual_playlist_json_path_for(resident)
        try:
            with open(path, "w", encoding="utf-8") as f:
                json.dump(items, f, indent=2, ensure_ascii=False)
            print(f"[MANUAL] Synced manual playlist (json) for {resident} ({len(items)} items)")
            _persist_debug_playlist(resident, items)
        except Exception as e:
            print(f"[MANUAL] Failed to persist manual playlist json for {resident}: {e}")
    else:
        path = manual_playlist_path_for(resident)
        try:
            with open(path, "w", encoding="utf-8") as f:
                for line in items:
                    if not line:
                        continue
                    f.write(str(line).strip() + "\n")
            print(f"[MANUAL] Synced manual playlist for {resident} ({len(items)} items)")
            _persist_debug_playlist(resident, items)
        except Exception as e:
            print(f"[MANUAL] Failed to persist manual playlist for {resident}: {e}")

# =========================
# Playlist normalization helpers
# =========================
def normalize_playlist_items(items):
    """
    Convert loose string/dict entries into a consistent dict format with
    {type, url/query, name}. This lets the player pick the right handler and
    keeps debug snapshots meaningful.
    """
    normalized: list[dict] = []
    for item in items or []:
        if item is None:
            continue

        if isinstance(item, str):
            s = item.strip()
            if not s:
                continue
            s = _maybe_add_scheme(s)
            low = s.lower()
            if low.startswith("radio:"):
                url = s.split(":", 1)[1].strip()
                normalized.append({"type": "radio", "url": url, "name": url})
                continue
            if _is_image_url(s):
                normalized.append({"type": "photo", "url": s, "name": s})
                continue
            if _is_radio_url(s):
                normalized.append({"type": "radio", "url": s, "name": s})
                continue
            if s.startswith("http"):
                normalized.append({"type": "video", "url": s, "name": s})
                continue
            normalized.append({"type": "youtube", "query": s, "name": s})
            continue

        if isinstance(item, dict):
            entry = dict(item)
            url = entry.get("url") or entry.get("mediaUrl") or entry.get("source") or entry.get("href")
            query = entry.get("query") or entry.get("search")
            radio_url = entry.get("radioUrl") or entry.get("radio") or entry.get("station")
            t = (entry.get("type") or entry.get("kind") or entry.get("mediaType") or entry.get("media_type") or "").lower()
            if isinstance(url, str):
                url = _maybe_add_scheme(url)
            if isinstance(query, str):
                query = _maybe_add_scheme(query)
            if isinstance(radio_url, str):
                radio_url = _maybe_add_scheme(radio_url)

            if isinstance(url, str) and url.lower().startswith("radio:"):
                url = url.split(":", 1)[1].strip()
                entry["url"] = url
                t = t or "radio"

            if not url and radio_url:
                url = radio_url
                t = t or "radio"

            if not t:
                if url and _is_image_url(url):
                    t = "photo"
                elif url and _is_radio_url(url):
                    t = "radio"
                elif url and _is_youtube_url(url):
                    t = "youtube"
                elif url:
                    t = "video"
                elif query:
                    t = "youtube"

            entry["type"] = t or "media"
            if not entry.get("name"):
                entry["name"] = entry.get("title") or entry.get("label") or url or query
            if url and not entry.get("url"):
                entry["url"] = url
            if query is not None and entry.get("query") is None:
                entry["query"] = query
            if entry.get("durationSeconds") is None and entry.get("duration") is not None:
                entry["durationSeconds"] = entry.get("duration")

            normalized.append(entry)
            continue

        try:
            print(f"[NORMALIZE] Skipping unsupported item: {item}")
        except Exception:
            pass

    return normalized

# =========================
# API sync helpers
# =========================
def _device_headers():
    if not DEVICE_KEY:
        return {}
    return {"X-DEVICE-KEY": DEVICE_KEY}

def push_ai_playlist_to_api(resident: str, playlist: list[str], survey_hash: str | None, model: str | None, meta: dict | None = None):
    if not API_BASE or not DEVICE_ID or not DEVICE_KEY or not playlist:
        return
    payload = {
        "resident": resident,
        "surveyHash": survey_hash,
        "model": model,
        "playlist": playlist,
        "builtAt": datetime.utcnow().isoformat(timespec="seconds") + "Z",
        "meta": meta or {},
    }
    try:
        url = f"{API_BASE}/api/device/{urllib.parse.quote(DEVICE_ID)}/resident/ai-playlist"
        r = requests.post(url, headers=_device_headers(), json=payload, timeout=10)
        r.raise_for_status()
        print(f"[API] Pushed AI playlist for {resident} ({len(playlist)} items)")
    except Exception as e:
        print(f"[API] Failed to push AI playlist for {resident}: {e}")

def fetch_manual_playlist_from_api(resident: str) -> list[str]:
    if not API_BASE or not DEVICE_ID or not DEVICE_KEY:
        return []
    try:
        url = f"{API_BASE}/api/device/{urllib.parse.quote(DEVICE_ID)}/resident/{urllib.parse.quote(resident)}/manual-playlist"
        r = requests.get(url, headers=_device_headers(), timeout=10)
        if r.status_code != 200:
            return []
        data = r.json()

        # API can respond with either a bare list or { items: [...] }
        if isinstance(data, list):
            items = data
        elif isinstance(data, dict):
            items = data.get("items") or data.get("playlist") or []
        else:
            items = []

        parsed: list = []
        for i in items:
            if isinstance(i, dict):
                parsed.append(i)
            else:
                s = str(i).strip()
                if s:
                    parsed.append(s)
        return parsed
    except Exception as e:
        print(f"[API] Failed to fetch manual playlist for {resident}: {e}")
        return []

# =========================
# Mobizio "This is Me" profile (replaces local This_is_Me.xlsx)
# =========================
_PROFILE_CACHE_DIR = os.path.join(BASE_DIR, ".data", "profiles")

def fetch_resident_profile_from_api(resident: str) -> dict:
    """
    Fetch the resident's 'This is Me' profile from the backend API (which reads from Mobizio).
    Returns a flat dict of form field label → value, same shape as ThisIsMe.row_for().
    Caches the result locally so playback continues when offline.
    Falls back to the local cache if the API is unreachable.
    """
    cache_file = os.path.join(_PROFILE_CACHE_DIR, f"{resident.replace(' ', '_')}.profile.json")
    os.makedirs(_PROFILE_CACHE_DIR, exist_ok=True)

    if API_BASE and DEVICE_ID and DEVICE_KEY:
        try:
            url = (f"{API_BASE}/api/device/{urllib.parse.quote(DEVICE_ID)}"
                   f"/resident-profile?residentKey={urllib.parse.quote(resident)}")
            r = requests.get(url, headers=_device_headers(), timeout=20)
            if r.status_code == 200:
                data = r.json()
                # API returns ResidentMobizioProfile: { name, dob, gender, formFields: {...} }
                form_fields = data.get("formFields") or {}
                # Enrich with top-level demographic fields
                if data.get("dob"):
                    form_fields.setdefault("Date of Birth", data["dob"])
                if data.get("gender"):
                    form_fields.setdefault("Gender", data["gender"])
                with open(cache_file, "w") as f:
                    json.dump(form_fields, f)
                print(f"[PROFILE] Fetched {len(form_fields)} field(s) for {resident} from Mobizio API")
                return form_fields
            else:
                print(f"[PROFILE] API returned {r.status_code} for {resident}")
        except Exception as e:
            print(f"[PROFILE] API fetch failed for {resident}: {e}")

    # Fall back to local cache
    if os.path.exists(cache_file):
        try:
            with open(cache_file) as f:
                data = json.load(f)
            print(f"[PROFILE] Using cached profile for {resident}")
            return data
        except Exception as e:
            print(f"[PROFILE] Cache read failed: {e}")

    return {}

# =========================
# Photo cache for ambient slideshow
# =========================

def _photo_cache_key(url: str) -> str:
    """Stable cache key derived from the blob path only (ignores SAS expiry tokens)."""
    try:
        p = urllib.parse.urlparse(url)
        return hashlib.sha256((p.netloc + p.path).encode()).hexdigest()
    except Exception:
        return hashlib.sha256(url.encode()).hexdigest()


def download_photo_to_cache(url: str) -> str | None:
    """Download a photo URL to the local photo cache. Returns local path or None."""
    key = _photo_cache_key(url)
    try:
        ext = os.path.splitext(urllib.parse.urlparse(url).path)[1].lower() or ".jpg"
    except Exception:
        ext = ".jpg"
    path = os.path.join(PHOTO_CACHE_DIR, key + ext)
    if os.path.exists(path):
        return path
    try:
        r = requests.get(url, timeout=20)
        r.raise_for_status()
        with open(path, "wb") as f:
            f.write(r.content)
        print(f"[PHOTO] Cached {os.path.basename(path)} ({len(r.content) // 1024} KB)")
        return path
    except Exception as e:
        print(f"[PHOTO] Cache download failed for {url}: {e}")
        return None


def prefetch_session_photos(playlist: list, max_photos: int = 20) -> list:
    """
    Extract all photo items from a playlist, download them to disk cache,
    and return a list of PIL Image objects ready for the ambient slideshow.
    """
    from PIL import Image as PIL_Image
    photos = []
    for item in playlist:
        if len(photos) >= max_photos:
            break
        url = None
        if isinstance(item, dict):
            t = (item.get("type") or "").lower()
            if t in {"photo", "image", "picture"}:
                url = item.get("url")
        elif isinstance(item, str) and _is_image_url(item):
            url = item
        if not url:
            continue
        path = download_photo_to_cache(url)
        if path:
            try:
                img = PIL_Image.open(path).convert("RGB")
                photos.append(img)
            except Exception as e:
                print(f"[PHOTO] Failed to open cached image {path}: {e}")
    return photos


# =========================
# Media player (mpv embedded)
# =========================
class MediaPlayer:
    """
    VLC-backed media player for Raspberry Pi - fully embeddable in Tk.
    No mpv, no subprocesses, no GPU-context issues.
    """

    _yt_cache_cleared = False

    def __init__(self, volume_percent=80):
        # Allow VLC to send audio to Pulse sink if you set PULSE_SINK
        # Force software decode to avoid v4l2m2m failures on some Pis
        aout = AUDIO_OUT or "pulse"
        opts = [f"--aout={aout}", "--avcodec-hw=none"]
        chosen_device = AUDIO_DEVICE
        if aout.startswith("alsa") and not chosen_device:
            chosen_device = _detect_alsa_device()
        if aout.startswith("alsa") and chosen_device:
            opts.append(f"--alsa-audio-device={chosen_device}")
            _log(f"[AUDIO] Using ALSA device: {chosen_device}")
        _log(f"[AUDIO] VLC opts: {opts}")
        self.instance = vlc.Instance(*opts)
        self.player = self.instance.media_player_new()
        self.radio_player = self.instance.media_player_new()
        self.set_volume(volume_percent)

        # Track state for Engine compatibility
        self.current_proc = None     # always None now
        self._playing = False
        self._stop_flag = False
        self._radio_url = None
        


    # ------------------------------
    # Embedding helper
    # ------------------------------
    
    
    def _bind_window(self, wid):
        """Bind VLC output to our Tk window."""
        try:
            self.player.set_xwindow(wid)   # X11 (Linux)
        except Exception as e:
            print("[VLC] set_xwindow failed:", e)

    # ------------------------------
    # Core playback
    # ------------------------------
    def _play_media(self, media_url: str, wid: int | None) -> bool:
        """Shared internal method for radio + YouTube."""
        try:
            media = self.instance.media_new(media_url)
            # Helpful defaults for HTTP(S) media
            if isinstance(media_url, str) and media_url.startswith("http"):
                media.add_option(":http-user-agent=Mozilla/5.0")
                media.add_option(":network-caching=1500")
                media.add_option(":live-caching=1500")
                if YT_FORCE_IPV4:
                    media.add_option(":ipv4")
            self.player.set_media(media)

            if wid:
                self._bind_window(wid)

            # If an external stop was requested between _stop_current() and here, honour it.
            if self._stop_flag:
                return False
            self._stop_flag = False
            self.player.play()
            self._playing = True
            return True

        except Exception as e:
            print("[VLC] Playback failed:", e)
            return False

    # ------------------------------
    # Public API
    # ------------------------------
    def play_radio(self, url: str, wid: int | None = None) -> bool:
        _log(f"[MEDIA] VLC radio start: {url}")
        self._stop_current()
        ok = self._play_media(url, wid)
        if not ok:
            _log(f"[MEDIA] VLC radio start FAILED: {url}")
        else:
            try:
                state = self.player.get_state()
                _log(f"[MEDIA] VLC radio state after start: {state}")
            except Exception as e:
                _log(f"[MEDIA] VLC radio state check error: {e}")
        return ok

    def play_youtube(self, query_or_url: str, resident: str | None = None, wid: int | None = None) -> bool:
        """
        Accepts either:
        - a full YouTube URL (will resolve via yt-dlp)
        - a user query string (resolved through ytsearch by your engine)
        """
        print(f"[MEDIA] VLC YouTube resolve: {query_or_url}")
        self._stop_current()

        try:
            # Resolve using yt-dlp; prefer explicit web client to reduce 403s
            if not self._yt_cache_cleared:
                subprocess.run([YT_DLP_BIN, "--rm-cache-dir"], check=False, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
                self._yt_cache_cleared = True

            # Try a couple of extractor args / formats to dodge SABR and image-only failures.
            # Preference order (as of 2026-03):
            #   web_creator — web-based, avoids SABR in recent yt-dlp builds
            #   ios         — may need PO Token but sometimes succeeds without
            #   mweb        — may need PO Token; keep as second option
            #   android     — needs GVS PO Token; URL sometimes still plays in VLC
            #   None        — yt-dlp defaults
            # tv_embedded excluded: YouTube now returns "not supported in this app"
            extractor_variants = []
            if YT_EXTRACTOR_ARGS:
                extractor_variants.append(YT_EXTRACTOR_ARGS)
            extractor_variants.extend([
                "youtube:player_client=web_creator",
                "youtube:player_client=ios",
                "youtube:player_client=mweb",
                "youtube:player_client=android",
                None,  # yt-dlp defaults
            ])

            format_variants = [
                YT_FORMAT,
                "bv*[ext=mp4][height<=480][fps<=30]+ba/best[ext=mp4]/bestvideo+bestaudio/best",
                "best",
            ]

            def _run_once(url: str, fmt: str, extractor: str | None):
                cmd = [YT_DLP_BIN, "-g", "-f", fmt]
                if extractor:
                    cmd += ["--extractor-args", extractor]
                if YT_FORCE_IPV4:
                    cmd.append("--force-ipv4")
                if YT_COOKIES_BROWSER:
                    cmd += ["--cookies-from-browser", YT_COOKIES_BROWSER]
                elif YT_COOKIES_FILE and os.path.isfile(YT_COOKIES_FILE):
                    cmd += ["--cookies", YT_COOKIES_FILE]
                cmd.append(url)
                return subprocess.check_output(cmd, text=True).splitlines()

            # Resolve URL (query -> ID -> URL) once
            if re.match(r"^https?://", query_or_url):
                target_url = query_or_url
            else:
                avoid = set(recent_for(resident)) if resident else set()
                vid = pick_random_youtube_id(query_or_url, YT_DLP_BIN, avoid_ids=avoid, max_candidates=25)
                if not vid:
                    raise RuntimeError("No YouTube candidates")
                target_url = f"https://www.youtube.com/watch?v={vid}"
                if resident:
                    remember(resident, vid)

            last_err = None
            for fmt in format_variants:
                for extractor in extractor_variants:
                    try:
                        streams = _run_once(target_url, fmt, extractor)
                        if streams:
                            stream_url = streams[0].strip()
                            print(f"[MEDIA] Using direct stream URL for VLC (fmt={fmt}, extractor={extractor})")
                            return self._play_media(stream_url, wid)
                    except Exception as e:
                        last_err = e
                        continue

            if last_err:
                raise last_err
            raise RuntimeError("yt-dlp produced no streams")

        except Exception as e:
            print("[MEDIA] VLC YouTube playback failed:", e)
            return False

    def play_direct(self, media_url: str, wid: int | None = None) -> bool:
        """Play a direct media URL (e.g. MP4 stream) via VLC without yt-dlp."""
        print(f"[MEDIA] VLC direct: {media_url}")
        self._stop_current()
        return self._play_media(media_url, wid)

    def play_background_radio(self, url: str) -> bool:
        """
        Start or keep a low-footprint radio stream running on a dedicated player.
        """
        if not url:
            return False
        try:
            # Already playing the same station
            if self._radio_url == url and self.radio_player.get_state() == vlc.State.Playing:
                return True

            media = self.instance.media_new(url)
            if url.startswith("http"):
                media.add_option(":http-user-agent=Mozilla/5.0")
                media.add_option(":network-caching=1500")
                media.add_option(":live-caching=1500")
                if YT_FORCE_IPV4:
                    media.add_option(":ipv4")
            self.radio_player.set_media(media)
            self.radio_player.play()
            self._radio_url = url
            try:
                state = self.radio_player.get_state()
                _log(f"[MEDIA] Background radio state after start: {state} ({url})")
            except Exception:
                pass
            return True
        except Exception as e:
            _log(f"[MEDIA] Background radio failed: {e}")
            return False

    def stop_background_radio(self):
        try:
            self.radio_player.stop()
        except Exception:
            pass
        self._radio_url = None

    # ------------------------------
    # Stop + lifecycle
    # ------------------------------
    def _stop_current(self):
        """Stop current media and clear stop flag so the next play can start cleanly."""
        self._stop_flag = False
        try:
            self.player.stop()
        except Exception:
            pass
        self._playing = False

    def stop(self):
        """External stop — sets the stop flag so _play_media will refuse to start new media."""
        try:
            self._stop_flag = True
            self.player.stop()
        except Exception:
            pass
        self._playing = False
        self.current_proc = None

    def toggle_pause(self):
        try:
            self.player.pause()
        except Exception as e:
            _log(f"[MEDIA] Pause toggle failed: {e}")

    def set_volume(self, percent: int):
        try:
            self.player.audio_set_volume(int(percent))
            self.radio_player.audio_set_volume(int(percent))
        except Exception:
            pass

    # ------------------------------
    # Track-waiting (Engine-compatible)
    # ------------------------------
    def wait_until_track_finishes_or_stop(self, check_interval=0.5):
        """
        VLC-style playback waiting, compatible with your existing Engine logic.
        """
        while True:
            if self._stop_flag:
                return False

            state = self.player.get_state()
            # Ended / Error / Stopped
            if state in (vlc.State.Ended, vlc.State.Error, vlc.State.Stopped):
                return True

            time.sleep(check_interval)

    def wait_for_first_frame(self, timeout=5):
        """
        Block until VLC starts actually rendering video frames.
        Returns True if a frame appeared, False on timeout.
        """
        start = time.time()
        while time.time() - start < timeout:
            state = self.player.get_state()
            if state == vlc.State.Playing:
                # VLC is actively outputting frames
                return True
            time.sleep(0.05)
        return False            

    def show_image(self, url: str, duration: int = 10, wid: int | None = None) -> bool:
        """
        Display a still image via VLC for the given duration (seconds).
        """
        try:
            ok = self._play_media(url, wid)
            if not ok:
                return False
            self.wait_for_first_frame(timeout=3)
            # Simple timed display; exit early if stop is requested
            elapsed = 0.0
            while elapsed < duration:
                if self._stop_flag:
                    return False
                time.sleep(0.2)
                elapsed += 0.2
            self.stop()
            return True
        except Exception as e:
            print("[VLC] show_image failed:", e)
            return False



# =========================
# Beacon parsing
# =========================
class BeaconParser:
    @staticmethod
    def eddystone_uid_from_advertisement(data: dict):
        try:
            svc_data = data.get("service_data", {})
            for uuid, payload in svc_data.items():
                if uuid.lower().replace("-", "") == EDDYSTONE_UUID:
                    b = bytes(payload)
                    if len(b) >= 18 and b[0] == 0x00:
                        ns = b[2:12].hex()
                        inst = b[12:18].hex()
                        return f"eddystone:{ns}{inst}"
        except Exception:
            pass
        return None

    @staticmethod
    def ibeacon_from_manufacturer(data: dict):
        try:
            mfg = data.get("manufacturer_data", {})
            payload = mfg.get(76) or mfg.get(0x004C)
            if payload and len(payload) >= 23 and payload[0] == 0x02 and payload[1] == 0x15:
                uuid = payload[2:18].hex()
                uuid_fmt = f"{uuid[0:8]}-{uuid[8:12]}-{uuid[12:16]}-{uuid[16:20]}-{uuid[20:32]}"
                major = int.from_bytes(payload[18:20], "big")
                minor = int.from_bytes(payload[20:22], "big")
                return f"ibeacon:{uuid_fmt}-{major:04d}-{minor:04d}"
        except Exception:
            pass
        return None

# =========================
# BLE listener (presence + trigger)
# =========================
class BeaconListener:
    def __init__(self, on_trigger, on_presence, detection_cooldown: int = 20):
        self.on_trigger = on_trigger
        self.on_presence = on_presence
        self._last_trigger = {}
        self._cooldown = detection_cooldown

    async def run(self):
        async with BleakScanner(detection_callback=self._callback) as _:
            print("[BLE] Scanning for beacons… (Ctrl+C to quit)")
            while True:
                await asyncio.sleep(0)

    def _callback(self, device, adv_data):
        parsed = {
            "service_data": adv_data.service_data,
            "manufacturer_data": adv_data.manufacturer_data,
        }
        key = BeaconParser.eddystone_uid_from_advertisement(parsed) or \
              BeaconParser.ibeacon_from_manufacturer(parsed)
        if not key:
            return

        now = time.time()

        # Always refresh presence
        try:
            self.on_presence(key)
        except Exception as e:
            print(f"[BLE] on_presence error: {e}")

        # Debounced trigger
        last = self._last_trigger.get(key, 0)
        if now - last < self._cooldown:
            return
        self._last_trigger[key] = now

        try:
            self.on_trigger(key)
        except Exception as e:
            print(f"[BLE] on_trigger error: {e}")

# =========================
# Engine with session + cache + UI
# =========================
class Engine:
    
    def __init__(self, survey: Survey, player: MediaPlayer, config: dict, ui: MediaUI):
        self._last_trigger = {}
        self.trigger_cooldown = 5  # seconds
        self.survey = survey
        self.player = player
        self.config = config
        self.ui = ui

        self.sessions = {}         # resident -> session dict
        self.session_threads = {}  # resident -> Thread
        self.session_locks = {}    # resident -> Lock
        self.stop_grace_misses = self.config.get("stop_grace_misses", 1)
        self.watchdog_interval = self.config.get("watchdog_interval", 0.5)
        self.control_mode = self.config.get("control_mode", "beacon")
        self._miss_counts = {}  # resident -> consecutive misses

        # apply config overrides
        global GONE_TIMEOUT, DETECTION_COOLDOWN, AVOID_RECENT_N, BIG_PLAYLIST_SIZE
        GONE_TIMEOUT = config.get('gone_timeout', GONE_TIMEOUT)
        DETECTION_COOLDOWN = config.get('detection_cooldown', DETECTION_COOLDOWN)
        AVOID_RECENT_N = config.get('avoid_recent_n', AVOID_RECENT_N)
        BIG_PLAYLIST_SIZE = config.get('big_playlist_size', BIG_PLAYLIST_SIZE)

        threading.Thread(target=self._presence_watchdog, daemon=True).start()

    def _gather_radio_urls(self, playlist, resident: str):
        urls = []

        def _maybe_add(u):
            if not u:
                return
            su = _maybe_add_scheme(str(u).strip())
            if not su:
                return
            urls.append(su)

        for item in playlist or []:
            if isinstance(item, dict):
                t = (item.get("type") or item.get("kind") or item.get("mediaType") or item.get("media_type") or "").lower()
                u = item.get("url") or item.get("radioUrl") or item.get("radio") or item.get("station")
                if t == "radio" or (u and _is_radio_url(str(u))):
                    _maybe_add(u)
            elif isinstance(item, str):
                if item.lower().startswith("radio:"):
                    _maybe_add(item.split(":", 1)[1])
                elif _is_radio_url(item):
                    _maybe_add(item)

        res_cfg = self.config.get("residents", {}).get(resident, {})
        if res_cfg.get("mode") == "radio" and res_cfg.get("url"):
            _maybe_add(res_cfg.get("url"))

        if DEFAULT_RADIO_URL:
            _maybe_add(DEFAULT_RADIO_URL)

        # Preserve order but drop dups
        seen = set()
        uniq = []
        for u in urls:
            if u in seen:
                continue
            uniq.append(u)
            seen.add(u)
        return uniq

    def _resident_for_beacon(self, beacon_key: str):
        beacons = self.config.get('beacons', {})
        entry = beacons.get(beacon_key)
        return entry.get('resident') if entry else None

    def _resident_identity(self, resident_name: str) -> ResidentIdentity:
        row = self.survey.row_for(resident_name) or {}
        key = row.get("ResidentGUID") or resident_name
        return ResidentIdentity(name=resident_name, key=key, survey_blob=row)

    # presence refresh for every advert
    def refresh_presence(self, beacon_key: str):
        resident = self._resident_for_beacon(beacon_key)
        if not resident:
            return

        # Update presence timestamp
        LAST_SEEN[resident] = time.time()

        # Show “Ready” when idle
        if resident not in self.sessions:
            ident = self._resident_identity(resident)
            self.ui.show_idle(ident, subtitle="Ready")


    def _presence_watchdog(self):
        """
        For a switch-powered beacon, absence means OFF.
        Stop playback within ~1 second of absence.
        """
        ABSENCE_TIMEOUT = 1.2  # second without packets = button off
        CHECK = 0.2            # check 5 times a second

        while True:
            try:
                now = time.time()

                for resident in list(LAST_SEEN.keys()):
                    last_seen = LAST_SEEN.get(resident, 0)

                # Switch turned OFF
                    if now - last_seen > ABSENCE_TIMEOUT:
                        print(f"[WATCHDOG] {resident} switch OFF — stopping session.")
                        self._stop_session(resident, from_thread=False)
                        LAST_SEEN.pop(resident, None)
                        LAST_OFF[resident] = now
                        self.ui.back_to_idle()

            except Exception as e:
                print(f"[WATCHDOG] error: {e}")

            time.sleep(CHECK)


    def _ensure_lock(self, resident):
        if resident not in self.session_locks:
            self.session_locks[resident] = threading.Lock()
        return self.session_locks[resident]

    def prepare_session(self, resident: str, *, playlist_override=None, ordered: bool = False, start_index: int = 0):
        """
        Build (or rebuild) a session for a resident without needing a beacon trigger.
        When ordered=True, playback will follow the playlist sequentially starting at start_index.
        """
        sess = self._make_session(resident, playlist_override=playlist_override, ordered=ordered, start_index=start_index)
        return sess

    def _stop_session(self, resident, from_thread: bool):
        s = self.sessions.get(resident)
        if s:
            s["running"] = False

        self.player.stop()
        self.player.stop_background_radio()

        t = self.session_threads.get(resident)
        if t and t.is_alive() and not from_thread and t is not threading.current_thread():
            try:
                t.join(timeout=2)
            except Exception:
                pass

        self.sessions.pop(resident, None)
        self.session_threads.pop(resident, None)
        self.session_locks.pop(resident, None)
        print(f"[ENGINE] Session stopped for {resident}")

    def start_manual_session(self, resident: str, *, playlist_override=None, start_index: int = 0, ordered: bool = True):
        """Start a session from a menu selection (no beacon required). ordered=False for shuffle."""
        with self._ensure_lock(resident):
            self._stop_session(resident, from_thread=False)
            sess = self._make_session(resident, playlist_override=playlist_override, ordered=ordered, start_index=start_index)
            self.sessions[resident] = sess
            t = threading.Thread(target=self._session_loop, args=(resident,), daemon=True)
            self.session_threads[resident] = t
            t.start()
            ident = self._resident_identity(resident)
            self.ui.show_preparing(ident, allow_loader=False)

    def handle_beacon_trigger(self, beacon_key: str):
        resident = self._resident_for_beacon(beacon_key)
        if not resident:
            return

        now = time.time()
        last = self._last_trigger.get(resident, 0)

    # Debounce to avoid spamming starts
        if now - last < self.trigger_cooldown:
            return

        self._last_trigger[resident] = now
        LAST_SEEN[resident] = now

    # Already playing → ignore
        if resident in self.sessions:
            return

        off_since = LAST_OFF.get(resident, 0)
        radio_first = (now - off_since) >= RADIO_FIRST_THRESHOLD
        if radio_first:
            print(f"[ENGINE] Starting session for {resident} (radio-first: off for {now - off_since:.0f}s)")
        else:
            print(f"[ENGINE] Starting session for {resident}")

        with self._ensure_lock(resident):
            sess = self._make_session(resident, radio_first=radio_first)
            self.sessions[resident] = sess

            t = threading.Thread(target=self._session_loop, args=(resident,), daemon=True)
            self.session_threads[resident] = t
            t.start()

    
    # Playlist building / caching
    def _make_session(self, resident, *, playlist_override=None, ordered: bool = False, start_index: int = 0, radio_first: bool = False):
        sess = {
            "resident": resident,
            "running": True,
            "last_track_end": 0.0,
            "playlist": [],
            "playlist_mode": "ordered" if ordered else "random",
            "cursor": max(0, start_index),
            "radio_first": radio_first,
        }

        if playlist_override is not None:
            sess["playlist"] = playlist_override or []
            sess["radio_urls"] = self._gather_radio_urls(sess["playlist"], resident)
            return sess

        # Pinned overrides
        res_cfg = self.config.get('residents', {}).get(resident, {})
        mode = res_cfg.get('mode')
        query = res_cfg.get('query')
        radio = res_cfg.get('url')
        if mode == "radio" and radio:
            sess["playlist"] = [radio]
            sess["force_radio"] = True
            sess["radio_urls"] = self._gather_radio_urls(sess["playlist"], resident)
            return sess
        if mode in {"music", "youtube"} and query:
            sess["playlist"] = [query]
            sess["radio_urls"] = self._gather_radio_urls(sess["playlist"], resident)
            return sess

        # Manual playlist override – takes priority over AI/cache
        try:
            remote_manual = fetch_manual_playlist_from_api(resident)
            if remote_manual:
                norm_remote = normalize_playlist_items(remote_manual)
                persist_manual_playlist(resident, norm_remote)
                _persist_debug_playlist(resident, norm_remote)
        except Exception as e:
            print(f"[MANUAL] Remote sync failed for {resident}: {e}")

        manual = load_manual_playlist(resident)
        if manual:
            norm = normalize_playlist_items(manual)
            sess["playlist"] = norm
            sess["manual_override"] = True
            persist_manual_playlist(resident, norm)
            _persist_debug_playlist(resident, norm)
            sess["radio_urls"] = self._gather_radio_urls(sess["playlist"], resident)
            return sess

        # LLM cache – fetch "This is Me" profile from Mobizio API (falls back to local cache)
        survey_row = self.survey.row_for(resident) or {}
        profile = fetch_resident_profile_from_api(resident) or THIS_IS_ME.row_for(resident) or {}

        combined = {
            "survey_row": survey_row,
            "this_is_me_profile": profile,
        }
        sv_hash = sha256_of(combined)

        cache = load_cached_playlist(resident)
        need_rebuild = True
        if (
            cache
            and cache.get("survey_hash") == sv_hash
            and isinstance(cache.get("playlist"), list)
            and cache["playlist"]
        ):
            sess["playlist"] = cache["playlist"]
            need_rebuild = False
            _persist_debug_playlist(resident, cache["playlist"])

        if need_rebuild:
            plist = llm_build_big_playlist(
                resident,
                survey_row=survey_row,
                profile=profile,
                want=BIG_PLAYLIST_SIZE,
            )
            if not plist:
                if DEFAULT_RADIO_URL:
                    plist = [DEFAULT_RADIO_URL]
                    sess["force_radio"] = True
                else:
                    plist = []
            sess["playlist"] = plist
            save_cached_playlist(resident, sv_hash, plist, meta={"source": "llm_big_with_profile"})
            try:
                push_ai_playlist_to_api(resident, plist, survey_hash=sv_hash, model=LLM_MODEL, meta={"source": "llm_big_with_profile"})
            except Exception as e:
                print(f"[API] Unable to push AI playlist for {resident}: {e}")
            _persist_debug_playlist(resident, plist)

        sess["radio_urls"] = self._gather_radio_urls(sess["playlist"], resident)
        if ordered:
            sess["playlist_mode"] = "ordered"
            sess["cursor"] = max(0, start_index)
        return sess

    def _pick_next_query_random(self, sess):
        playlist = sess.get("playlist") or []
        if sess.get("playlist_mode") == "ordered":
            cursor = sess.setdefault("cursor", 0)
            if cursor >= len(playlist):
                return None
            sess["cursor"] = cursor + 1
            return playlist[cursor]
        if not playlist:
            return None
        recent_q = sess.setdefault("recent_queries", deque(maxlen=AVOID_RECENT_N))

        def _classify_kind(item):
            """
            Lightweight classifier so we can alternate photos vs other media.
            """
            url = None
            if isinstance(item, dict):
                url = item.get("url") or item.get("query")
                t = (item.get("type") or item.get("kind") or item.get("mediaType") or item.get("media_type") or "").lower()
                if t in {"photo", "image", "picture"}:
                    return "photo"
                if t == "radio":
                    return "radio"
                if t:
                    return t
                radio_hint = item.get("radioUrl") or item.get("radio") or item.get("station")
                if radio_hint:
                    url = radio_hint
            else:
                if isinstance(item, str):
                    if item.lower().startswith("radio:"):
                        return "radio"
                    url = item
            if url and _is_image_url(url):
                return "photo"
            if url and _is_radio_url(url):
                return "radio"
            return "media"

        def _key(item):
            if isinstance(item, dict):
                return item.get("url") or item.get("query") or json.dumps(item, sort_keys=True)
            return str(item)

        # Prefer items that are not in the short-term recent list
        candidates = [q for q in playlist if _key(q) not in recent_q] or list(playlist)

        # Photos and radio are "background" content; videos/YouTube are "foreground".
        # After any background item, steer back toward foreground so videos aren't
        # crowded out by photo→radio→photo loops.
        # After foreground, pick freely — the natural random mix provides variety.
        _BG = {"photo", "image", "picture", "radio"}
        _FG = {"media", "video", "youtube"}
        last_kind = sess.get("last_kind")
        if last_kind in _BG:
            fg_candidates = [q for q in candidates if _classify_kind(q) in _FG]
            if fg_candidates:
                candidates = fg_candidates

        q = random.choice(candidates)
        recent_q.append(_key(q))
        sess["last_kind"] = _classify_kind(q)
        return q

    def _session_loop(self, resident):
        sess = self.sessions.get(resident)
        if not sess:
            return

        print(f"[ENGINE] Session loop running for {resident}")
        ident = self._resident_identity(resident)
        radio_options = sess.get("radio_urls") or []

        # Pre-fetch all photos for ambient slideshow (runs in background)
        sess_photos: list = []

        def _do_photo_prefetch():
            imgs = prefetch_session_photos(sess.get("playlist") or [])
            if imgs:
                sess_photos.extend(imgs)
                print(f"[PHOTO] {len(imgs)} photo(s) ready for ambient slideshow")
                # If radio started before photos finished loading, kick off the slideshow now
                if sess.get("last_kind") == "radio":
                    self.ui.start_ambient_slideshow(list(sess_photos))

        _has_photos = any(
            (isinstance(i, dict) and (i.get("type") or "").lower() in {"photo", "image", "picture"})
            or (isinstance(i, str) and _is_image_url(i))
            for i in (sess.get("playlist") or [])
        )
        if _has_photos:
            threading.Thread(target=_do_photo_prefetch, daemon=True).start()

        def _pick_radio_bed():
            if not radio_options:
                return None
            return radio_options[0]

        while sess["running"]:
            if not sess["running"]:
                break

            # If the button was off for ≥30s, start with radio + ambient photos
            if sess.pop("radio_first", False):
                playlist = sess.get("playlist") or []
                def _is_radio_item(item):
                    if isinstance(item, dict):
                        t = (item.get("type") or item.get("kind") or item.get("mediaType") or item.get("media_type") or "").lower()
                        if t == "radio":
                            return True
                        radio_hint = item.get("radioUrl") or item.get("radio") or item.get("station")
                        url = item.get("url") or item.get("query") or radio_hint or ""
                    else:
                        url = str(item)
                    if isinstance(url, str) and url.lower().startswith("radio:"):
                        return True
                    return bool(url and _is_radio_url(url))
                radio_items = [x for x in playlist if _is_radio_item(x)]
                if radio_items:
                    q = random.choice(radio_items)
                    sess["last_kind"] = "radio"
                    print(f"[ENGINE] Radio-first: starting with radio for {resident}")
                elif radio_options:
                    q = radio_options[0]
                    sess["last_kind"] = "radio"
                    print(f"[ENGINE] Radio-first: starting with radio bed for {resident}")
                else:
                    q = self._pick_next_query_random(sess)
            else:
                q = self._pick_next_query_random(sess)
            if not q:
                break

            # Normalize playlist item
            media_url = None
            force_radio = False
            is_photo = False
            is_youtube = False
            photo_via_ui = False
            photo_duration = 0
            display_label = None
            radio_hint = None

            # Unpack and classify the item
            if isinstance(q, dict):
                media_url = q.get("url") or q.get("mediaUrl") or q.get("query")
                item_type = (q.get("type") or q.get("kind") or q.get("mediaType") or q.get("media_type") or "").lower()
                force_radio = item_type == "radio"
                is_photo = item_type in {"photo", "image", "picture"}
                radio_hint = q.get("radioUrl") or q.get("radio") or q.get("station")
                # Allow radio: prefix even if type was omitted
                if media_url and media_url.lower().startswith("radio:"):
                    media_url = media_url.split(":", 1)[1].strip()
                    force_radio = True
                if not media_url and radio_hint:
                    media_url = radio_hint
                    force_radio = True
                display_label = q.get("name") or q.get("title") or media_url
            else:
                if isinstance(q, str) and q.lower().startswith("radio:"):
                    media_url = q.split(":", 1)[1].strip()
                    force_radio = True
                else:
                    media_url = q
                    force_radio = bool(sess.get("force_radio"))
                display_label = str(q)

            if isinstance(media_url, str):
                media_url = _maybe_add_scheme(media_url)

            # Heuristics: if no explicit type, infer from URL/extension
            if media_url:
                if isinstance(media_url, str) and media_url.startswith("http"):
                    media_url = _escape_spaces(media_url)
                if not is_photo and _is_image_url(media_url):
                    is_photo = True
                    force_radio = False
                if not force_radio and _is_radio_url(media_url):
                    force_radio = True
                    is_photo = False
                    _log(f"[ENGINE] Treating as radio: {media_url}")
                # Decide whether to use yt-dlp vs direct stream
                if not force_radio and not is_photo:
                    if _is_youtube_url(media_url) or not media_url.lower().startswith("http"):
                        is_youtube = True
                    else:
                        is_youtube = False

                    # Encode any spaces for VLC compatibility (e.g., blob paths with spaces)
                    if isinstance(media_url, str) and media_url.startswith("http"):
                        media_url = _escape_spaces(media_url)

            if not media_url:
                continue

            print(f"[ENGINE] Next query for {resident}: {media_url}")

            # 1) Prepare screen
            allow_loader = not (is_photo or force_radio)
            self.ui.show_preparing(ident, query=display_label or q, allow_loader=allow_loader)
            # While a video is loading, show ambient photos so the screen isn't blank
            if not is_photo and not force_radio and sess_photos:
                self.ui.start_ambient_slideshow(sess_photos)


            # Let Tk process the preparing screen before we request player
            self.ui.root.update_idletasks()
            self.ui.root.update()
            

            # Give Tk a moment to process the preparing UI
            time.sleep(0.05)

            # 2) Create player frame & capture WID (radio is audio-only; no window needed)
            wid = None
            if not photo_via_ui and not force_radio:
                wid_holder = {"wid": None}

                def _bind(win_id):
                    wid_holder["wid"] = win_id

                self.ui.show_player(lambda: _bind)

                # 3) Allow Tk to map & assign WID
                for _ in range(200):  # 0.5s max
                    if wid_holder["wid"] is not None:
                        break
                    time.sleep(0.01)

                wid = wid_holder["wid"]
                if wid is None:
                    print("[ENGINE] WARN: No window ID after waiting; skipping this track")
                    continue

                print(f"[ENGINE] Got WID: {wid}")

            # 4) Start VLC while keeping loading GIF visible
            if force_radio:
                self.player.stop_background_radio()
                ok = self.player.play_radio(media_url, wid=None)  # audio-only, no window
                # Stay on idle frame and show photos while radio plays
                if ok and sess_photos:
                    self.ui.start_ambient_slideshow(sess_photos)
            elif is_photo:
                self.ui.stop_ambient_slideshow()   # individual photo takes over
                bed = radio_hint or _pick_radio_bed()
                if bed:
                    self.player.play_background_radio(bed)
                else:
                    self.player.stop_background_radio()
                dur = 10
                if isinstance(q, dict):
                    try:
                        dur = int(q.get("durationSeconds") or q.get("duration") or dur)
                    except Exception:
                        pass
                photo_duration = dur
                photo_via_ui = True
                evt = threading.Event()
                self.ui.show_photo(media_url, on_ready=lambda: evt.set())
                evt.wait(timeout=3)
                ok = True
            elif is_youtube:
                self.player.stop_background_radio()
                ok = self.player.play_youtube(media_url, resident=resident, wid=wid)
            else:
                self.player.stop_background_radio()
                ok = self.player.play_direct(media_url, wid=wid)
            if not ok:
                print("[ENGINE] VLC start failed")
                time.sleep(1)
                continue

            # 5) Wait for VLC to produce the first frame
            if is_photo:
                # Individual photo shown via Tk; just clear prep spinner
                self.ui._stop_prep_animation()
            elif force_radio:
                # Radio: wait for stream to buffer, stay on idle frame with slideshow
                self.player.wait_for_first_frame(timeout=6)
                self.ui._stop_prep_animation()
            elif self.player.wait_for_first_frame(timeout=6):
                print("[ENGINE] VLC first frame ready")
                self.ui.stop_ambient_slideshow()   # hand over to video
                self.ui._stop_prep_animation()
            else:
                print("[ENGINE] WARNING: Video never reported Playing (timeout)")
                self.ui.stop_ambient_slideshow()

            # 6) Reveal video — not for radio (stays on idle) or photos (already shown)
            if not force_radio and not is_photo:
                self.ui.reveal_video()
            print(f"[ENGINE] Now showing {display_label or q}")

            # 7) Wait for the track to finish
            finished = True
            if is_photo and photo_via_ui:
                # Already displayed via Tk; just sleep for duration
                photo_end = time.time() + max(1, photo_duration)
                while time.time() < photo_end and sess["running"]:
                    time.sleep(0.2)
                finished = True
            elif not is_photo:
                finished = self.player.wait_until_track_finishes_or_stop()

            # 8) Between tracks: stop radio bed + slideshow, then briefly show idle
            self.player.stop_background_radio()
            self.ui.stop_ambient_slideshow()
            if sess["running"]:
                self.ui.back_to_idle()
                time.sleep(1)

        # Cleanup after session ends
        self._stop_session(resident, from_thread=True)
        self.ui.back_to_idle()


# =========================
# Remote control (FLIRC / TV remote)
# =========================
class RemoteMenuController:
    def __init__(self, engine: Engine, ui: MediaUI, config: dict):
        self.engine = engine
        self.ui = ui
        self.config = config
        self.mode = config.get("control_mode", "beacon")
        self.enabled = self.mode == "remote_menu"
        self.remote_cfg = config.get("remote_control", {}) if isinstance(config.get("remote_control", {}), dict) else {}
        self.resident = self.remote_cfg.get("resident") or self._default_resident()
        self.show_menu_on_start = bool(self.remote_cfg.get("show_menu_on_start", False))
        defaults = {"menu": "m", "up": "Up", "down": "Down", "select": "Return", "back": "Escape", "pause": "space"}
        override = self.remote_cfg.get("keymap") or {}
        self.keymap = {**defaults, **{k: v for k, v in override.items() if v}}
        self.playlist = []
        self.labels = []
        self._special_items = ["▶  Play All", "⇄  Shuffle"]
        self._last_key_time: dict = {}
        if not self.enabled:
            return
        self._bind_keys()
        self._start_focus_maintainer()
        # open_menu deferred — called via root.after() in main() after mainloop starts

    def _default_resident(self) -> str | None:
        try:
            res_cfg = self.config.get("remote_control", {}).get("resident")
            if res_cfg:
                return res_cfg
            res = self.config.get("residents", {})
            if isinstance(res, dict) and res:
                return list(res.keys())[0]
        except Exception:
            pass
        return None

    def _label_for_item(self, item) -> str:
        try:
            if isinstance(item, dict):
                name = item.get("name") or item.get("title") or item.get("query") or item.get("url") or item.get("radio") or item.get("station")
                t = (item.get("type") or item.get("kind") or item.get("mediaType") or "").lower()
                return f"[{t or 'media'}] {name}".strip()
            if isinstance(item, str):
                return item
            return str(item)
        except Exception:
            return str(item)

    def _ensure_playlist(self):
        if not self.resident:
            self.playlist = []
            self.labels = []
            return
        try:
            sess = self.engine.prepare_session(self.resident, ordered=True, start_index=0)
            self.playlist = sess.get("playlist") or []
            self.labels = [self._label_for_item(i) for i in self.playlist]
        except Exception as e:
            print(f"[REMOTE] Failed to build playlist: {e}")
            self.playlist = []
            self.labels = []

    def open_menu(self):
        self._ensure_playlist()
        title = f"{self.resident} • Playlist" if self.resident else "Playlist"
        if not self.playlist:
            self.ui.show_idle(ResidentIdentity(name=self.resident or "Guest", key=self.resident or "guest", survey_blob={}), subtitle="No playlist available")
            return
        all_labels = self._special_items + self.labels
        self.ui.show_menu(title, all_labels, selected_index=0, hint="Up/Down to navigate • OK to play • Back to exit")

    def toggle_menu(self, *_):
        if self.ui.is_menu_visible():
            self.ui.hide_menu()
        else:
            self.open_menu()

    def move_selection(self, delta: int, *_):
        if not self.ui.is_menu_visible():
            self.open_menu()
            return
        self.ui.highlight_menu(delta)

    def play_selected(self, *_):
        if not self.playlist:
            self.open_menu()
            return
        if not self.ui.is_menu_visible():
            self.open_menu()
            return
        idx = self.ui.current_menu_index() or 0
        n_special = len(self._special_items)
        self.ui.hide_menu()
        if idx < n_special:
            # Special top-of-menu actions
            if idx == 0:  # Play All
                self.engine.start_manual_session(self.resident, playlist_override=self.playlist, start_index=0, ordered=True)
            elif idx == 1:  # Shuffle
                shuffled = list(self.playlist)
                random.shuffle(shuffled)
                self.engine.start_manual_session(self.resident, playlist_override=shuffled, start_index=0, ordered=True)
        else:
            # Individual playlist item — play from that item in order
            item_idx = max(0, min(idx - n_special, len(self.playlist) - 1))
            self.engine.start_manual_session(self.resident, playlist_override=self.playlist, start_index=item_idx, ordered=True)

    def pause_or_resume(self, *_):
        self.engine.player.toggle_pause()

    def back(self, *_):
        if self.ui.is_menu_visible():
            self.ui.hide_menu()
        else:
            # Stop all sessions and return to idle
            for res in list(self.engine.sessions.keys()):
                self.engine._stop_session(res, from_thread=False)
            self.ui.back_to_idle()

    def _debounced(self, name: str, handler, ms: int = 200):
        """Wrap handler to suppress FLIRC key-repeat events within `ms` milliseconds."""
        def _wrapper(*args):
            now = time.monotonic()
            if now - self._last_key_time.get(name, 0) < ms / 1000.0:
                return
            self._last_key_time[name] = now
            handler(*args)
        return _wrapper

    def _start_focus_maintainer(self):
        """Periodically reclaim Tk focus so FLIRC events aren't captured by VLC sub-windows."""
        def _tick():
            try:
                self.ui.root.focus_force()
            except Exception:
                pass
            self.ui.root.after(400, _tick)
        self.ui.root.after(400, _tick)

    def _bind_keys(self):
        bindings = [
            ("menu",   self.toggle_menu),
            ("up",     lambda e=None: self.move_selection(-1)),
            ("down",   lambda e=None: self.move_selection(1)),
            ("select", self.play_selected),
            ("pause",  self.pause_or_resume),
            ("back",   self.back),
        ]
        for name, handler in bindings:
            key = self.keymap.get(name)
            if not key:
                continue
            try:
                # bind_all: captures events regardless of which child widget holds focus.
                # The Escape binding here also overrides the quit shortcut in MediaUI,
                # so Back on the remote returns to idle rather than exiting the process.
                self.ui.root.bind_all(f"<KeyPress-{key}>", self._debounced(name, handler))
                print(f"[REMOTE] Bound '{name}' → <{key}>")
            except Exception as e:
                print(f"[REMOTE] Failed to bind key {key}: {e}")

# =========================
# Graph poller
# =========================
def graph_poll_task(graph: GraphClient):
    last_seen = None
    while True:
        try:
            mod = graph.get_item_last_modified(DRIVE_ID, ITEM_ID, ITEM_PATH)
            if mod != last_seen:
                print(f"[GRAPH] Change detected ({mod}); downloading Excel…")

                graph.download_excel(LOCAL_SURVEY_XLSX, DRIVE_ID, ITEM_ID, ITEM_PATH)
                SURVEY.load()
                last_seen = mod
            else:
                print("[GRAPH] No change.")
        except Exception as e:
            print(f"[GRAPH] Poll error: {e}")
        time.sleep(GRAPH_POLL_SECONDS)

# =========================
# Bootstrap
# =========================
with open(CONFIG_PATH, 'r') as f:
    CONFIG = yaml.safe_load(f) or {}

# apply config
GONE_TIMEOUT = CONFIG.get('gone_timeout', GONE_TIMEOUT)
DETECTION_COOLDOWN = CONFIG.get('detection_cooldown', DETECTION_COOLDOWN)
AVOID_RECENT_N = CONFIG.get('avoid_recent_n', AVOID_RECENT_N)
BIG_PLAYLIST_SIZE = CONFIG.get('big_playlist_size', BIG_PLAYLIST_SIZE)

GRAPH = GraphClient(TENANT_ID, CLIENT_ID, CLIENT_SECRET)
SURVEY = Survey(LOCAL_SURVEY_XLSX)
THIS_IS_ME = ThisIsMe(THIS_IS_ME_XLSX)
PLAYER = MediaPlayer(volume_percent=VOLUME_PERCENT)

# We'll construct ENGINE in main() once UI exists

def _run_ble_loop(listener: BeaconListener):
    loop = asyncio.new_event_loop()
    asyncio.set_event_loop(loop)
    try:
        loop.run_until_complete(listener.run())
    finally:
        loop.close()

def main():
    global ENGINE
    control_mode = CONFIG.get("control_mode", "beacon")

    # initial sync
    try:
        print("[BOOT] Syncing survey…")
        GRAPH.download_excel(LOCAL_SURVEY_XLSX, DRIVE_ID, ITEM_ID, ITEM_PATH)
        SURVEY.load()
    except Exception as e:
        print(f"[BOOT] Sync failed: {e}. Using cached file if present.")
        if os.path.exists(LOCAL_SURVEY_XLSX):
            SURVEY.load()
        # Load This_is_Me profiles (local file)
    try:
        THIS_IS_ME.load()
    except Exception as e:
        print(f"[BOOT] Failed to load This_is_Me profiles: {e}")


    # Start UI on main thread
    ui = MediaUI()

    # Build engine with UI
    ENGINE = Engine(SURVEY, PLAYER, CONFIG, ui=ui)
    remote_controller = RemoteMenuController(ENGINE, ui, CONFIG)

    # Graph poller
    threading.Thread(target=graph_poll_task, args=(GRAPH,), daemon=True).start()

    # BLE listener using engine handlers (only when in beacon mode)
    if control_mode == "beacon":
        def on_trigger(beacon_key: str):
            ENGINE.handle_beacon_trigger(beacon_key)

        def on_presence(beacon_key: str):
            ENGINE.refresh_presence(beacon_key)

        listener = BeaconListener(on_trigger=on_trigger, on_presence=on_presence, detection_cooldown=DETECTION_COOLDOWN)
        threading.Thread(target=_run_ble_loop, args=(listener,), daemon=True).start()
    else:
        print("[BOOT] Beacon listener disabled (remote_menu control_mode)")
        if remote_controller.enabled and remote_controller.show_menu_on_start:
            # Defer until after mainloop starts so Tk is fully ready
            ui.root.after(500, remote_controller.open_menu)

    # Hand over to Tk mainloop
    ui.mainloop()

if __name__ == "__main__":
    main()
