import { useCallback, useEffect, useMemo, useState } from "react";
import { useIsAuthenticated, useMsal } from "@azure/msal-react";
import { useApiClient } from "../hooks/useApiClient";
import { appConfig } from "../config";
import type { AiPlaylistEnvelope, ManualPlaylistResponse, MediaItem, MediaType, Playlist, ResidentList } from "../types";

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

type PlaylistCreateRequest = {
  name: string;
  items: {
    mediaId: string;
    order: number;
    durationSeconds?: number | null;
  }[];
};

function formatDate(dateString?: string) {
  if (!dateString) return "—";
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
  const [creating, setCreating] = useState(false);
  const [assigning, setAssigning] = useState(false);

  const [selectedMediaType, setSelectedMediaType] = useState<MediaType>("Photo");
  const [selectedFile, setSelectedFile] = useState<File | null>(null);
  const [mediaName, setMediaName] = useState("");
  const [duration, setDuration] = useState<number | undefined>(undefined);

  const [playlistName, setPlaylistName] = useState("");
  const [selectedMediaIds, setSelectedMediaIds] = useState<string[]>([]);

  const [deviceId, setDeviceId] = useState("");
  const [playlistId, setPlaylistId] = useState("");

  const accountName = useMemo(() => accounts[0]?.name ?? "Signed-in user", [accounts]);

  // Resident AI/manual editing
  const [residentQuery, setResidentQuery] = useState("");
  const [aiSuggestion, setAiSuggestion] = useState<string[]>([]);
  const [manualText, setManualText] = useState("");
  const [manualMeta, setManualMeta] = useState<{ updatedAt?: string | null; updatedBy?: string | null }>({});
  const [loadingResident, setLoadingResident] = useState(false);
  const [savingManual, setSavingManual] = useState(false);
  const [residentList, setResidentList] = useState<string[]>([]);

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

  const loadResidentAi = useCallback(async () => {
    if (!residentQuery.trim()) {
      setError("Enter a resident name to load the AI playlist.");
      return;
    }
    setLoadingResident(true);
    setError(null);
    try {
      const data = await call<AiPlaylistEnvelope>({
        url: `/api/admin/residents/${encodeURIComponent(residentQuery.trim())}/ai-playlist`,
        method: "GET",
      });
      const suggestion = Array.isArray(data?.payload?.playlist) ? (data.payload.playlist as string[]) : [];
      setAiSuggestion(suggestion);
      if (suggestion.length) {
        setManualText(suggestion.join("\n"));
      }
    } catch (err) {
      console.error(err);
      setAiSuggestion([]);
      setError("Could not load AI playlist for that resident.");
    } finally {
      setLoadingResident(false);
    }
  }, [call, residentQuery]);

  const loadResidentManual = useCallback(async () => {
    if (!residentQuery.trim()) {
      setError("Enter a resident name to load manual playlist.");
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
      setManualText(items.join("\n"));
      setManualMeta({ updatedAt: data?.updatedAtUtc, updatedBy: data?.updatedBy });
    } catch (err) {
      console.error(err);
      setManualText("");
      setManualMeta({});
      setError("Could not load manual playlist for that resident.");
    } finally {
      setLoadingResident(false);
    }
  }, [call, residentQuery]);

  const saveResidentManual = useCallback(async () => {
    if (!residentQuery.trim()) {
      setError("Resident name is required.");
      return;
    }
    const items = manualText
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
    } catch (err) {
      console.error(err);
      setError("Failed to save manual playlist.");
    } finally {
      setSavingManual(false);
    }
  }, [accountName, call, manualText, residentQuery]);

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
        const uploadBody: UploadRequest = {
          fileName: selectedFile.name,
          type: selectedMediaType,
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
          type: selectedMediaType,
          name: mediaName || selectedFile.name,
          contentType: selectedFile.type || null,
          durationSeconds: duration || null,
        };

        await call({
          url: "/api/admin/media",
          method: "POST",
          data: registerBody,
        });

        setSelectedFile(null);
        setMediaName("");
        setDuration(undefined);
        await loadData();
      } catch (err) {
        console.error(err);
        setError("Upload failed. Check storage settings and try again.");
      } finally {
        setUploading(false);
      }
    },
    [call, duration, loadData, mediaName, selectedFile, selectedMediaType]
  );

  const onCreatePlaylist = useCallback(
    async (evt: React.FormEvent) => {
      evt.preventDefault();
      if (!playlistName.trim()) {
        setError("Playlist name is required.");
        return;
      }
      if (selectedMediaIds.length === 0) {
        setError("Select at least one media item.");
        return;
      }
      setCreating(true);
      setError(null);
      try {
        const items = selectedMediaIds.map((id, index) => ({
          mediaId: id,
          order: index,
        }));
        const body: PlaylistCreateRequest = {
          name: playlistName.trim(),
          items,
        };
        await call({
          url: "/api/admin/playlists",
          method: "POST",
          data: body,
        });
        setPlaylistName("");
        setSelectedMediaIds([]);
        await loadData();
      } catch (err) {
        console.error(err);
        setError("Could not create playlist. Ensure your token has the Admin or Relative role.");
      } finally {
        setCreating(false);
      }
    },
    [call, loadData, playlistName, selectedMediaIds]
  );

  const onAssignPlaylist = useCallback(
    async (evt: React.FormEvent) => {
      evt.preventDefault();
      if (!deviceId.trim() || !playlistId.trim()) {
        setError("Device ID and playlist are required.");
        return;
      }
      setAssigning(true);
      setError(null);
      try {
        await call({
          url: `/api/admin/devices/${encodeURIComponent(deviceId.trim())}/playlist`,
          method: "PUT",
          data: playlistId,
        });
        setDeviceId("");
        setPlaylistId("");
      } catch (err) {
        console.error(err);
        setError("Assignment failed. Confirm the device ID and playlist ID.");
      } finally {
        setAssigning(false);
      }
    },
    [call, deviceId, playlistId]
  );

  const mediaByType = useMemo(
    () => ({
      photo: media.filter((m) => m.type === "Photo"),
      video: media.filter((m) => m.type === "Video"),
    }),
    [media]
  );

  const handleLogout = () => instance.logoutRedirect();

  return (
    <div className="page">
      <header className="nav">
        <div className="brand">
          <img src="/bch-logo.svg" alt="Bristol Care Homes" className="brand-logo" />
          <div className="brand-text">
            <span className="brand-line">Bristol Care Homes</span>
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
            <img src="/bch-logo.svg" alt="Bristol Care Homes logo" className="hero-logo" />
            <p className="hero-mini">
              Signed in as {accountName}. Each colour tile is a reminder that every playlist should
              feel personal, safe, and familiar.
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

        <div className="grid c3">
          <StatCard
            title="Playlists ready"
            value={loading ? "—" : `${playlists.length}`}
            hint="Curated sets you can assign to any device."
          />
          <StatCard
            title="Photos"
            value={loading ? "—" : `${mediaByType.photo.length}`}
            hint="Gentle visuals for residents."
          />
          <StatCard
            title="Videos"
            value={loading ? "—" : `${mediaByType.video.length}`}
            hint="Stories that feel familiar."
          />
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
                {uploading ? "Uploading…" : "Upload & register"}
              </button>
            </form>
          </div>

          <div className="card glass">
            <div className="card-header">
              <p className="card-title">Create playlist</p>
              <span className="tag">Drag-free, quick stack</span>
            </div>
            <form className="grid" onSubmit={onCreatePlaylist}>
              <label className="grid">
                <span className="muted">Playlist name</span>
                <input
                  className="input"
                  value={playlistName}
                  onChange={(e) => setPlaylistName(e.target.value)}
                  placeholder="Evening calm - Willow House"
                />
              </label>
              <label className="grid">
                <span className="muted">Pick media (order follows selection)</span>
                <div className="list" style={{ maxHeight: 200, overflow: "auto" }}>
                  {media.map((m) => {
                    const checked = selectedMediaIds.includes(m.id);
                    return (
                      <label key={m.id} className="row">
                        <div>
                          <div style={{ fontWeight: 700 }}>{m.name || "Untitled"}</div>
                          <div className="muted" style={{ fontSize: 12 }}>
                            {m.type} • {formatDate(m.uploadedAt)}
                          </div>
                        </div>
                        <input
                          type="checkbox"
                          checked={checked}
                          onChange={(e) => {
                            setSelectedMediaIds((prev) =>
                              e.target.checked ? [...prev, m.id] : prev.filter((id) => id !== m.id)
                            );
                          }}
                        />
                      </label>
                    );
                  })}
                </div>
              </label>
              <button className="btn primary" type="submit" disabled={creating}>
                {creating ? "Saving…" : "Create playlist"}
              </button>
            </form>
          </div>
        </div>

        <div className="card glass">
          <div className="card-header">
            <p className="card-title">Assign playlist to device</p>
            <span className="tag">Pi endpoints</span>
          </div>
          <form className="form-row" onSubmit={onAssignPlaylist}>
            <input
              className="input"
              placeholder="Device ID (e.g., beech_pi_01)"
              value={deviceId}
              onChange={(e) => setDeviceId(e.target.value)}
            />
            <select
              className="select"
              value={playlistId}
              onChange={(e) => setPlaylistId(e.target.value)}
            >
              <option value="">Select playlist</option>
              {playlists.map((p) => (
                <option key={p.id} value={p.id}>
                  {p.name}
                </option>
              ))}
            </select>
            <button className="btn primary" type="submit" disabled={assigning}>
              {assigning ? "Assigning…" : "Assign"}
            </button>
          </form>
        </div>

        <div className="card glass">
          <div className="card-header">
            <p className="card-title">AI → Manual playlist</p>
            <span className="tag">Resident-specific</span>
          </div>
          <div className="grid" style={{ gap: 12 }}>
            <div className="form-row">
              <select
                className="select"
                value={residentQuery}
                onChange={(e) => setResidentQuery(e.target.value)}
              >
                <option value="">Select resident...</option>
                {residentList.map((r) => (
                  <option key={r} value={r}>
                    {r}
                  </option>
                ))}
              </select>
              <div className="nav-actions" style={{ gap: 8 }}>
                <button className="btn ghost" type="button" disabled={loadingResident} onClick={loadResidentAi}>
                  {loadingResident ? "Loading..." : "Load AI suggestion"}
                </button>
                <button className="btn ghost" type="button" disabled={loadingResident} onClick={loadResidentManual}>
                  {loadingResident ? "Loading..." : "Load manual"}
                </button>
              </div>
            </div>
            <div className="grid">
              <span className="muted">Manual playlist (one line per item). Save to send to Pi as manual override.</span>
              <textarea
                className="textarea"
                style={{ minHeight: 160 }}
                value={manualText}
                onChange={(e) => setManualText(e.target.value)}
                placeholder="Enter titles or queries per line..."
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
                  <button
                    className="btn ghost"
                    type="button"
                    disabled={!aiSuggestion.length || !residentQuery}
                    onClick={() => setManualText(aiSuggestion.join("\n"))}
                  >
                    Use AI suggestion
                  </button>
                  <button className="btn primary" type="button" disabled={savingManual} onClick={saveResidentManual}>
                    {savingManual ? "Saving…" : "Save manual override"}
                  </button>
                </div>
              </div>
            </div>
          </div>
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
                  <div className="tag">{p.id.slice(0, 8)}…</div>
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
