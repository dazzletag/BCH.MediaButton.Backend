import { useCallback, useEffect, useMemo, useState } from "react";
import { useApiClient } from "../hooks/useApiClient";
import type { CareHome } from "../types";

/**
 * Searching a care home's own programme guide.
 *
 * The guide is uploaded by whichever device in that home has a TV aerial, so
 * it shows what can genuinely be received there rather than a national
 * listing. Results are grouped by serial: a soap runs five times a week, and
 * forty near-identical rows would bury everything else.
 */

type EpgEvent = {
  id: string;
  channelName: string;
  channelNumber?: number | null;
  title: string;
  subtitle?: string | null;
  startUtc: string;
  stopUtc: string;
  seriesCrid?: string | null;
  genre?: string | null;
  isHd: boolean;
  isSubtitled: boolean;
  isAudioDescribed: boolean;
};

type EpgGroup = {
  key: string;
  seriesCrid?: string | null;
  isSeries: boolean;
  title: string;
  channelName: string;
  channelNumber?: number | null;
  genre?: string | null;
  showings: number;
  nextStartUtc: string;
  events: EpgEvent[];
};

type SearchResponse = { careHomeId: string; count: number; results: EpgGroup[] };

type EpgStatus = {
  careHomeId: string;
  careHome: string;
  events: number;
  channels: number;
  earliest: string;
  latest: string;
  lastUpdated: string;
};

function when(iso: string) {
  const d = new Date(iso);
  const today = new Date();
  const sameDay = d.toDateString() === today.toDateString();
  const time = d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
  return sameDay ? `Today ${time}` : `${d.toLocaleDateString([], { weekday: "short", day: "numeric", month: "short" })} ${time}`;
}

