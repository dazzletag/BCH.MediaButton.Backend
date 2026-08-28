#!/usr/bin/env python3
"""
ui_display.py — Fullscreen Tk UI for Media Button with async AI logo generation.

Views:
  • Idle: resident-branded screen with cached logo + optional "Up next: …"
  • Playing: embeds mpv/VLC via a native window id passed back to the engine

API (unchanged):
  ui.show_idle(resident, subtitle="Ready — press the button")
  ui.show_search(resident, query)  # shows "Up next: query" on the idle view
  ui.show_player(lambda: setter)   # switches to player view and returns window id
  ui.back_to_idle()
"""

import os, io, base64, threading, requests
from dataclasses import dataclass
from typing import Optional, Callable
from dotenv import load_dotenv

import tkinter as tk
from PIL import Image, ImageDraw, ImageFont, ImageOps, ImageTk
load_dotenv()
# ---- Config / theming ----
BASE_DIR = os.path.dirname(__file__)
LOADING_GIF_PATH = os.getenv("LOADING_GIF_PATH") or os.path.join(BASE_DIR, "loading_clean.gif")
USE_STABILITY_LOGOS = bool(os.getenv("STABILITY_API_KEY"))
STABILITY_ENGINE = os.getenv("STABILITY_ENGINE", "core")  # core | sd3 | ultra
LOGO_DIR = os.getenv("MEDIA_LOGO_DIR", ".data/logos")
FORCE_REGEN = os.getenv("MEDIA_LOGO_FORCE_REGEN", "0") == "1"
os.makedirs(LOGO_DIR, exist_ok=True)

BG = "#0b0b0f"      # background
FG = "#e8e8ec"      # primary text
ACCENT = "#6ee7ff"  # highlight accents
CARD_BG = "#121218" # idle card background
BORDER = ACCENT

@dataclass
class ResidentIdentity:
    name: str
    key: str
    survey_blob: dict


