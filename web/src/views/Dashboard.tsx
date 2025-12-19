import { useCallback, useEffect, useMemo, useState } from "react";
import { useIsAuthenticated, useMsal } from "@azure/msal-react";
import { useApiClient } from "../hooks/useApiClient";
import { appConfig } from "../config";
import type { ManualPlaylistResponse, MediaItem, MediaType, Playlist, ResidentList } from "../types";

type UploadRequest = {
  fileName: string;
  type: MediaType;
  contentType?: string | null;
  durationSeconds?: number | null;
};

type UploadResponse = {
  uploadUrl: string;
  blobPath: string;
  expiresAtUtc: string;
};

type MediaRegisterRequest = {
  blobPath: string;
  type: MediaType;
  name?: string | null;
  contentType?: string | null;
  durationSeconds?: number | null;
};

function formatDate(dateString?: string) {
  if (!dateString) return "-";
  return new Intl.DateTimeFormat("en-GB", {
    month: "short",
    day: "numeric",
    year: "numeric",
  }).format(new Date(dateString));
}

function StatCard({
  title,
  value,
  hint,
}: {
  title: string;
  value: string;
  hint: string;
}) {
  return (
    <div className="card glass">
      <div className="card-header">
        <p className="card-title">{title}</p>
        <span className="status-dot" />
      </div>
      <div className="metric">{value}</div>
      <div className="muted">{hint}</div>
    </div>
  );
}