export default function TvGuide() {
  const { call } = useApiClient();
  const [careHomes, setCareHomes] = useState<CareHome[]>([]);
  const [careHomeId, setCareHomeId] = useState("");
  const [status, setStatus] = useState<EpgStatus[]>([]);
  const [channels, setChannels] = useState<{ channelName: string; channelNumber?: number | null }[]>([]);
  const [q, setQ] = useState("");
  const [channel, setChannel] = useState("");
  const [results, setResults] = useState<EpgGroup[]>([]);
  const [expanded, setExpanded] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [searched, setSearched] = useState(false);

  useEffect(() => {
    call<CareHome[]>({ url: "/api/admin/care-homes", method: "GET" })
      .then((h) => setCareHomes(h || []))
      .catch(() => setCareHomes([]));
    call<EpgStatus[]>({ url: "/api/admin/epg/status", method: "GET" })
      .then((s) => setStatus(s || []))
      .catch(() => setStatus([]));
  }, [call]);

  // Only homes that actually have a guide can be searched, so default to one
  // that does rather than leaving the user on an empty selection.
  useEffect(() => {
    if (!careHomeId && status.length > 0) setCareHomeId(status[0].careHomeId);
  }, [status, careHomeId]);

  useEffect(() => {
    if (!careHomeId) return;
    call<{ channelName: string; channelNumber?: number | null }[]>({
      url: `/api/admin/epg/channels?careHomeId=${encodeURIComponent(careHomeId)}`,
      method: "GET",
    })
      .then((c) => setChannels(c || []))
      .catch(() => setChannels([]));
  }, [call, careHomeId]);

  const current = useMemo(
    () => status.find((s) => s.careHomeId === careHomeId),
    [status, careHomeId]
  );

  const search = useCallback(async () => {
    if (!careHomeId) {
      setError("Choose a care home first.");
      return;
    }
    setLoading(true);
    setError(null);
    try {
      const params = new URLSearchParams({ careHomeId, limit: "80" });
      if (q.trim()) params.set("q", q.trim());
      if (channel) params.set("channel", channel);
      const data = await call<SearchResponse>({
        url: `/api/admin/epg/search?${params.toString()}`,
        method: "GET",
      });
      setResults(data?.results || []);
      setSearched(true);
    } catch (err) {
      console.error(err);
      setError("Search failed.");
      setResults([]);
    } finally {
      setLoading(false);
    }
  }, [call, careHomeId, q, channel]);

  return (
    <main className="container">
      <section className="card">
        <h2>TV Guide</h2>
        <p className="muted">
          Search what a care home can actually receive on its own aerial, and find the
          programmes a resident always used to watch.
        </p>

        {status.length === 0 && (
          <p className="muted" style={{ marginTop: 12 }}>
            No guide has been uploaded yet. A device with a TV aerial in the home sends
            its listing up automatically once it is set up.
          </p>
        )}

        <div style={{ display: "flex", gap: 8, flexWrap: "wrap", alignItems: "flex-end", marginTop: 12 }}>
          <label style={{ display: "flex", flexDirection: "column", gap: 4 }}>
            <span className="muted" style={{ fontSize: 13 }}>Care home</span>
            <select value={careHomeId} onChange={(e) => setCareHomeId(e.target.value)}>
              <option value="">Select…</option>
              {careHomes.map((h) => {
                const s = status.find((x) => x.careHomeId === h.id);
                return (
                  <option key={h.id} value={h.id} disabled={!s}>
                    {h.name}{s ? ` — ${s.events} programmes` : " — no guide yet"}
                  </option>
                );
              })}
            </select>
          </label>

          <label style={{ display: "flex", flexDirection: "column", gap: 4, flex: "1 1 240px" }}>
            <span className="muted" style={{ fontSize: 13 }}>Programme</span>
            <input
              className="input"
              placeholder="e.g. Emmerdale, Countryfile, Songs of Praise"
              value={q}
              onChange={(e) => setQ(e.target.value)}
              onKeyDown={(e) => { if (e.key === "Enter") search(); }}
            />
          </label>

          <label style={{ display: "flex", flexDirection: "column", gap: 4 }}>
            <span className="muted" style={{ fontSize: 13 }}>Channel</span>
            <select value={channel} onChange={(e) => setChannel(e.target.value)}>
              <option value="">All channels</option>
              {channels.map((c) => (
                <option key={c.channelName} value={c.channelName}>
                  {c.channelNumber ? `${c.channelNumber} · ` : ""}{c.channelName}
                </option>
              ))}
            </select>
          </label>

          <button className="btn primary" onClick={search} disabled={loading || !careHomeId}>
            {loading ? "Searching…" : "Search"}
          </button>
        </div>

        {current && (
          <p className="muted" style={{ fontSize: 13, marginTop: 10 }}>
            {current.channels} channels · {current.events} programmes · guide updated{" "}
            {new Date(current.lastUpdated).toLocaleString()}
          </p>
        )}
        {error && <p style={{ color: "crimson", marginTop: 8 }}>{error}</p>}
      </section>

      {searched && (
        <section className="card">
          <h3>{results.length} result{results.length === 1 ? "" : "s"}</h3>
          {results.length === 0 && (
            <p className="muted">Nothing matching, in what is still to come. Past showings are not listed.</p>
          )}

          <div style={{ display: "flex", flexDirection: "column", gap: 8, marginTop: 8 }}>
            {results.map((g) => (
              <div key={g.key} className="card" style={{ padding: 12 }}>
                <div style={{ display: "flex", justifyContent: "space-between", gap: 12, alignItems: "baseline", flexWrap: "wrap" }}>
                  <div>
                    <strong>{g.title}</strong>{" "}
                    {g.isSeries && (
                      <span className="badge" title="Broadcast as a series, so every episode can be followed">
                        series · {g.showings} showing{g.showings === 1 ? "" : "s"}
                      </span>
                    )}
                    <div className="muted" style={{ fontSize: 13 }}>
                      {g.channelNumber ? `${g.channelNumber} · ` : ""}{g.channelName}
                      {" · next "}{when(g.nextStartUtc)}
                    </div>
                  </div>
                  <button
                    className="btn ghost"
                    onClick={() => setExpanded(expanded === g.key ? null : g.key)}
                  >
                    {expanded === g.key ? "Hide showings" : "Showings"}
                  </button>
                </div>

                {expanded === g.key && (
                  <ul style={{ marginTop: 10, marginBottom: 0, paddingLeft: 18 }}>
                    {g.events.map((e) => (
                      <li key={e.id} className="muted" style={{ fontSize: 13 }}>
                        {when(e.startUtc)} — {e.subtitle || e.title}
                        {e.isSubtitled ? " · subtitled" : ""}
                        {e.isAudioDescribed ? " · audio described" : ""}
                      </li>
                    ))}
                  </ul>
                )}
              </div>
            ))}
          </div>
        </section>
      )}
    </main>
  );
}
