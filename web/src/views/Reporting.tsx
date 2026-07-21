import { useCallback, useEffect, useMemo, useState } from "react";
import { useApiClient } from "../hooks/useApiClient";
import type {
  CareHome,
  ReportResident,
  ReportSummary,
  ReportVideo,
} from "../types";

/* ------------------------------ formatting ------------------------------ */

function fmtInt(n?: number | null): string {
  if (n == null) return "0";
  return new Intl.NumberFormat("en-GB").format(Math.round(n));
}

function fmtBytes(bytes?: number | null): string {
  if (!bytes || bytes <= 0) return "0 MB";
  const mb = bytes / 1024 / 1024;
  if (mb < 1) return `${(bytes / 1024).toFixed(0)} KB`;
  if (mb < 1024) return `${mb.toFixed(0)} MB`;
  return `${(mb / 1024).toFixed(2)} GB`;
}

function fmtWatch(seconds?: number | null): string {
  const s = Math.max(0, Math.floor(seconds ?? 0));
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  if (h >= 100) return `${h}h`;
  if (h > 0) return `${h}h ${m}m`;
  if (m > 0) return `${m}m`;
  return `${s}s`;
}

function fmtRelative(iso?: string | null): string {
  if (!iso) return "Never";
  const ms = Date.now() - new Date(iso).getTime();
  if (Number.isNaN(ms)) return "—";
  const s = Math.floor(ms / 1000);
  if (s < 60) return "just now";
  const m = Math.floor(s / 60);
  if (m < 60) return `${m}m ago`;
  const h = Math.floor(m / 60);
  if (h < 48) return `${h}h ago`;
  const d = Math.floor(h / 24);
  if (d < 30) return `${d}d ago`;
  return `${Math.floor(d / 30)}mo ago`;
}

const SOURCE_LABELS: Record<string, string> = {
  yt: "YouTube",
  fam: "Family upload",
  azure: "Portal upload",
};
function sourceLabel(s: string): string {
  return SOURCE_LABELS[s.toLowerCase()] ?? s;
}

const SERIES = ["#0d8ab2", "#a3457d", "#6d7b33", "#4b2f58", "#c27a1c", "#0b6f93", "#b0559b", "#8a9a45"];

/* ------------------------------ mini charts ----------------------------- */