export default function Dashboard() {
  const isAuthed = useIsAuthenticated();
  const { instance, accounts } = useMsal();
  const { call } = useApiClient();

  const [playlists, setPlaylists] = useState<Playlist[]>([]);
  const [media, setMedia] = useState<MediaItem[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [uploading, setUploading] = useState(false);
  const [assigning, setAssigning] = useState(false);

  const [selectedMediaType, setSelectedMediaType] = useState<MediaType>("Photo");
  const [selectedFile, setSelectedFile] = useState<File | null>(null);
  const [mediaName, setMediaName] = useState("");
  const [duration, setDuration] = useState<number | undefined>(undefined);

  const [residentList, setResidentList] = useState<string[]>([]);
  const [residentQuery, setResidentQuery] = useState("");
  const [manualText, setManualText] = useState("");
  const [manualMeta, setManualMeta] = useState<{ updatedAt?: string | null; updatedBy?: string | null }>({});
  const [loadingResident, setLoadingResident] = useState(false);
  const [savingManual, setSavingManual] = useState(false);

  const [playlistId, setPlaylistId] = useState("current");
  const [seasonalTheme, setSeasonalTheme] = useState("");
  const [radioFavorites, setRadioFavorites] = useState<string[]>([]);
  const [urlInput, setUrlInput] = useState("");
  const [playlistUrls, setPlaylistUrls] = useState<string[]>([]);
  const [saveMessage, setSaveMessage] = useState<string | null>(null);

  const accountName = useMemo(() => accounts[0]?.name ?? "Signed-in user", [accounts]);

  const loadData = useCallback(async () => {
    if (!isAuthed) return;
    setLoading(true);
    setError(null);
    try {
      const [playlistData, mediaData] = await Promise.all([
        call<Playlist[]>({ url: "/api/admin/playlists", method: "GET" }),
        call<MediaItem[]>({ url: "/api/admin/media", method: "GET" }),
      ]);
      setPlaylists(playlistData);
      setMedia(mediaData);
    } catch (err) {
      console.error(err);
      setError("Unable to load data. Please check your permissions or API settings.");
    } finally {
      setLoading(false);
    }
  }, [call, isAuthed]);

  useEffect(() => {
    loadData();
  }, [loadData]);

  useEffect(() => {
    if (playlists.length && !playlistId) {
      setPlaylistId(playlists[0].id || "current");
    }
  }, [playlists, playlistId]);

  useEffect(() => {
    const fetchResidents = async () => {
      try {
        const residents = await call<ResidentList>({ url: "/api/admin/residents", method: "GET" });
        setResidentList(residents || []);
      } catch (err) {
        console.error("Failed to load resident list", err);
      }
    };
    fetchResidents();
  }, [call]);

  const applyOptionsToManual = (base: string[]) => {
    const extras: string[] = [];
    if (seasonalTheme) extras.push(`season:${seasonalTheme}`);
    radioFavorites.forEach((u) => extras.push(`radio:${u}`));
    playlistUrls.forEach((u) => extras.push(u));
    const combined = Array.from(new Set([...base, ...extras]));
    setManualText(combined.join("\n"));
    return combined;
  };

  const loadResidentManual = useCallback(async () => {
    if (!residentQuery.trim()) {
      setError("Enter a resident name to load playlist.");
      return;
    }
    setLoadingResident(true);
    setError(null);
    try {
      const data = await call<ManualPlaylistResponse>({
        url: `/api/admin/residents/${encodeURIComponent(residentQuery.trim())}/manual-playlist`,
        method: "GET",
      });
      const items = data?.items ?? [];
      const withOptions = applyOptionsToManual(items);
      setManualText(withOptions.join("\n"));
      setManualMeta({ updatedAt: data?.updatedAtUtc, updatedBy: data?.updatedBy });
    } catch (err) {
      console.error(err);
      setManualText("");
      setManualMeta({});
      setError("Could not load playlist for that resident.");
    } finally {
      setLoadingResident(false);
    }
  }, [call, residentQuery, applyOptionsToManual]);

  const saveResidentManual = useCallback(
    async (overrideItems?: string[]) => {
      if (!residentQuery.trim()) {
        setError("Resident name is required.");
        return;
      }
      const items =
        overrideItems ??
        manualText
          .split(/\r?\n/)
          .map((l) => l.trim())
          .filter(Boolean);
      setSavingManual(true);
      setError(null);
      try {
        await call({
          url: `/api/admin/residents/${encodeURIComponent(residentQuery.trim())}/manual-playlist`,
          method: "PUT",
          data: { items },
        });
        setManualMeta({ updatedAt: new Date().toISOString(), updatedBy: accountName });
        setSaveMessage("Manual playlist saved.");
      } catch (err) {
        console.error(err);
        setError("Failed to save manual playlist.");
      } finally {
        setSavingManual(false);
      }
    },
    [accountName, call, manualText, residentQuery]
  );

  const onUploadMedia = useCallback(
    async (evt: React.FormEvent) => {
      evt.preventDefault();
      if (!selectedFile) {
        setError("Select a file to upload.");
        return;
      }
      setUploading(true);
      setError(null);
      try {
        // API expects numeric enum (0 Photo, 1 Video)
        const apiMediaType = selectedMediaType === "Photo" ? 0 : 1;
        const uploadBody: UploadRequest = {
          fileName: selectedFile.name,
          type: apiMediaType as unknown as MediaType,
          contentType: selectedFile.type || null,
          durationSeconds: duration || null,
        };

        const upload = await call<UploadResponse>({
          url: "/api/admin/media/upload-url",
          method: "POST",
          data: uploadBody,
        });

        await fetch(upload.uploadUrl, {
          method: "PUT",
          headers: {
            "x-ms-blob-type": "BlockBlob",
            "Content-Type": selectedFile.type || "application/octet-stream",
          },
          body: selectedFile,
        });

        const registerBody: MediaRegisterRequest = {
          blobPath: upload.blobPath,
          type: apiMediaType as unknown as MediaType,
          name: mediaName || selectedFile.name,
          contentType: selectedFile.type || null,
          durationSeconds: duration || null,
        };

        await call({
          url: "/api/admin/media",
          method: "POST",
          data: registerBody,
        });

        // Add uploaded item into manual playlist view
        const updated = applyOptionsToManual([...manualText.split(/\r?\n/).filter(Boolean), mediaName || selectedFile.name]);
        setManualText(updated.join("\n"));
        await saveResidentManual(updated);

        setSelectedFile(null);
        setMediaName("");
        setDuration(undefined);
        await loadData();
      } catch (err: any) {
        console.error(err);
        const serverMessage =
          err?.response?.data && typeof err.response.data === "string"
            ? err.response.data
            : err?.response?.data?.message ||
              err?.response?.data?.error ||
              err?.message;
        setError(
          serverMessage
            ? `Upload failed: ${serverMessage}`
            : "Upload failed. Check storage settings and try again."
        );
      } finally {
        setUploading(false);
      }
    },
    [call, duration, loadData, manualText, mediaName, saveResidentManual, selectedFile, selectedMediaType, applyOptionsToManual]
  );

  const onAssignPlaylist = useCallback(
    async (evt: React.FormEvent) => {
      evt.preventDefault();
      if (!residentQuery.trim()) {
        setError("Resident and playlist are required.");
        return;
      }
      setAssigning(true);
      setError(null);
      setSaveMessage(null);
      try {
        // Ensure latest manual playlist is saved before sending
        await saveResidentManual();
        const payload = {
          playlistId: (playlistId || "current").trim(),
          radioFavorites,
          playlistUrls,
          seasonalTheme,
          resident: residentQuery.trim(),
        };
      await call({
        url: `/api/admin/residents/${encodeURIComponent(residentQuery.trim())}/playlist`,
        method: "PUT",
        data: payload,
      });
      setSaveMessage("Playlist sent to Press & Play. The Pi will pull it down shortly.");
    } catch (err) {
      console.error(err);
      setError("Assignment failed. Confirm the resident and playlist.");
    } finally {
      setAssigning(false);
      }
    },
    [call, playlistId, playlistUrls, radioFavorites, residentQuery, seasonalTheme]
  );

  const mediaByType = useMemo(
    () => ({
      photo: media.filter((m) => m.type === "Photo"),
      video: media.filter((m) => m.type === "Video"),
    }),
    [media]
  );

  const handleLogout = () => instance.logoutRedirect();

  const radioPresets = [
    { name: "BBC Radio 1", url: "https://stream.live.vc.bbcmedia.co.uk/bbc_radio_one" },
    { name: "BBC Radio 2", url: "https://stream.live.vc.bbcmedia.co.uk/bbc_radio_two" },
    { name: "BBC Radio 3", url: "https://stream.live.vc.bbcmedia.co.uk/bbc_radio_three" },
    { name: "BBC Radio 4", url: "https://stream.live.vc.bbcmedia.co.uk/bbc_radio_fourfm" },
    { name: "BBC Radio 5 Live", url: "https://stream.live.vc.bbcmedia.co.uk/bbc_radio_five_live_online_nonuk" },
    { name: "BBC Radio 6 Music", url: "https://stream.live.vc.bbcmedia.co.uk/bbc_6music" },
    { name: "BBC Radio 7", url: "https://stream.live.vc.bbcmedia.co.uk/bbc_radio_four_extra" },
    { name: "BBC Radio Bristol", url: "https://stream.live.vc.bbcmedia.co.uk/bbc_radio_bristol" },
  ];

  const seasonalOptions = ["", "Christmas", "Easter", "Diwali", "Eid", "Hanukkah", "Remembrance", "Summer", "Winter"];

  return (
    <div className="page">
      <header className="nav">
        <div className="brand">
          <img src="/bch-logo.svg" alt="Bristol Care Homes" className="brand-logo" />
          <div className="brand-text">
            <span className="brand-line">Press &amp; Play</span>
            <span className="brand-subline">Media Button</span>
          </div>
        </div>
        <div className="nav-actions">
          <div className="pill">
            <span className="status-dot" />
            {accountName}
          </div>
          <button className="btn ghost" onClick={handleLogout}>
            Sign out
          </button>
        </div>
      </header>

      <section className="hero">
        <div className="hero-left">
          <div className="hero-eyebrow">Bristol Care Homes</div>
          <h1 className="hero-title">Design soothing playlists for every room.</h1>
          <p className="hero-copy">
            Upload photos or videos, arrange them into playlists, and assign them to
            any Media Button Pi in seconds. Secure sign-in via your home tenant keeps
            residents' memories private and staff workflows simple.
          </p>
          <div className="pill brand-pill">Providing top quality, best value, holistic care.</div>
          <div className="nav-actions">
            <div className="badge">
              <span className="status-dot" />
              Home tenant: {appConfig.auth.tenantId || "Configure tenant ID"}
            </div>
          </div>
        </div>

        <div className="hero-right">
          <div className="logo-card glass">
            <img src="/press-play-hero.svg" alt="Press & Play Media Button" className="hero-logo" />
            <p className="hero-mini">
              Signed in as {accountName}. Press &amp; Play keeps playlists familiar, safe, and easy
              for every resident.
            </p>
          </div>
        </div>
      </section>

      <main className="content">
        {error && (
          <div className="card" style={{ borderColor: "rgba(217,72,15,0.3)" }}>
            <strong style={{ color: "var(--warning)" }}>Heads up: </strong>
            <span className="muted">{error}</span>
          </div>
        )}
        {saveMessage && (
          <div className="card glass">
            <strong style={{ color: "var(--brand-strong)" }}>Saved: </strong>
            <span className="muted">{saveMessage}</span>
          </div>
        )}

        <div className="grid c2">
          <StatCard
            title="Photos"
            value={loading ? "-" : `${mediaByType.photo.length}`}
            hint="Gentle visuals for residents."
          />
          <StatCard
            title="Videos"
            value={loading ? "-" : `${mediaByType.video.length}`}
            hint="Stories that feel familiar."
          />
        </div>

        <div className="card glass">
          <div className="card-header">
            <p className="card-title">Resident playlist</p>
            <span className="tag">Load current playlist first</span>
          </div>
          <div className="form-row">
            <select className="select" value={residentQuery} onChange={(e) => setResidentQuery(e.target.value)}>
              <option value="">Select resident...</option>
              {residentList.map((r) => (
                <option key={r} value={r}>
                  {r}
                </option>
              ))}
            </select>
            <div className="nav-actions" style={{ gap: 8 }}>
              <button className="btn ghost" type="button" disabled={loadingResident} onClick={loadResidentManual}>
                {loadingResident ? "Loading..." : "Load current playlist"}
              </button>
            </div>
          </div>
        </div>

        <div className="split">
          <div className="card glass">
            <div className="card-header">
              <p className="card-title">Upload media</p>
              <span className="tag">Direct-to-blob SAS</span>
            </div>
            <form className="grid" onSubmit={onUploadMedia}>
              <div className="form-row">
                <label className="grid">
                  <span className="muted">Choose file</span>
                  <input
                    type="file"
                    className="input"
                    onChange={(e) => setSelectedFile(e.target.files?.[0] ?? null)}
                    accept="image/*,video/*"
                  />
                </label>
                <label className="grid">
                  <span className="muted">Type</span>
                  <select
                    className="select"
                    value={selectedMediaType}
                    onChange={(e) => setSelectedMediaType(e.target.value as MediaType)}
                  >
                    <option value="Photo">Photo</option>
                    <option value="Video">Video</option>
                  </select>
                </label>
              </div>
              <div className="form-row">
                <label className="grid">
                  <span className="muted">Display name (optional)</span>
                  <input
                    className="input"
                    value={mediaName}
                    onChange={(e) => setMediaName(e.target.value)}
                    placeholder="Sunset walk in Clifton"
                  />
                </label>
                <label className="grid">
                  <span className="muted">Duration seconds (optional for videos)</span>
                  <input
                    className="input"
                    type="number"
                    value={duration ?? ""}
                    onChange={(e) =>
                      setDuration(e.target.value ? Number(e.target.value) : undefined)
                    }
                    min={0}
                  />
                </label>
              </div>
              <button className="btn primary" type="submit" disabled={uploading}>
                {uploading ? "Uploading." : "Upload & register"}
              </button>
            </form>
          </div>

          <div className="card glass">
            <div className="card-header">
              <p className="card-title">Radio favourites</p>
              <span className="tag">BBC stations</span>
            </div>
            <div className="list" style={{ gap: 6 }}>
              {radioPresets.map((r) => {
                const checked = radioFavorites.includes(r.url);
                const disabled = !checked && radioFavorites.length >= 3;
                return (
                  <label className="row" key={r.url}>
                    <div style={{ fontWeight: 700 }}>{r.name}</div>
                    <input
                      type="checkbox"
                      checked={checked}
                      disabled={disabled}
                      onChange={(e) => {
                        setRadioFavorites((prev) =>
                          e.target.checked ? [...prev, r.url] : prev.filter((u) => u !== r.url)
                        );
                      }}
                    />
                  </label>
                );
              })}
            </div>
          </div>
        </div>

        <div className="split">
          <div className="card glass">
            <div className="card-header">
              <p className="card-title">Seasonal influence</p>
              <span className="tag">Optional theme</span>
            </div>
            <select className="select" value={seasonalTheme} onChange={(e) => setSeasonalTheme(e.target.value)}>
              {seasonalOptions.map((s) => (
                <option key={s || "none"} value={s}>
                  {s || "No seasonal focus"}
                </option>
              ))}
            </select>
          </div>

          <div className="card glass">
            <div className="card-header">
              <p className="card-title">Add stream/media URLs</p>
              <span className="tag">Playlists & radio</span>
            </div>
            <div className="grid" style={{ gap: 8 }}>
              <div className="form-row">
                <input
                  className="input"
                  placeholder="https://example.com/stream.mp3"
                  value={urlInput}
                  onChange={(e) => setUrlInput(e.target.value)}
                />
                <button className="btn ghost" type="button" onClick={loadResidentManual} disabled={loadingResident}>
                  Reload playlist
                </button>
                <button className="btn ghost" type="button" onClick={() => setUrlInput("")} disabled={!urlInput.trim()}>
                  Clear
                </button>
              </div>
              <button className="btn primary" type="button" onClick={() => {
                if (!urlInput.trim()) return;
                const updated = applyOptionsToManual([...manualText.split(/\r?\n/).filter(Boolean), urlInput.trim()]);
                setManualText(updated.join("\n"));
                setPlaylistUrls((prev) => (prev.includes(urlInput.trim()) ? prev : [...prev, urlInput.trim()]));
                saveResidentManual(updated);
                setUrlInput("");
              }}>
                Add URL to playlist
              </button>
              {!!playlistUrls.length && (
                <div className="list">
                  {playlistUrls.map((u) => (
                    <div className="row" key={u}>
                      <span style={{ wordBreak: "break-all" }}>{u}</span>
                      <button
                        className="btn ghost"
                        type="button"
                        onClick={() => {
                          setPlaylistUrls((prev) => prev.filter((x) => x !== u));
                          const updated = manualText
                            .split(/\r?\n/)
                            .map((l) => l.trim())
                            .filter(Boolean)
                            .filter((line) => line !== u);
                          setManualText(updated.join("\n"));
                          saveResidentManual(updated);
                        }}
                      >
                        Remove
                      </button>
                    </div>
                  ))}
                </div>
              )}
            </div>
          </div>
        </div>

        <div className="card glass">
          <div className="card-header">
            <p className="card-title">Manual playlist</p>
            <span className="muted">
              Auto-saved when options change. Includes uploads by name; device receives blob URLs.
            </span>
          </div>
          <textarea
            className="textarea"
            style={{ minHeight: 200 }}
            value={manualText}
            onChange={(e) => setManualText(e.target.value)}
            placeholder="Enter titles, URLs or radio: prefixed stations"
          />
          <div className="nav-actions" style={{ justifyContent: "space-between", width: "100%" }}>
            <div className="muted">
              {manualMeta.updatedAt && (
                <span>
                  Last saved {formatDate(manualMeta.updatedAt)} {manualMeta.updatedBy ? `by ${manualMeta.updatedBy}` : ""}
                </span>
              )}
            </div>
            <div className="nav-actions" style={{ gap: 8 }}>
              <button className="btn ghost" type="button" disabled={loadingResident} onClick={loadResidentManual}>
                Reload
              </button>
              <button className="btn primary" type="button" disabled={savingManual} onClick={() => saveResidentManual()}>
                {savingManual ? "Saving." : "Save manual playlist"}
              </button>
            </div>
          </div>
        </div>

        <div className="card glass">
          <div className="card-header">
            <p className="card-title">Send playlist to device</p>
            <span className="tag">Press &amp; Play Media Button</span>
          </div>
          <form className="form-row" onSubmit={onAssignPlaylist}>
            <select
              className="select"
              value={residentQuery}
              onChange={(e) => setResidentQuery(e.target.value)}
            >
              <option value="">Select resident</option>
              {residentList.map((r) => (
                <option key={r} value={r}>
                  {r}
                </option>
              ))}
            </select>
            <button className="btn primary" type="submit" disabled={assigning || !playlistId}>
              {assigning ? "Sending." : "Send playlist to Press & Play"}
            </button>
          </form>
        </div>

        <div className="grid c2">
          <div className="card glass">
            <div className="card-header">
              <p className="card-title">Playlists</p>
              <span className="muted">{playlists.length} total</span>
            </div>
            <div className="list">
              {playlists.map((p) => (
                <div className="row" key={p.id}>
                  <div>
                    <div style={{ fontWeight: 800 }}>{p.name}</div>
                    <div className="muted" style={{ fontSize: 13 }}>
                      {p.items.length} item(s)
                    </div>
                  </div>
                  <div className="tag">{p.id.slice(0, 8)}.</div>
                </div>
              ))}
            </div>
          </div>

          <div className="card glass">
            <div className="card-header">
              <p className="card-title">Media</p>
              <span className="muted">{media.length} assets</span>
            </div>
            <div className="list" style={{ maxHeight: 420, overflow: "auto" }}>
              {media.map((m) => (
                <div className="row" key={m.id}>
                  <div>
                    <div style={{ fontWeight: 700 }}>{m.name || m.blobPath}</div>
                    <div className="muted" style={{ fontSize: 12 }}>
                      {m.type} • {formatDate(m.uploadedAt)}
                    </div>
                  </div>
                  <span className="tag">{m.type}</span>
                </div>
              ))}
            </div>
          </div>
        </div>
      </main>
    </div>
  );
}