class AmbientSlideshow:
    """
    Gently crossfades a collection of PIL images on a Tk Label.
    Designed to run as a background visual while radio plays or a video is loading.

    Usage (always thread-safe):
        slideshow.load(pil_images)   # supply new images
        slideshow.start()            # begin cycling
        slideshow.stop()             # freeze and clear timer
    """

    INTERVAL_MS = 10_000   # ms each photo is on screen
    FADE_STEPS  = 10       # blend frames per transition
    FADE_MS     = 60       # ms between frames → 0.6 s total crossfade
    MAX_DIM     = 960      # cap fitted image dimension (saves RAM / CPU on Pi)

    def __init__(self, root: tk.Tk, label: tk.Label):
        self.root  = root
        self.label = label
        self._fitted: list = []   # pre-fitted PIL RGB images
        self._idx      = 0
        self._running  = False
        self._after_id = None
        self._tk_ref   = None     # prevents GC of the current PhotoImage

    # ── Public API (all thread-safe) ────────────────────────────────────────

    def load(self, pil_images: list):
        """Load a new set of images. Fits them on the Tk thread and restarts if running."""
        self.root.after(0, lambda imgs=pil_images: self._load_on_tk(imgs))

    def start(self):
        """Start cycling. Thread-safe."""
        self.root.after(0, self._start_on_tk)

    def stop(self):
        """Stop cycling. Thread-safe."""
        self.root.after(0, self._stop_on_tk)

    # ── Internal (Tk thread only) ────────────────────────────────────────────

    def _load_on_tk(self, pil_images: list):
        try:
            sw = min(self.root.winfo_screenwidth(),  self.MAX_DIM * 16 // 9)
            sh = min(self.root.winfo_screenheight(), self.MAX_DIM)
        except Exception:
            sw, sh = 1280, 720

        fitted = []
        for img in pil_images:
            try:
                fitted.append(self._fit(img.convert("RGB"), sw, sh))
            except Exception:
                pass
        self._fitted = fitted
        self._idx = 0
        if self._running and fitted:
            self._show_idx(0)

    @staticmethod
    def _fit(img, w: int, h: int):
        """Letterbox-fit image into (w, h) with black background."""
        img = img.copy()
        img.thumbnail((w, h), Image.LANCZOS)
        bg = Image.new("RGB", (w, h), (0, 0, 0))
        bg.paste(img, ((w - img.width) // 2, (h - img.height) // 2))
        return bg

    def _start_on_tk(self):
        if self._running or not self._fitted:
            return
        self._running = True
        self._show_idx(self._idx)

    def _stop_on_tk(self):
        self._running = False
        if self._after_id:
            try:
                self.root.after_cancel(self._after_id)
            except Exception:
                pass
            self._after_id = None

    def _show_idx(self, idx: int):
        if not self._running or not self._fitted:
            return
        n     = len(self._fitted)
        img_b = self._fitted[idx % n]
        if idx == 0 or n == 1:
            self._set(img_b)
            self._after_id = self.root.after(self.INTERVAL_MS, self._advance)
        else:
            img_a = self._fitted[(idx - 1) % n]
            self._fade(img_a, img_b, step=0)

    def _fade(self, img_a, img_b, step: int):
        if not self._running:
            return
        try:
            blended = Image.blend(img_a, img_b, step / self.FADE_STEPS)
        except Exception:
            blended = img_b
        self._set(blended)
        if step < self.FADE_STEPS:
            self._after_id = self.root.after(
                self.FADE_MS, lambda: self._fade(img_a, img_b, step + 1)
            )
        else:
            self._after_id = self.root.after(self.INTERVAL_MS, self._advance)

    def _set(self, pil_img):
        try:
            tk_img = ImageTk.PhotoImage(pil_img)
            self._tk_ref = tk_img
            self.label.configure(image=tk_img, bg="#000000")
            self.label.image = tk_img
        except Exception:
            pass

    def _advance(self):
        if not self._running:
            return
        self._idx = (self._idx + 1) % max(len(self._fitted), 1)
        self._show_idx(self._idx)


class MediaUI:
    def __init__(self, on_quit: Optional[Callable]=None):
        self.root = tk.Tk()
        self.root.title("Media Button")
        self.root.configure(bg=BG)
        self.root.attributes("-fullscreen", True)
        self.root.attributes("-topmost", True)
        self.root.after(300, lambda: self.root.attributes("-fullscreen", True))
        # Quit shortcuts — Escape is intentionally NOT listed here because
        # RemoteMenuController binds Escape to "Back" via bind_all (which takes
        # precedence).  Use Ctrl+Q or the 'q' key to exit at the keyboard.
        self.root.bind("<q>", lambda e: self._exit())
        self.root.bind("<Control-q>", lambda e: self._exit())
        self.on_quit = on_quit
        self._gif_delay_id = None


        print(f"[UI] Stability AI logos enabled: {USE_STABILITY_LOGOS} (engine={STABILITY_ENGINE})  FORCE_REGEN={FORCE_REGEN}")

        # Stack
        self.stack = tk.Frame(self.root, bg=BG)
        self.stack.pack(fill="both", expand=True)

        # ----- Idle (logo) view -----
        self.idle_frame = tk.Frame(self.stack, bg=BG)

        self.logo_label = tk.Label(self.idle_frame, bg=BG)
        self.logo_label.pack(pady=(30, 10))

        self.subtitle_var = tk.StringVar(value="")
        self.subtitle = tk.Label(
            self.idle_frame, textvariable=self.subtitle_var,
            fg=FG, bg=BG, font=("DejaVu Sans", 18)
        )
        self.subtitle.pack(pady=(0, 16))
        self._logo_target_w = None

        # "Up next" row (hidden when empty)
        upnext_wrap = tk.Frame(self.idle_frame, bg=BG)
        upnext_wrap.pack(pady=(6, 0))
        self.upnext_label = tk.Label(upnext_wrap, text="Up next:", fg=ACCENT, bg=BG, font=("DejaVu Sans", 18, "bold"))
        self.upnext_value = tk.Message(upnext_wrap, text="", width=1200, fg=FG, bg=BG, font=("DejaVu Sans", 20))
        self.upnext_label.grid(row=0, column=0, sticky="ne", padx=(0, 8))
        self.upnext_value.grid(row=0, column=1, sticky="nw")

        # Spacer
        tk.Label(self.idle_frame, text="", bg=BG).pack(pady=8)

        # ----- Player view (render target) -----
        self.play_frame = tk.Frame(self.stack, bg="black")
        self.video_holder = tk.Frame(self.play_frame, bg="black")
        self.video_holder.pack(fill="both", expand=True)

        # ----- Remote menu view -----
        self.menu_frame = tk.Frame(self.stack, bg=BG)
        self.menu_title_var = tk.StringVar(value="Select program")
        self.menu_title = tk.Label(
            self.menu_frame, textvariable=self.menu_title_var,
            fg=FG, bg=BG, font=("DejaVu Sans", 36, "bold"))
        self.menu_title.pack(pady=(20, 6))
        self.menu_canvas = tk.Canvas(self.menu_frame, bg=BG, highlightthickness=0)
        self.menu_canvas.pack(fill="both", expand=True, padx=20)
        self.menu_hint_var = tk.StringVar(value="")
        self.menu_hint = tk.Label(
            self.menu_frame, textvariable=self.menu_hint_var,
            fg="#666", bg=BG, font=("DejaVu Sans", 20))
        self.menu_hint.pack(pady=(4, 14))

        # ----- Card menu view (videos with thumbnails) -----
        self.card_menu_frame = tk.Frame(self.stack, bg=BG)
        self.card_title_var = tk.StringVar(value="")
        self.card_title_lbl = tk.Label(
            self.card_menu_frame, textvariable=self.card_title_var,
            fg=FG, bg=BG, font=("DejaVu Sans", 26, "bold"))
        self.card_title_lbl.pack(pady=(20, 6))
        self.card_canvas = tk.Canvas(self.card_menu_frame, bg=BG, highlightthickness=0)
        self.card_canvas.pack(fill="both", expand=True, padx=20)
        self.card_hint_var = tk.StringVar(value="")
        self.card_hint_lbl = tk.Label(
            self.card_menu_frame, textvariable=self.card_hint_var,
            fg="#666", bg=BG, font=("DejaVu Sans", 15))
        self.card_hint_lbl.pack(pady=(4, 14))

        # ----- Full-screen photo frame (remote photo navigation) -----
        self.photo_full_frame = tk.Frame(self.stack, bg="black")
        self.photo_full_label = tk.Label(self.photo_full_frame, bg="black")
        self.photo_full_label.pack(fill="both", expand=True)
        self._photo_full_ref = None   # prevent GC
        self._photo_gen = 0           # generation counter; stale fetches are dropped

        # Footer
        self.footer = tk.Label(self.stack, text="Bristol Care Homes", fg="#999", bg=BG, font=("DejaVu Sans", 14))
        self.footer.place(relx=1.0, rely=1.0, anchor="se", x=-16, y=-12)

        self.current_resident_key: Optional[str] = None
        self._logo_jobs: set[str] = set()  # in-flight AI jobs by resident key
        # in __init__, just after self._logo_jobs = set()
        self._current_frame: Optional[tk.Frame] = None
        self._search_debounce_id: Optional[str] = None
        self._logo_target_w = None

        # "Up next" state + preparing animation handle
        self._upnext_text: str = ""
        self._prep_anim_id: Optional[str] = None
        self._current_frame: Optional[tk.Frame] = None
        self._search_debounce_id: Optional[str] = None
        self._logo_target_w = None
        self._upnext_text: str = ""           # sticky Up Next
        self._prep_anim_id: Optional[str] = None
        self._menu_items: list[str] = []
        self._menu_selected: int = 0
        self._menu_mode: str = "list"   # "list" | "card"
        self._menu_canvas_refs: list = []
        self._card_data: list = []      # [[title, meta, thumb_pil_or_None], ...]
        self._card_image_refs: list = []


        self._show(self.idle_frame)
        self._set_upnext(None)  # will show last sticky value once we set it

        
        self._current_frame: Optional[tk.Frame] = None
        self._search_debounce_id: Optional[str] = None

        self._show(self.idle_frame)
        self._set_upnext(None)


        self._show(self.idle_frame)
        self._set_upnext(None)
        self._current_frame: Optional[tk.Frame] = None
        self._search_debounce_id: Optional[str] = None

        # Ambient slideshow (runs on logo_label while radio plays / video loads)
        self.slideshow = AmbientSlideshow(self.root, self.logo_label)

        # Warm-up: prepare idle frame fully before any beacon arrives
        self.root.update_idletasks()
        self.root.update()

        # Force the video_holder to exist and have a stable WID early
        self.play_frame.update_idletasks()
        self.video_holder.update_idletasks()
        self.root.update()

        # Steal input focus from the desktop immediately and again after the WM
        # settles — without this the desktop retains keyboard focus and remote-
        # control OK/Enter events land on whatever the cursor is hovering over.
        self.root.focus_force()
        self.root.after(1000, self.root.focus_force)
        self.root.after(3000, self.root.focus_force)

        # Re-claim focus whenever the window manager hands it elsewhere
        # (e.g. a system notification briefly takes it).
        self.root.bind("<FocusOut>", lambda e: self.root.after(200, self.root.focus_force))


    def schedule_loading_gif(self, delay_ms=500):
        # Cancel any previous schedules
        if self._gif_delay_id:
            try:
                self.root.after_cancel(self._gif_delay_id)
            except:
                pass
            self._gif_delay_id = None

        def _start():
            self._gif_delay_id = None
            print("[UI] Delayed GIF starting")
            self._start_loading_gif()

        self._gif_delay_id = self.root.after(delay_ms, _start)

        
    def reveal_video(self):
        """Actually switch to video frame once mpv is ready."""
        print("[UI] reveal_video() switching to play_frame")
        self._show(self.play_frame)    
        
    def _start_loading_gif(self):
        import itertools
        from PIL import Image as PIL_Image, ImageTk

        gif_path = LOADING_GIF_PATH
        if not os.path.exists(gif_path):
            print(f"[UI] No loading_clean.gif found at {gif_path}; skipping loader animation.")
            return

        print("[UI] Starting loading GIF animation")

        # --- Load & resize all frames ONCE ---
        if not hasattr(self, "_prep_frames"):
            try:
                pil = PIL_Image.open(gif_path)
            except Exception as e:
                print("[UI] GIF open failed:", e)
                return

            frames = []
            scale = 1.6  # make bigger
            try:
                while True:
                    frame = pil.copy().convert("RGBA")
                    w, h = frame.size
                    new_size = (int(w * scale), int(h * scale))
                    frame = frame.resize(new_size, PIL_Image.LANCZOS)
                    frames.append(ImageTk.PhotoImage(frame))
                    pil.seek(pil.tell() + 1)
            except EOFError:
                pass  # finished reading GIF frames

            if not frames:
                print("[UI] GIF had no frames")
                return

            self._prep_frames = frames
            self._prep_label = tk.Label(self.idle_frame, bg=BG)

        # Restart from frame 0
        self._prep_iter = itertools.cycle(self._prep_frames)
        self._prep_label.place(relx=0.5, rely=0.65, y=-194, anchor="center")


        # Animation loop
        def animate():
            if self._current_frame is self.play_frame:
                return
            frame = next(self._prep_iter)
            self._prep_label.configure(image=frame)
            self._prep_label.image = frame
            self.root.after(150, animate)  # slower animation

        animate()


    # ---------- Public API (thread-safe via `after`) ----------

    # ── Ambient slideshow (thread-safe public API) ───────────────────────────

    def start_ambient_slideshow(self, pil_images: list):
        """Load photos and begin the ambient crossfade slideshow. Thread-safe."""
        self.slideshow.load(pil_images)
        self.slideshow.start()

    def stop_ambient_slideshow(self):
        """Stop the ambient slideshow and restore normal label behaviour. Thread-safe."""
        self.slideshow.stop()

    # ────────────────────────────────────────────────────────────────────────

    def show_idle(self, resident: ResidentIdentity, subtitle: str = ""):
        def _do():
            self.slideshow._stop_on_tk()   # restore logo when going idle
            self.current_resident_key = resident.key
            self.subtitle_var.set(subtitle if subtitle else _today_long_date())
            img = self._ensure_logo_async(resident)  # returns immediate (cached or local)
            self._set_logo(img)
            self._show(self.idle_frame)
        self.root.after(0, _do)

    # --- tighten show_search to avoid rapid re-shows and keep Up Next sticky ---
    def show_search(self, resident: ResidentIdentity, query: str):
            """Show 'Up next: <query>' on the idle view (does not switch away)."""
            def _do():
                # Only (re)load logo if resident changed
                if resident.key != self.current_resident_key:
                    self.current_resident_key = resident.key
                    img = self._ensure_logo_async(resident)
                    self._set_logo(img)

                # Always refresh the Up Next text (don’t hide it here)
                self._set_upnext(query.strip() if query else None)

                # Only switch views if we’re not already on idle
                self._show(self.idle_frame)

                # clear debounce handle
                self._search_debounce_id = None

            # Debounce rapid calls (e.g., engine updating the query string repeatedly)
            if self._search_debounce_id:
                try:
                    self.root.after_cancel(self._search_debounce_id)
                except Exception:
                    pass
            self._search_debounce_id = self.root.after(120, _do)


    def show_player(self, binder):
        print("[UI] show_player CALLED")

        def _do():
            # Stop any prep animation but do NOT change frames
            #self._stop_prep_animation()

            # Prepare video frame but keep it HIDDEN
            self.play_frame.pack_forget()
            self.play_frame.update_idletasks()

            # Force creation of holder window
            self.video_holder.update_idletasks()
            self.root.update()

            # Now that the widget exists, get real WID
            wid = self.video_holder.winfo_id()
            print(f"[UI] Bound WID = {wid}")

            try:
                binder()(wid)
            except Exception as e:
                print("[UI] WID callback error:", e)

        self.root.after(0, _do)
   


    def back_to_idle(self):
        def _do():
            self.slideshow._stop_on_tk()   # clear any slideshow when returning to idle
            self.subtitle_var.set(_today_long_date())
            self._show(self.idle_frame)
            self.root.focus_force()  # reclaim focus from VLC so FLIRC events are received
        self.root.after(0, _do)


    def mainloop(self):
        self.root.mainloop()

    # ---------- Internal helpers ----------

    def _get_video_window_id(self) -> int:
        self.video_holder.update()
        return self.video_holder.winfo_id()

    # --- replace your _show with this idempotent version --
    def _show(self, frame: tk.Frame):
        print(f"[UI] SHOW: { 'play' if frame is self.play_frame else 'idle' }")

        if self._current_frame is frame:
            return  # already showing; avoid pack thrash (prevents flicker)
        for child in (self.idle_frame, self.play_frame, self.menu_frame,
                      self.card_menu_frame, self.photo_full_frame):
            child.pack_forget()
        frame.pack(fill="both", expand=True)
        self._current_frame = frame
        if frame is self.play_frame:
            self._stop_prep_animation()


   # replace your _set_upnext with this sticky version
    def _set_upnext(self, text: Optional[str], *, sticky: bool = True):
    # If sticky and text is None, keep whatever we already had visible
        if sticky and (text is None) and self._upnext_text:
            self.upnext_label.configure(text="Up next:")
            self.upnext_value.configure(text=self._upnext_text)
            self.upnext_label.grid(); self.upnext_value.grid()
            return

        if text:
            self._upnext_text = text
            self.upnext_label.configure(text="Up next:")
            self.upnext_value.configure(text=text)
            self.upnext_label.grid(); self.upnext_value.grid()
        else:
            self._upnext_text = ""
            self.upnext_label.grid_remove()
            self.upnext_value.grid_remove()

    # ----- Menu helpers -----

    _MENU_ROW_H = 90

    def show_menu(self, title: str, items: list[str], selected_index: int = 0, hint: str | None = None):
        def _do():
            self.menu_title_var.set(title or "Select program")
            self.menu_hint_var.set(hint or "Use arrows to navigate  •  OK to select  •  Back to exit")
            self._menu_items = list(items or [])
            self._menu_selected = max(0, min(selected_index, max(0, len(self._menu_items) - 1)))
            self._menu_mode = "list"
            self._redraw_menu_canvas()
            self._show(self.menu_frame)
            self.root.focus_force()
            self.root.update_idletasks()
        self.root.after(0, _do)

    def _redraw_menu_canvas(self):
        canvas = self.menu_canvas
        canvas.delete("all")
        self._menu_canvas_refs = []
        if not self._menu_items:
            return
        canvas.update_idletasks()
        cw = max(canvas.winfo_width(), 1240)
        ch = max(canvas.winfo_height(), 400)
        rh = self._MENU_ROW_H
        visible = max(1, ch // rh)
        n = len(self._menu_items)
        sel = self._menu_selected
        first = max(0, min(sel - visible // 2, max(0, n - visible)))
        for i in range(visible + 1):
            idx = first + i
            if idx >= n:
                break
            y0, y1 = i * rh, i * rh + rh
            if y0 > ch:
                break
            selected = (idx == sel)
            canvas.create_rectangle(
                0, y0 + 2, cw, y1 - 2,
                fill="#141a2a" if selected else BG, outline="")
            if selected:
                canvas.create_rectangle(0, y0 + 2, 7, y1 - 2, fill=ACCENT, outline="")
            else:
                canvas.create_line(20, y1 - 1, cw - 20, y1 - 1, fill="#1a1a26")
            canvas.create_text(
                22, y0 + rh // 2,
                text=self._menu_items[idx], anchor="w",
                fill=ACCENT if selected else FG,
                font=("DejaVu Sans", 32, "bold" if selected else "normal"),
                width=cw - 44,
            )

    def highlight_menu(self, delta: int):
        def _do():
            if not self._menu_items:
                return
            self._menu_selected = (self._menu_selected + delta) % max(len(self._menu_items), 1)
            if self._menu_mode == "card":
                self._redraw_card_canvas()
            else:
                self._redraw_menu_canvas()
        self.root.after(0, _do)

    def hide_menu(self):
        self.root.after(0, lambda: self._show(self.idle_frame))

    def current_menu_index(self) -> int | None:
        if not self._menu_items:
            return None
        return self._menu_selected

    def is_menu_visible(self) -> bool:
        return self._current_frame in (self.menu_frame, self.card_menu_frame)

    # ----- Video card menu (thumbnail grid) -----

    _CARD_H  = 150
    _CARD_GAP = 8
    _THUMB_W  = 192
    _THUMB_H  = 108

    def show_video_card_menu(self, videos: list):
        def _do():
            self.card_title_var.set("📼  Cached Videos")
            self.card_hint_var.set("Up/Down to select  •  OK to play  •  Back to return")
            self._card_data = []
            self._menu_items = []
            for row in videos:
                t = (row.get("title") or row.get("source_id") or "").strip() or "(untitled)"
                if row.get("_shuffle"):
                    meta = row.get("_meta_override", "")
                    self._card_data.append([t, meta, "shuffle"])
                else:
                    dur = _fmt_duration(row.get("duration_seconds"), row.get("filesize_bytes"))
                    plays = row.get("play_count") or 0
                    meta = dur + (f"   ▶ {plays}×" if plays else "")
                    self._card_data.append([t, meta, None])
                self._menu_items.append(t)
            self._menu_selected = 0
            self._menu_mode = "card"
            self._redraw_card_canvas()
            self._show(self.card_menu_frame)
            self.root.focus_force()
            self.root.update_idletasks()
            fps = [row.get("filepath") for row in videos]
            threading.Thread(target=self._load_thumbs_async, args=(fps,), daemon=True).start()
        self.root.after(0, _do)

    def _load_thumbs_async(self, filepaths: list):
        import subprocess as _sp
        for i, fp in enumerate(filepaths):
            if not fp or not os.path.exists(fp):
                continue
            thumb = fp + ".thumb.jpg"
            if not os.path.exists(thumb):
                try:
                    _sp.run(
                        ["ffmpeg", "-ss", "5", "-i", fp,
                         "-vframes", "1", "-q:v", "3", "-vf", "scale=320:180",
                         "-y", thumb],
                        capture_output=True, timeout=20,
                    )
                except Exception:
                    continue
            if not os.path.exists(thumb):
                continue
            try:
                img = Image.open(thumb).convert("RGB")
                def _upd(idx=i, im=img):
                    if idx < len(self._card_data) and self._menu_mode == "card":
                        self._card_data[idx][2] = im
                        self._redraw_card_canvas()
                self.root.after(0, _upd)
            except Exception:
                pass

    def _redraw_card_canvas(self):
        canvas = self.card_canvas
        canvas.delete("all")
        self._card_image_refs = []
        if not self._card_data:
            return
        canvas.update_idletasks()
        cw = max(canvas.winfo_width(), 1240)
        ch = max(canvas.winfo_height(), 400)
        per = self._CARD_H + self._CARD_GAP
        rows_visible = max(1, ch // per)
        n = len(self._card_data)
        sel = self._menu_selected
        first = max(0, min(sel - rows_visible // 2, max(0, n - rows_visible)))
        for i in range(rows_visible + 1):
            idx = first + i
            if idx >= n:
                break
            y = i * per + 4
            if y > ch:
                break
            title, meta, thumb = self._card_data[idx]
            img = self._make_card_pil(title, meta, thumb, idx == sel, cw)
            tk_img = ImageTk.PhotoImage(img)
            self._card_image_refs.append(tk_img)
            canvas.create_image(0, y, anchor="nw", image=tk_img)

    def _make_card_pil(self, title: str, meta: str, thumb_pil, selected: bool, card_w: int) -> Image.Image:
        bg = (18, 24, 40) if selected else (11, 11, 17)
        card = Image.new("RGB", (card_w, self._CARD_H), bg)
        draw = ImageDraw.Draw(card)
        if selected:
            draw.rectangle([0, 0, 7, self._CARD_H - 1], fill=ACCENT)
        tx, ty = 18, (self._CARD_H - self._THUMB_H) // 2
        if thumb_pil == "shuffle":
            ph = Image.new("RGB", (self._THUMB_W, self._THUMB_H), (10, 14, 26))
            pd = ImageDraw.Draw(ph)
            pd.rectangle([0, 0, self._THUMB_W - 1, self._THUMB_H - 1], outline=ACCENT)
            f_big = _safe_font(22, bold=True)
            f_sm  = _safe_font(15)
            tw, th = _measure_text(pd, "SHUFFLE", f_big)
            px = (self._THUMB_W - tw) // 2
            py = self._THUMB_H // 2 - th - 4
            pd.text((px, py), "SHUFFLE", font=f_big, fill=ACCENT)
            aw, _ = _measure_text(pd, "ALL", f_sm)
            pd.text(((self._THUMB_W - aw) // 2, py + th + 6), "ALL", font=f_sm, fill="#aaaacc")
            card.paste(ph, (tx, ty))
        elif isinstance(thumb_pil, Image.Image):
            try:
                card.paste(thumb_pil.resize((self._THUMB_W, self._THUMB_H), Image.LANCZOS), (tx, ty))
            except Exception:
                thumb_pil = None
        if thumb_pil is None:
            ph = Image.new("RGB", (self._THUMB_W, self._THUMB_H), (14, 14, 24))
            pd = ImageDraw.Draw(ph)
            pd.rectangle([0, 0, self._THUMB_W - 1, self._THUMB_H - 1], outline=(40, 40, 65))
            cx, cy = self._THUMB_W // 2, self._THUMB_H // 2
            pd.polygon([(cx - 18, cy - 22), (cx - 18, cy + 22), (cx + 24, cy)], fill=(55, 55, 90))
            card.paste(ph, (tx, ty))
        draw.rectangle(
            [tx, ty, tx + self._THUMB_W - 1, ty + self._THUMB_H - 1],
            outline=ACCENT if selected else "#222235",
        )
        text_x = tx + self._THUMB_W + 22
        title_y = ty + 4
        meta_y  = title_y + 46
        max_chars = max(20, (card_w - text_x - 20) // 13)
        draw.text((text_x, title_y), title[:max_chars],
                  font=_safe_font(22, bold=True), fill=ACCENT if selected else FG)
        draw.text((text_x, meta_y), meta,
                  font=_safe_font(22), fill="#aaaacc")
        if not selected:
            draw.line([(18, self._CARD_H - 1), (card_w - 18, self._CARD_H - 1)], fill="#181826")
        return card

# convenience wrappers
    def set_upnext(self, text: str):
        self._set_upnext(text, sticky=True)

    def clear_upnext(self):
        self._set_upnext("", sticky=False)

    def _set_logo(self, pil_img: Image.Image):
            try:
                from PIL import ImageTk

                # Reserve vertical space for subtitle + "Up next"
                # (tweak if your fonts/padding change)
                RESERVED_H = 280

                # Determine available size
                self.root.update_idletasks()
                sw = max(1, self.root.winfo_screenwidth())
                sh = max(1, self.root.winfo_screenheight())

                # Lock a pleasant max width (slightly smaller than screen)
                if getattr(self, "_logo_target_w", None) is None:
                    self._logo_target_w = min(max(540, sw - 260), 900)

                max_w = self._logo_target_w
                max_h = max(260, int(sh - RESERVED_H))  # leave room below the logo

                # Compute scale to satisfy BOTH constraints
                w0, h0 = max(1, pil_img.width), max(1, pil_img.height)
                scale_w = max_w / w0
                scale_h = max_h / h0
                scale = min(scale_w, scale_h)  # fit inside box

                new_w = max(1, int(w0 * scale))
                new_h = max(1, int(h0 * scale))

                resized = pil_img.resize((new_w, new_h), Image.LANCZOS)
                img_tk = ImageTk.PhotoImage(resized)

                # Apply without changing layout; other widgets stay visible
                self.logo_label.configure(image=img_tk, text="", compound="none")
                self.logo_label.image = img_tk

            except Exception as e:
                self.logo_label.configure(image="", compound="none",
                                          text="Media Button", fg=FG, bg=BG,
                                          font=("DejaVu Sans", 48, "bold"))
                print("[UI] Logo render failed:", e)


    # ---- Async/cached logo pipeline ----

    def _ensure_logo_async(self, resident: ResidentIdentity) -> Image.Image:
        """
        Return a logo image immediately (cached AI if present; else local),
        and if AI generation is enabled/needed, kick off a background job that
        will refresh the logo in-place when done.
        """
        safe = "".join(c for c in resident.key if c.isalnum() or c in ("_", "-")).strip("_-")
        path = os.path.join(LOGO_DIR, f"{safe}.png")

        # Use cached AI if available and not forcing regeneration
        if os.path.exists(path) and not FORCE_REGEN:
            try:
                print(f"[UI] Using cached logo: {path}")
                img = Image.open(path).convert("RGBA")
                # Optionally still refresh in background if USE_OPENAI_IMAGES and FORCE_REGEN is true
                return img
            except Exception as e:
                print(f"[UI] Failed to read cached logo ({e}); regenerating.")

        # Generate a local instant logo (non-blocking feel)
        local_img = self._generate_logo_local(resident)

        # If AI is enabled, spawn a background job (dedup per resident)
        if USE_STABILITY_LOGOS and resident.key not in self._logo_jobs:
            self._logo_jobs.add(resident.key)
            threading.Thread(
                target=self._ai_job,
                args=(resident, path),
                daemon=True
            ).start()

        return local_img

    # --- tiny guard in _ai_job swap to avoid accidental thrash during playback ---
    def _ai_job(self, resident: ResidentIdentity, out_path: str):
        try:
            print(f"[UI] (AI) Generating logo for {resident.name}…")
            ai_img = self._generate_logo_ai(resident)
            try:
                ai_img.save(out_path)
                print(f"[UI] (AI) Cached logo: {out_path}")
            except Exception as e:
                print(f"[UI] (AI) Failed to save logo cache: {e}")

            def _swap_if_current():
                # Only update the image; do NOT call _show here.
                if self.current_resident_key == resident.key and self._current_frame is self.idle_frame:
                    self._set_logo(ai_img)
                    # NOTE: we do NOT touch Up Next here, so it stays visible.
            self.root.after(0, _swap_if_current)

        except Exception as e:
            print(f"[UI] (AI) Generation failed: {e}")
        finally:
            self._logo_jobs.discard(resident.key)


    def _ensure_logo_path(self, resident: ResidentIdentity) -> str:
        safe = "".join(c for c in resident.key if c.isalnum() or c in ("_", "-")).strip("_-")
        return os.path.join(LOGO_DIR, f"{safe}.png")

    # ---------- Logo generation ----------

    def _generate_logo_ai(self, resident: ResidentIdentity) -> Image.Image:
        """
        Generate a personalised background image via Stability AI, then overlay
        the resident's name and vibe line using PIL text (AI models can't do text).
        Falls back to the local PIL render if the API call fails.
        """
        api_key = os.getenv("STABILITY_API_KEY")
        if not api_key:
            return self._generate_logo_local(resident)

        likes_line = self._vibe_line(resident.survey_blob)
        # Prompt asks for a background scene only — no text, no letters
        prompt = (
            f"A warm, cheerful abstract background scene inspired by someone who enjoys "
            f"{likes_line}. Vibrant colours, cinematic 16:9 format, painterly style, "
            "no text, no letters, no words, no people, no faces."
        )

        url = f"https://api.stability.ai/v2beta/stable-image/generate/{STABILITY_ENGINE}"
        try:
            resp = requests.post(
                url,
                headers={
                    "Authorization": f"Bearer {api_key}",
                    "Accept": "image/*",
                },
                data={
                    "prompt": prompt,
                    "aspect_ratio": "16:9",
                    "output_format": "png",
                },
                files={"none": ("", b"")},  # required for multipart
                timeout=60,
            )
            resp.raise_for_status()
            bg = Image.open(io.BytesIO(resp.content)).convert("RGBA")
            print(f"[UI] (AI) Stability background generated for {resident.name} ({len(resp.content)} bytes)")
        except Exception as e:
            print(f"[UI] (AI) Stability API failed ({e}); falling back to local render")
            return self._generate_logo_local(resident)

        # ── Overlay PIL text on top of the AI background ──────────────────────
        W, H = 1600, 900
        bg = bg.resize((W, H), Image.LANCZOS)

        # Semi-transparent dark bar so text is always readable
        title   = f"{resident.name} TV"
        draw    = ImageDraw.Draw(bg)
        title_font = _safe_font(size=140, bold=True)
        sub_font   = _safe_font(size=54)

        tw, th = _measure_text(draw, title, title_font)
        sw, sh = _measure_text(draw, likes_line, sub_font)
        ty = H // 2 - th - 20

        bar_pad = 30
        bar = Image.new("RGBA", (W, th + bar_pad * 2 + 60 + sh), (0, 0, 0, 160))
        bg.alpha_composite(bar, dest=(0, ty - bar_pad))

        tx = (W - tw) // 2
        _text_outline(draw, (tx, ty), title, title_font, fill="#ffffff", outline="#000", width=6)

        draw.text(((W - sw) // 2, ty + th + 30), likes_line, font=sub_font, fill=ACCENT)

        return bg

    def _generate_logo_local(self, resident: ResidentIdentity) -> Image.Image:
        """Crisp locally rendered logo card."""
        title = f"{resident.name} TV"
        vibe = self._vibe_line(resident.survey_blob)
        W, H = 1600, 900
        base = Image.new("RGBA", (W, H), color=BG)
        draw = ImageDraw.Draw(base)

        # Card
        margin = 80
        card = Image.new("RGBA", (W - 2*margin, H - 2*margin), CARD_BG)
        from PIL import Image as PIL_Image  # avoid shadowing
        card = ImageOps.expand(card, border=6, fill=BORDER)
        base.alpha_composite(card, dest=(margin, margin))

        title_font = _safe_font(size=140, bold=True)
        sub_font   = _safe_font(size=54)

        w, h = _measure_text(draw, title, title_font)
        x = (W - w)//2
        y = H//2 - h - 20
        _text_outline(draw, (x, y), title, title_font, fill=FG, outline="#000", width=6)

        sw, sh = _measure_text(draw, vibe, sub_font)
        sx = (W - sw)//2
        sy = y + h + 30
        draw.text((sx, sy), vibe, font=sub_font, fill=ACCENT)

        return base
        
    def show_preparing(self, resident: ResidentIdentity, query: Optional[str] = None, allow_loader: bool = True):
        def _do():
                self.current_resident_key = resident.key

                # Up next
                if query:
                        self.set_upnext(query)
                else:
                        self._set_upnext(None, sticky=True)

                # Start “Preparing…” animation (only if loader allowed)
                if allow_loader:
                        print("[UI] Starting Animation")
                        self._start_prep_animation()
                else:
                        self._stop_prep_animation()

                # Ensure logo
                img = self._ensure_logo_async(resident)
                self._set_logo(img)

                # Start GIF only after 1 second if still waiting
                if allow_loader:
                        self.schedule_loading_gif(delay_ms=1000)
                else:
                        # Cancel any pending loader
                        try:
                                self.root.after_cancel(self._gif_delay_id)
                        except Exception:
                                pass
                        self._gif_delay_id = None


                # Stay on idle/preparing frame
                self._show(self.idle_frame)
                self.root.update_idletasks()
                self.root.update()

        self.root.after(0, _do)

        
    def _start_prep_animation(self):
        self._stop_prep_animation()
        dots = ["", ".", "..", "..."]
        i = 0
        def tick():
            nonlocal i
            self.subtitle_var.set(f"Preparing program{dots[i % 4]}")
            i += 1
            self._prep_anim_id = self.root.after(400, tick)
        tick()

    def _stop_prep_animation(self):
        # stop preparing dots animation
        if self._prep_anim_id:
            try:
                self.root.after_cancel(self._prep_anim_id)
            except:
                pass
            self._prep_anim_id = None

        # hide loading GIF if present
        if hasattr(self, "_prep_label"):
            self._prep_label.place_forget()

        # cancel scheduled GIF if exists
        if self._gif_delay_id:
            try:
                self.root.after_cancel(self._gif_delay_id)
            except:
                pass
            self._gif_delay_id = None

    def show_photo(self, url: str, on_ready=None):
        """Fetch and display a still image directly in Tk (no VLC), reusing the idle/logo frame."""
        def _do():
            self.slideshow._stop_on_tk()   # individual photo takes over from slideshow
            try:
                resp = requests.get(url, timeout=10)
                resp.raise_for_status()
                data = io.BytesIO(resp.content)
                img = Image.open(data).convert("RGBA")
            except Exception as e:
                print(f"[UI] Photo fetch failed: {e}")
                if on_ready:
                    on_ready()
                return

            # Fit to idle frame area
            self.idle_frame.update_idletasks()
            vw = max(self.idle_frame.winfo_width(), 640)
            vh = max(self.idle_frame.winfo_height(), 480)
            try:
                img.thumbnail((vw, vh), Image.LANCZOS)
            except Exception:
                pass

            photo = ImageTk.PhotoImage(img)
            # Replace logo with photo
            self.logo_label.configure(image=photo, bg=BG)
            self.logo_label.image = photo
            print(f"[UI] Photo rendered ({img.size[0]}x{img.size[1]}) from {url}")
            self._stop_prep_animation()
            self._show(self.idle_frame)
            self.root.update_idletasks()
            self.root.lift()  # ensure window is on top
            if on_ready:
                on_ready()

        self.root.after(0, _do)



    def show_photo_fullscreen(self, url: str, counter: str = "", on_error=None):
        """Fetch a photo and fill the screen edge-to-edge (black letterbox).
        counter is an optional overlay string, e.g. '2 / 8'.
        Thread-safe; stale fetches from rapid navigation are silently dropped.
        on_error is called on the Tk thread when the photo cannot be shown, so
        the caller can skip to the next one rather than leaving the screen on
        whatever happened to be there — which reads as 'Photos does nothing'."""
        self._photo_gen += 1
        my_gen = self._photo_gen

        def _fail(e):
            def _run():
                if my_gen != self._photo_gen:
                    return  # superseded by a newer navigation press
                if on_error:
                    on_error()
            print(f"[UI] Fullscreen photo fetch failed: {e}")
            self.root.after(0, _run)

        def _fetch():
            try:
                resp = requests.get(url, timeout=12)
                resp.raise_for_status()
                img = Image.open(io.BytesIO(resp.content)).convert("RGB")
            except Exception as e:
                _fail(e)
                return

            def _display(im=img):
                if my_gen != self._photo_gen:
                    return  # superseded by a newer navigation press
                try:
                    self.root.update_idletasks()
                    sw = max(self.root.winfo_screenwidth(), 640)
                    sh = max(self.root.winfo_screenheight(), 480)
                    im_w, im_h = im.size
                    scale = min(sw / im_w, sh / im_h)
                    new_w = max(1, int(im_w * scale))
                    new_h = max(1, int(im_h * scale))
                    resized = im.resize((new_w, new_h), Image.LANCZOS)
                    bg = Image.new("RGB", (sw, sh), (0, 0, 0))
                    bg.paste(resized, ((sw - new_w) // 2, (sh - new_h) // 2))
                    if counter:
                        draw = ImageDraw.Draw(bg)
                        font = _safe_font(28)
                        tw, th = _measure_text(draw, counter, font)
                        # subtle semi-transparent pill behind text
                        pad = 10
                        draw.rectangle(
                            [sw - tw - pad * 2 - 16, sh - th - pad * 2 - 12,
                             sw - 16, sh - 12],
                            fill=(0, 0, 0, 160),
                        )
                        draw.text((sw - tw - pad - 16, sh - th - pad - 12),
                                  counter, font=font, fill="white")
                    tk_img = ImageTk.PhotoImage(bg)
                    self._photo_full_ref = tk_img
                    self.photo_full_label.configure(image=tk_img)
                    self.photo_full_label.image = tk_img
                    self._show(self.photo_full_frame)
                except Exception as e:
                    print(f"[UI] Fullscreen photo display failed: {e}")
                    if on_error:
                        on_error()

            self.root.after(0, _display)

        threading.Thread(target=_fetch, daemon=True).start()

    @staticmethod
    def _vibe_line(s: dict) -> str:
        likes = []
        for k in ("music_60s","music_70s","rock","classical","radio","nature","crafts","sports","documentaries"):
            v = s.get(k)
            if v in (True, "TRUE", "Yes", "YES", "Y", 1, "1"):
                likes.append(k.replace("_"," ").title())
        return f"Loves: {', '.join(likes[:3])}" if likes else "Curated by us for you"

    def _exit(self):
        if self.on_quit:
            try: self.on_quit()
            except: pass
        self.root.destroy()
        os._exit(0)

# ---------- Font / drawing helpers ----------

def _fmt_duration(secs, size_bytes) -> str:
    parts = []
    if secs:
        total_m = int(secs) // 60
        h, m = divmod(total_m, 60)
        if h and m:
            parts.append(f"{h} hour{'s' if h != 1 else ''} {m} mins")
        elif h:
            parts.append(f"{h} hour{'s' if h != 1 else ''}")
        else:
            parts.append(f"{max(total_m, 1)} min{'s' if total_m != 1 else ''}")
    if size_bytes:
        parts.append(f"{size_bytes / 1_048_576:.0f} MB")
    return "  ".join(parts) if parts else ""


def _safe_font(size=64, bold=False):
    try:
        path = "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf" if bold else "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"
        return ImageFont.truetype(path, size)
    except Exception:
        return ImageFont.load_default()

from datetime import datetime

def _ordinal(n: int) -> str:
    return "th" if 11 <= (n % 100) <= 13 else {1: "st", 2: "nd", 3: "rd"}.get(n % 10, "th")

def _today_long_date() -> str:
    now = datetime.now()  # local time (UK for you)
    return f'{now.strftime("%A")}, {now.day}{_ordinal(now.day)} {now.strftime("%B")}'

def _measure_text(draw: ImageDraw.ImageDraw, text: str, font: ImageFont.FreeTypeFont):
    """Pillow ≥10: textbbox; fallback to getsize."""
    try:
        left, top, right, bottom = draw.textbbox((0, 0), text, font=font)
        return right - left, bottom - top
    except Exception:
        try:
            return font.getsize(text)
        except Exception:
            return (len(text) * (font.size // 2), font.size)

def _text_outline(draw: ImageDraw.ImageDraw, xy, text, font, fill, outline="#000000", width=3):
    x, y = xy
    for dx in range(-width, width+1):
        for dy in range(-width, width+1):
            if dx*dx + dy*dy <= width*width:
                draw.text((x+dx, y+dy), text, font=font, fill=outline)
    draw.text((x, y), text, font=font, fill=fill)
if __name__ == "__main__":
    demo = MediaUI()
    ident = ResidentIdentity(name="Alice", key="alice-001", survey_blob={"music_60s": True, "documentaries": True})
    demo.show_preparing(ident, query="ABBA – Dancing Queen")
    demo.root.after(4000, lambda: demo.show_search(ident, "Nature documentary David Attenborough"))
    demo.root.after(8000, lambda: demo.back_to_idle())
    demo.mainloop()