function BarRow({
  label,
  value,
  max,
  color,
  caption,
}: {
  label: string;
  value: number;
  max: number;
  color: string;
  caption?: string;
}) {
  const pct = max > 0 ? Math.max(2, (value / max) * 100) : 0;
  return (
    <div style={{ display: "grid", gap: 4 }}>
      <div style={{ display: "flex", justifyContent: "space-between", gap: 10, fontSize: 13 }}>
        <span style={{ fontWeight: 600, overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}>
          {label}
        </span>
        <span className="muted" style={{ whiteSpace: "nowrap" }}>{caption}</span>
      </div>
      <div style={{ height: 10, background: "rgba(16,35,56,0.06)", borderRadius: 999, overflow: "hidden" }}>
        <div style={{ width: `${pct}%`, height: "100%", background: color, borderRadius: 999, transition: "width .4s ease" }} />
      </div>
    </div>
  );
}

function Donut({
  segments,
  centerValue,
  centerLabel,
}: {
  segments: { label: string; value: number; color: string }[];
  centerValue: string;
  centerLabel: string;
}) {
  const total = segments.reduce((a, s) => a + s.value, 0);
  const r = 54;
  const c = 2 * Math.PI * r;
  let offset = 0;
  return (
    <div style={{ display: "flex", alignItems: "center", gap: 18, flexWrap: "wrap" }}>
      <svg width={140} height={140} viewBox="0 0 140 140" style={{ flexShrink: 0 }}>
        <g transform="rotate(-90 70 70)">
          <circle cx={70} cy={70} r={r} fill="none" stroke="rgba(16,35,56,0.06)" strokeWidth={18} />
          {total > 0 &&
            segments.map((s, i) => {
              const len = (s.value / total) * c;
              const el = (
                <circle
                  key={i}
                  cx={70}
                  cy={70}
                  r={r}
                  fill="none"
                  stroke={s.color}
                  strokeWidth={18}
                  strokeDasharray={`${len} ${c - len}`}
                  strokeDashoffset={-offset}
                  strokeLinecap="butt"
                />
              );
              offset += len;
              return el;
            })}
        </g>
        <text x={70} y={66} textAnchor="middle" fontSize={24} fontWeight={800} fill="#0b6f93">
          {centerValue}
        </text>
        <text x={70} y={86} textAnchor="middle" fontSize={11} fill="#5d5863">
          {centerLabel}
        </text>
      </svg>
      <div style={{ display: "grid", gap: 8, minWidth: 130 }}>
        {segments.map((s, i) => (
          <div key={i} style={{ display: "flex", alignItems: "center", gap: 8, fontSize: 13 }}>
            <span style={{ width: 12, height: 12, borderRadius: 4, background: s.color, flexShrink: 0 }} />
            <span style={{ fontWeight: 600 }}>{s.label}</span>
            <span className="muted" style={{ marginLeft: "auto" }}>
              {fmtInt(s.value)}
              {total > 0 ? ` · ${Math.round((s.value / total) * 100)}%` : ""}
            </span>
          </div>
        ))}
        {total === 0 && <span className="muted">No data in range</span>}
      </div>
    </div>
  );
}

function AreaChart({ points }: { points: { date: string; added: number }[] }) {
  const W = 720;
  const H = 180;
  const pad = { l: 28, r: 12, t: 14, b: 22 };
  if (points.length === 0) {
    return <div className="muted" style={{ padding: "24px 0" }}>No items added in this period.</div>;
  }
  const maxV = Math.max(1, ...points.map((p) => p.added));
  const iw = W - pad.l - pad.r;
  const ih = H - pad.t - pad.b;
  const n = points.length;
  const x = (i: number) => pad.l + (n === 1 ? iw / 2 : (i / (n - 1)) * iw);
  const y = (v: number) => pad.t + ih - (v / maxV) * ih;

  const line = points.map((p, i) => `${i === 0 ? "M" : "L"} ${x(i).toFixed(1)} ${y(p.added).toFixed(1)}`).join(" ");
  const area = `${line} L ${x(n - 1).toFixed(1)} ${(pad.t + ih).toFixed(1)} L ${x(0).toFixed(1)} ${(pad.t + ih).toFixed(1)} Z`;
  const gridVals = [0, Math.ceil(maxV / 2), maxV];

  return (
    <div style={{ overflowX: "auto" }}>
      <svg width="100%" viewBox={`0 0 ${W} ${H}`} preserveAspectRatio="none" style={{ minWidth: 420, maxWidth: "100%" }}>
        <defs>
          <linearGradient id="growthFill" x1="0" y1="0" x2="0" y2="1">
            <stop offset="0%" stopColor="rgba(13,138,178,0.28)" />
            <stop offset="100%" stopColor="rgba(13,138,178,0.02)" />
          </linearGradient>
        </defs>
        {gridVals.map((v, i) => (
          <g key={i}>
            <line x1={pad.l} x2={W - pad.r} y1={y(v)} y2={y(v)} stroke="rgba(16,35,56,0.07)" strokeWidth={1} />
            <text x={4} y={y(v) + 4} fontSize={10} fill="#5d5863">{v}</text>
          </g>
        ))}
        <path d={area} fill="url(#growthFill)" />
        <path d={line} fill="none" stroke="#0d8ab2" strokeWidth={2.4} strokeLinejoin="round" />
        {n <= 32 &&
          points.map((p, i) => <circle key={i} cx={x(i)} cy={y(p.added)} r={2.6} fill="#0b6f93" />)}
        <text x={pad.l} y={H - 6} fontSize={10} fill="#5d5863">{points[0].date}</text>
        <text x={W - pad.r} y={H - 6} fontSize={10} fill="#5d5863" textAnchor="end">{points[n - 1].date}</text>
      </svg>
    </div>
  );
}

/* --------------------------------- KPI ---------------------------------- */

function Kpi({ label, value, sub, accent }: { label: string; value: string; sub?: string; accent?: string }) {
  return (
    <div className="card" style={{ padding: "16px 18px", display: "grid", gap: 2 }}>
      <span className="muted" style={{ fontSize: 12, textTransform: "uppercase", letterSpacing: 1 }}>{label}</span>
      <span style={{ fontSize: 30, fontWeight: 800, color: accent ?? "var(--brand-strong)", lineHeight: 1.1 }}>
        {value}
      </span>
      {sub && <span className="muted" style={{ fontSize: 12 }}>{sub}</span>}
    </div>
  );
}

/* ------------------------------ date presets ---------------------------- */

type Preset = "7" | "30" | "90" | "all";
function rangeFor(p: Preset): { from?: string; to?: string } {
  if (p === "all") return {};
  const to = new Date();
  const from = new Date();
  from.setDate(from.getDate() - Number(p));
  return { from: from.toISOString(), to: to.toISOString() };
}

/* --------------------------------- view --------------------------------- */

type ResidentSortKey = "plays" | "watchSeconds" | "videos" | "lastActive";

export default function Reporting({ isAdmin = false }: { isAdmin?: boolean }) {
  const { call } = useApiClient();

  const [summary, setSummary] = useState<ReportSummary | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const [careHomes, setCareHomes] = useState<CareHome[]>([]);
  const [residents, setResidents] = useState<string[]>([]);

  // filters
  const [preset, setPreset] = useState<Preset>("30");
  const [careHomeId, setCareHomeId] = useState("");
  const [resident, setResident] = useState("");
  const [source, setSource] = useState("");
  const [residentSort, setResidentSort] = useState<ResidentSortKey>("plays");

  useEffect(() => {
    if (!isAdmin) return;
    call<CareHome[]>({ url: "/api/admin/care-homes", method: "GET" })
      .then((h) => setCareHomes(h || []))
      .catch(() => setCareHomes([]));
  }, [call, isAdmin]);

  useEffect(() => {
    call<string[]>({ url: "/api/admin/residents", method: "GET" })
      .then((r) => setResidents(r || []))
      .catch(() => setResidents([]));
  }, [call]);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const { from, to } = rangeFor(preset);
      const params = new URLSearchParams();
      if (careHomeId) params.set("careHomeId", careHomeId);
      if (resident) params.set("resident", resident);
      if (source) params.set("source", source);
      if (from) params.set("from", from);
      if (to) params.set("to", to);
      const data = await call<ReportSummary>({
        url: `/api/admin/reporting/summary?${params.toString()}`,
        method: "GET",
      });
      setSummary(data);
    } catch (err) {
      console.error(err);
      setError("Could not load reporting data. Check your permissions and try again.");
    } finally {
      setLoading(false);
    }
  }, [call, preset, careHomeId, resident, source]);

  useEffect(() => {
    load();
  }, [load]);

  const presetLabel = useMemo(
    () => (preset === "all" ? "all time" : `last ${preset} days`),
    [preset]
  );

  const sortedResidents = useMemo(() => {
    if (!summary) return [];
    const rows = [...summary.residents];
    rows.sort((a, b) => {
      if (residentSort === "lastActive") {
        const av = a.lastActive ? new Date(a.lastActive).getTime() : 0;
        const bv = b.lastActive ? new Date(b.lastActive).getTime() : 0;
        return bv - av;
      }
      return (b[residentSort] as number) - (a[residentSort] as number);
    });
    return rows;
  }, [summary, residentSort]);

  const exportCsv = useCallback(() => {
    if (!summary) return;
    const header = ["Resident", "Care home", "Plays", "Watch time (min)", "Videos", "Library (MB)", "Devices", "Top theme", "Last active"];
    const lines = summary.residents.map((r) =>
      [
        r.resident,
        r.careHomeName ?? "",
        r.plays,
        Math.round(r.watchSeconds / 60),
        r.videos,
        Math.round(r.libraryBytes / 1024 / 1024),
        r.devices,
        r.topTerm ?? "",
        r.lastActive ?? "",
      ]
        .map((c) => `"${String(c).replace(/"/g, '""')}"`)
        .join(",")
    );
    const blob = new Blob([[header.join(","), ...lines].join("\n")], { type: "text/csv" });
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = `press-play-usage-${new Date().toISOString().slice(0, 10)}.csv`;
    a.click();
    URL.revokeObjectURL(url);
  }, [summary]);

  const k = summary?.kpis;
  const maxHomePlays = Math.max(1, ...(summary?.careHomes ?? []).map((h) => h.plays));
  const maxTermPlays = Math.max(1, ...(summary?.topTerms ?? []).map((t) => t.plays));
  const maxVideoPlays = Math.max(1, ...(summary?.topVideos ?? []).map((v) => v.plays));

  return (
    <main className="content" style={{ paddingTop: 24 }}>
      {/* header */}
      <div style={{ display: "flex", justifyContent: "space-between", alignItems: "flex-end", gap: 12, flexWrap: "wrap" }}>
        <div>
          <div className="hero-eyebrow">Bristol Care Homes</div>
          <h1 className="section-title" style={{ fontSize: 28, fontFamily: '"Playfair Display", serif', color: "var(--brand-strong)", margin: "4px 0 2px" }}>
            Engagement &amp; usage reporting
          </h1>
          <p className="muted" style={{ margin: 0 }}>
            Care-home-wide and per-resident insight from Media Button video plays · {presetLabel}
          </p>
        </div>
        <div className="nav-actions" style={{ gap: 8 }}>
          <button className="btn ghost" type="button" onClick={exportCsv} disabled={!summary}>
            Export CSV
          </button>
          <button className="btn primary" type="button" onClick={load} disabled={loading}>
            {loading ? "Refreshing…" : "Refresh"}
          </button>
        </div>
      </div>

      {/* filters */}
      <div className="card glass">
        <div className="form-row" style={{ alignItems: "end" }}>
          <label className="grid" style={{ gap: 4 }}>
            <span className="muted">Period</span>
            <div className="nav-actions" style={{ gap: 4 }}>
              {(["7", "30", "90", "all"] as Preset[]).map((p) => (
                <button
                  key={p}
                  type="button"
                  className={`btn ${preset === p ? "primary" : "ghost"}`}
                  style={{ padding: "8px 12px", borderRadius: 10 }}
                  onClick={() => setPreset(p)}
                >
                  {p === "all" ? "All" : `${p}d`}
                </button>
              ))}
            </div>
          </label>
          {isAdmin && (
            <label className="grid" style={{ gap: 4 }}>
              <span className="muted">Care home</span>
              <select className="select" value={careHomeId} onChange={(e) => setCareHomeId(e.target.value)}>
                <option value="">All care homes</option>
                {careHomes.map((h) => (
                  <option key={h.id} value={h.id}>{h.name}</option>
                ))}
              </select>
            </label>
          )}
          <label className="grid" style={{ gap: 4 }}>
            <span className="muted">Resident</span>
            <select className="select" value={resident} onChange={(e) => setResident(e.target.value)}>
              <option value="">All residents</option>
              {residents.map((r) => (
                <option key={r} value={r}>{r}</option>
              ))}
            </select>
          </label>
          <label className="grid" style={{ gap: 4 }}>
            <span className="muted">Source</span>
            <select className="select" value={source} onChange={(e) => setSource(e.target.value)}>
              <option value="">All sources</option>
              <option value="yt">YouTube</option>
              <option value="fam">Family upload</option>
              <option value="azure">Portal upload</option>
            </select>
          </label>
        </div>
      </div>

      {error && (
        <div className="card" style={{ borderColor: "rgba(194,122,28,0.35)" }}>
          <strong style={{ color: "var(--warning)" }}>Heads up: </strong>
          <span className="muted">{error}</span>
        </div>
      )}

      {loading && !summary ? (
        <div className="card glass"><span className="muted">Loading reporting data…</span></div>
      ) : !summary || (k && k.totalResidents === 0) ? (
        <div className="card glass">
          <p className="card-title">No usage data in scope yet</p>
          <p className="muted" style={{ margin: 0 }}>
            Metrics appear once assigned Media Buttons report their cached-video play counts. If devices are online
            but nothing shows here, widen the period to “All”.
          </p>
        </div>
      ) : (
        <>
          {/* KPI grid */}
          <div className="grid c3">
            <Kpi label="Total plays" value={fmtInt(k!.totalPlays)} sub={`${presetLabel} · ${fmtInt(k!.avgPlaysPerActiveResident)} avg / active resident`} />
            <Kpi label="Est. watch time" value={fmtWatch(k!.estimatedWatchSeconds)} sub="Plays × video length (approx.)" accent="var(--berry)" />
            <Kpi label="Active residents" value={`${fmtInt(k!.activeResidents)} / ${fmtInt(k!.totalResidents)}`} sub="Played ≥1 video this period" accent="var(--olive)" />
            <Kpi label="Videos in library" value={fmtInt(k!.uniqueVideos)} sub={`${fmtInt(k!.neverPlayedVideos)} never played · all-time`} />
            <Kpi label="Library storage" value={fmtBytes(k!.libraryBytes)} sub="Across all reporting devices" accent="var(--plum)" />
            <Kpi label="Devices online" value={`${fmtInt(k!.onlineDevices)} / ${fmtInt(k!.deviceCount)}`} sub={`${fmtInt(k!.activeDevices)} active in last 24h`} accent={k!.onlineDevices > 0 ? "var(--success)" : "var(--warning)"} />
          </div>

          {/* Care-home comparison */}
          {summary.careHomes.length > 1 && (
            <div className="card glass">
              <div className="card-header">
                <p className="card-title">Care home comparison</p>
                <span className="tag">Plays · {presetLabel}</span>
              </div>
              <div style={{ display: "grid", gap: 12 }}>
                {summary.careHomes.map((h, i) => (
                  <BarRow
                    key={h.careHomeId ?? "none"}
                    label={h.name}
                    value={h.plays}
                    max={maxHomePlays}
                    color={SERIES[i % SERIES.length]}
                    caption={`${fmtInt(h.plays)} plays · ${fmtWatch(h.watchSeconds)} · ${h.activeResidents}/${h.residents} active`}
                  />
                ))}
              </div>
            </div>
          )}

          {/* Library growth */}
          <div className="card glass">
            <div className="card-header">
              <p className="card-title">Library growth</p>
              <span className="tag">Videos added per day</span>
            </div>
            <AreaChart points={summary.libraryGrowth} />
          </div>

          {/* Split: source + recency donuts */}
          <div className="split">
            <div className="card glass">
              <div className="card-header">
                <p className="card-title">Where plays come from</p>
                <span className="tag">By source</span>
              </div>
              <Donut
                centerValue={fmtInt(k!.totalPlays)}
                centerLabel="plays"
                segments={summary.sourceSplit.map((s, i) => ({
                  label: sourceLabel(s.source),
                  value: s.plays,
                  color: SERIES[i % SERIES.length],
                }))}
              />
            </div>
            <div className="card glass">
              <div className="card-header">
                <p className="card-title">Content freshness</p>
                <span className="tag">By last played</span>
              </div>
              <Donut
                centerValue={fmtInt(k!.uniqueVideos)}
                centerLabel="videos"
                segments={[
                  { label: "Last 7 days", value: summary.playRecency.last7, color: "#6d7b33" },
                  { label: "Last 30 days", value: summary.playRecency.last30, color: "#0d8ab2" },
                  { label: "Older", value: summary.playRecency.older, color: "#c27a1c" },
                  { label: "Never played", value: summary.playRecency.never, color: "#b0451f" },
                ]}
              />
            </div>
          </div>

          {/* Top videos + top terms */}
          <div className="split">
            <div className="card glass">
              <div className="card-header">
                <p className="card-title">Most-played videos</p>
                <span className="tag">Top {Math.min(15, summary.topVideos.length)}</span>
              </div>
              {summary.topVideos.length === 0 ? (
                <p className="muted" style={{ margin: 0 }}>No plays recorded in this period.</p>
              ) : (
                <div style={{ display: "grid", gap: 12 }}>
                  {summary.topVideos.map((v: ReportVideo) => (
                    <BarRow
                      key={`${v.source}:${v.sourceId}`}
                      label={v.title}
                      value={v.plays}
                      max={maxVideoPlays}
                      color={SERIES[0]}
                      caption={`${fmtInt(v.plays)}× · ${v.resident}`}
                    />
                  ))}
                </div>
              )}
            </div>
            <div className="card glass">
              <div className="card-header">
                <p className="card-title">Popular themes</p>
                <span className="tag">Search terms</span>
              </div>
              {summary.topTerms.length === 0 ? (
                <p className="muted" style={{ margin: 0 }}>No themed plays in this period.</p>
              ) : (
                <div style={{ display: "grid", gap: 12 }}>
                  {summary.topTerms.map((t, i) => (
                    <BarRow
                      key={t.term}
                      label={t.term}
                      value={t.plays}
                      max={maxTermPlays}
                      color={SERIES[(i + 1) % SERIES.length]}
                      caption={`${fmtInt(t.plays)} plays · ${t.videos} video${t.videos === 1 ? "" : "s"}`}
                    />
                  ))}
                </div>
              )}
            </div>
          </div>

          {/* Per-resident table */}
          <div className="card glass">
            <div className="card-header">
              <p className="card-title">Per-resident engagement</p>
              <div className="nav-actions" style={{ gap: 6 }}>
                <span className="muted" style={{ fontSize: 12 }}>Sort by</span>
                <select
                  className="select"
                  style={{ width: "auto", padding: "6px 10px", fontSize: 13 }}
                  value={residentSort}
                  onChange={(e) => setResidentSort(e.target.value as ResidentSortKey)}
                >
                  <option value="plays">Plays</option>
                  <option value="watchSeconds">Watch time</option>
                  <option value="videos">Library size</option>
                  <option value="lastActive">Last active</option>
                </select>
              </div>
            </div>
            <div style={{ overflowX: "auto" }}>
              <table className="table" style={{ minWidth: 720 }}>
                <thead>
                  <tr>
                    <th>Resident</th>
                    {isAdmin && <th>Care home</th>}
                    <th style={{ textAlign: "right" }}>Plays</th>
                    <th style={{ textAlign: "right" }}>Watch time</th>
                    <th style={{ textAlign: "right" }}>Videos</th>
                    <th style={{ textAlign: "right" }}>Library</th>
                    <th>Top theme</th>
                    <th>Last active</th>
                  </tr>
                </thead>
                <tbody>
                  {sortedResidents.map((r: ReportResident) => {
                    const stale = !r.lastActive || Date.now() - new Date(r.lastActive).getTime() > 30 * 864e5;
                    return (
                      <tr key={r.resident}>
                        <td style={{ fontWeight: 700 }}>{r.resident}</td>
                        {isAdmin && <td className="muted">{r.careHomeName ?? "—"}</td>}
                        <td style={{ textAlign: "right", fontWeight: 700, color: r.plays > 0 ? "var(--brand-strong)" : "var(--muted)" }}>
                          {fmtInt(r.plays)}
                        </td>
                        <td style={{ textAlign: "right" }}>{fmtWatch(r.watchSeconds)}</td>
                        <td style={{ textAlign: "right" }}>{fmtInt(r.videos)}</td>
                        <td style={{ textAlign: "right" }} className="muted">{fmtBytes(r.libraryBytes)}</td>
                        <td className="muted" style={{ maxWidth: 180, overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}>
                          {r.topTerm ?? "—"}
                        </td>
                        <td style={{ color: stale ? "var(--warning)" : "var(--muted)" }}>{fmtRelative(r.lastActive)}</td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
          </div>

          <p className="muted" style={{ fontSize: 12, marginTop: -4 }}>
            Figures are derived from the video-play counters each Media Button reports in its cache snapshot
            (updated every few minutes). “Plays” count a video within the selected period when it was last played
            in that window; library totals are all-time. Radio, photo slideshows and raw button presses are not yet
            captured server-side. Generated {fmtRelative(summary.scope.generatedAtUtc)}.
          </p>
        </>
      )}
    </main>
  );
}
