import { useCallback, useEffect, useState } from "react";
import { useApiClient } from "../hooks/useApiClient";

/**
 * Generate and approve the picture on a resident's welcome screen.
 *
 * The approval step is the point of this panel, not a formality. Until
 * somebody chooses one, the device carries on drawing its plain card — so
 * nothing a model invented reaches a resident who may not be able to tell
 * anyone they find it upsetting.
 */

type WelcomeImage = {
  id: string;
  status: "Candidate" | "Approved" | "Rejected";
  subject: string | null;
  prompt: string | null;
  generatedAtUtc: string;
  approvedBy: string | null;
  url: string;
  urlExpiresAt: string;
};

type GenerateResponse = {
  items: WelcomeImage[];
  basis: string;
  prompt: string;
};

export function WelcomeImagePanel({ resident }: { resident: string }) {
  const { call } = useApiClient();
  const [items, setItems] = useState<WelcomeImage[]>([]);
  const [basis, setBasis] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [showPrompt, setShowPrompt] = useState(false);

  const name = resident.trim();

  const load = useCallback(async () => {
    if (!name) {
      setItems([]);
      return;
    }
    try {
      const r = await call<{ items: WelcomeImage[] }>({
        url: `/api/admin/residents/${encodeURIComponent(name)}/welcome-images`,
      });
      setItems(r?.items ?? []);
    } catch {
      // A resident with no pictures yet is the normal case, not a failure.
      setItems([]);
    }
  }, [call, name]);

  useEffect(() => {
    void load();
    setBasis(null);
    setError(null);
  }, [load]);

  const generate = async () => {
    if (!name) {
      setError("Choose a resident first.");
      return;
    }
    setBusy(true);
    setError(null);
    try {
      const r = await call<GenerateResponse>({
        url: `/api/admin/residents/${encodeURIComponent(name)}/welcome-image/generate?count=3`,
        method: "POST",
      });
      setItems(r?.items ?? []);
      setBasis(r?.basis ?? null);
      if (!r?.items?.length) setError("Nothing came back from the image flow.");
    } catch (err: unknown) {
      const status = (err as { response?: { status?: number } })?.response?.status;
      const detail = (err as { response?: { data?: string } })?.response?.data;
      if (status === 503) {
        setError(
          "No image flow is configured yet. Set WelcomeImageFlow:Url to a flow that takes " +
            "{ residentName, prompt, count } and returns { images: [ { base64 } ] }."
        );
      } else {
        setError(typeof detail === "string" && detail ? detail : "Could not generate a picture.");
      }
    } finally {
      setBusy(false);
    }
  };

  const decide = async (id: string, verdict: "approve" | "reject") => {
    setBusy(true);
    setError(null);
    try {
      await call({
        url: `/api/admin/residents/${encodeURIComponent(name)}/welcome-image/${id}/${verdict}`,
        method: "POST",
      });
      await load();
    } catch {
      setError(`Could not ${verdict} that picture.`);
    } finally {
      setBusy(false);
    }
  };

  const approved = items.find((i) => i.status === "Approved");
  const candidates = items.filter((i) => i.status === "Candidate");

  // Show the lettering the device will draw. Approving a picture without it
  // means approving something nobody has actually seen: the first attempt put
  // the name across a dog's face, and from a bare thumbnail that is invisible.
  const Preview = ({ img, border }: { img: WelcomeImage; border: string }) => (
    <div style={{ position: "relative", width: "100%", aspectRatio: "16 / 9",
                  borderRadius: 8, overflow: "hidden", border }}>
      <img
        src={img.url}
        alt={`Welcome screen for ${name}`}
        style={{ width: "100%", height: "100%", objectFit: "cover", display: "block" }}
      />
      <div
        style={{
          position: "absolute", inset: 0, display: "flex", flexDirection: "column",
          alignItems: "center", justifyContent: "flex-end", paddingBottom: "6%",
          background: "linear-gradient(to bottom, transparent 55%, rgba(0,0,0,0.78) 100%)",
          textAlign: "center",
        }}
      >
        <div style={{ color: "#fff", fontWeight: 800, lineHeight: 1.05,
                      fontSize: "clamp(14px, 7.5cqw, 46px)",
                      textShadow: "0 2px 6px rgba(0,0,0,0.9)" }}>
          {name || "Resident"} TV
        </div>
        <div style={{ color: "#ffd166", marginTop: "2%",
                      fontSize: "clamp(8px, 3cqw, 18px)",
                      textShadow: "0 2px 5px rgba(0,0,0,0.9)" }}>
          Curated by us just for you!
        </div>
      </div>
    </div>
  );

  return (
    <section style={{ marginTop: 24 }}>
      <h3 style={{ margin: "0 0 4px" }}>Welcome screen picture</h3>
      <p style={{ margin: "0 0 12px", opacity: 0.75, fontSize: 13, maxWidth: "60ch" }}>
        What {name || "the resident"} sees before anything plays. Until one is approved
        here, the device shows a plain card with their name on it.
      </p>

      <div style={{ display: "flex", gap: 8, alignItems: "center", flexWrap: "wrap" }}>
        <button onClick={generate} disabled={busy || !name}>
          {busy ? "Working…" : candidates.length ? "Generate three more" : "Generate pictures"}
        </button>
        {approved && (
          <span style={{ fontSize: 13, opacity: 0.75 }}>
            Approved{approved.approvedBy ? ` by ${approved.approvedBy}` : ""}
          </span>
        )}
      </div>

      {basis && (
        <p style={{ fontSize: 13, opacity: 0.8, marginTop: 10, maxWidth: "70ch" }}>{basis}</p>
      )}

      {error && (
        <p style={{ color: "#b00020", fontSize: 13, marginTop: 10, maxWidth: "70ch" }}>{error}</p>
      )}

      {approved && (
        <div style={{ marginTop: 16 }}>
          <div style={{ fontSize: 13, fontWeight: 600, marginBottom: 6 }}>
            Showing on the device now
          </div>
          <div style={{ maxWidth: 480, containerType: "inline-size" }}>
            <Preview img={approved} border="2px solid #2e7d32" />
          </div>
          {approved.subject && (
            <div style={{ fontSize: 12, opacity: 0.7, marginTop: 4 }}>
              From their record: “{approved.subject}”
            </div>
          )}
        </div>
      )}

      {candidates.length > 0 && (
        <div style={{ marginTop: 20 }}>
          <div style={{ fontSize: 13, fontWeight: 600, marginBottom: 8 }}>
            Waiting for approval — nobody sees these until one is chosen
          </div>
          <div
            style={{
              display: "grid",
              gridTemplateColumns: "repeat(auto-fill, minmax(220px, 1fr))",
              gap: 12,
            }}
          >
            {candidates.map((img) => (
              <div key={img.id} style={{ containerType: "inline-size" }}>
                <Preview img={img} border="1px solid #ccc" />
                <div style={{ display: "flex", gap: 6, marginTop: 6 }}>
                  <button onClick={() => decide(img.id, "approve")} disabled={busy}>
                    Use this one
                  </button>
                  <button onClick={() => decide(img.id, "reject")} disabled={busy}>
                    Discard
                  </button>
                </div>
              </div>
            ))}
          </div>
        </div>
      )}

      {items.length > 0 && (items[0].prompt ?? null) && (
        <div style={{ marginTop: 16 }}>
          <button
            onClick={() => setShowPrompt((v) => !v)}
            style={{ background: "none", border: "none", padding: 0, cursor: "pointer",
                     fontSize: 13, textDecoration: "underline", opacity: 0.8 }}
          >
            {showPrompt ? "Hide" : "Show"} what the model was asked for
          </button>
          {showPrompt && (
            <pre
              style={{
                whiteSpace: "pre-wrap",
                fontSize: 12,
                opacity: 0.8,
                marginTop: 8,
                maxWidth: "80ch",
                background: "rgba(0,0,0,0.04)",
                padding: 10,
                borderRadius: 6,
              }}
            >
              {items[0].prompt}
            </pre>
          )}
        </div>
      )}
    </section>
  );
}
