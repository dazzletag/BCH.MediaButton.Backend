import { useState, useEffect, useCallback, useMemo } from "react";
import { useParams, useNavigate } from "react-router-dom";
import { useMsal } from "@azure/msal-react";
import { useApiClient } from "../hooks/useApiClient";
import { db } from "../db";
import type {
  CarePlanVersionResponse,
  CarePlanVersionSummary,
  AssessmentField,
  CareActionsData,
  CareRoutine,
  CareStep,
} from "../types";
import { CARE_PLAN_SECTIONS } from "../types";
import "../styles/care-plan.css";

type ViewerMode = "manager" | "carer";
type TabKey = "assessment" | "care_actions" | "sign_off";

interface CarePlanViewerProps {
  mode?: ViewerMode;
}

function formatDate(iso: string | null | undefined): string {
  if (!iso) return "—";
  return new Date(iso).toLocaleDateString("en-GB", {
    day: "numeric",
    month: "short",
    year: "numeric",
  });
}

function formatDateTime(iso: string | null | undefined): string {
  if (!iso) return "—";
  return new Date(iso).toLocaleDateString("en-GB", {
    day: "numeric",
    month: "short",
    year: "numeric",
    hour: "2-digit",
    minute: "2-digit",
  });
}

function initials(name: string): string {
  return name
    .split(/\s+/)
    .map((w) => w[0])
    .join("")
    .toUpperCase()
    .slice(0, 2);
}

function sectionLabel(key: string): string {
  return CARE_PLAN_SECTIONS.find((s) => s.key === key)?.label ?? key;
}

export default function CarePlanViewer({ mode = "manager" }: CarePlanViewerProps) {
  const { residentId = "", section: urlSection } = useParams<{
    residentId: string;
    section: string;
  }>();
  const navigate = useNavigate();
  const { call } = useApiClient();
  const { accounts } = useMsal();

  const section = urlSection || CARE_PLAN_SECTIONS[0].key;
  const isCarer = mode === "carer";

  // ── State ──
  const [current, setCurrent] = useState<CarePlanVersionResponse | null>(null);
  const [versions, setVersions] = useState<CarePlanVersionSummary[]>([]);
  const [selectedVersion, setSelectedVersion] = useState<CarePlanVersionResponse | null>(null);
  const [editing, setEditing] = useState(false);
  const [activeTab, setActiveTab] = useState<TabKey>(isCarer ? "care_actions" : "care_actions");
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [offline, setOffline] = useState(false);
  const [cachedAt, setCachedAt] = useState<string | null>(null);

  // Draft state (editable copies)
  const [draftAssessment, setDraftAssessment] = useState<AssessmentField[]>([]);
  const [draftCareActions, setDraftCareActions] = useState<CareActionsData>({});
  const [signOffForm, setSignOffForm] = useState({
    residentInvolved: "",
    nextReviewDate: "",
    notes: "",
  });

  // Section content presence for sidebar dots
  const [sectionPresence, setSectionPresence] = useState<Record<string, boolean>>({});

  const basePath = useMemo(
    () => `/api/residents/${encodeURIComponent(residentId)}/care-plans/${encodeURIComponent(section)}`,
    [residentId, section]
  );

  // ── Data Fetching ──

  const loadCurrent = useCallback(async () => {
    setLoading(true);
    setError(null);
    setOffline(false);
    try {
      const data = await call<CarePlanVersionResponse>({
        url: `${basePath}/current`,
        method: "GET",
      });
      setCurrent(data);
      setSelectedVersion(null);
      setEditing(false);

      // Cache for offline use
      const cacheKey = `${residentId}::${section}`;
      await db.carePlans.put({
        key: cacheKey,
        residentId,
        section,
        data: JSON.stringify(data),
        syncedAt: new Date().toISOString(),
      });
    } catch (err: any) {
      if (!navigator.onLine && isCarer) {
        // Try Dexie cache
        const cacheKey = `${residentId}::${section}`;
        const cached = await db.carePlans.get(cacheKey);
        if (cached) {
          setCurrent(JSON.parse(cached.data));
          setOffline(true);
          setCachedAt(cached.syncedAt);
        } else {
          setError("Offline and no cached data available.");
        }
      } else if (err?.response?.status === 404) {
        setCurrent(null);
      } else {
        setError(err?.response?.data?.error ?? "Failed to load care plan.");
      }
    } finally {
      setLoading(false);
    }
  }, [call, basePath, residentId, section, isCarer]);

  const loadVersions = useCallback(async () => {
    try {
      const data = await call<CarePlanVersionSummary[]>({
        url: `${basePath}/versions`,
        method: "GET",
      });
      setVersions(data);
    } catch {
      // Non-critical
    }
  }, [call, basePath]);

  const loadSectionPresence = useCallback(async () => {
    const presence: Record<string, boolean> = {};
    for (const s of CARE_PLAN_SECTIONS) {
      try {
        await call<CarePlanVersionResponse>({
          url: `/api/residents/${encodeURIComponent(residentId)}/care-plans/${encodeURIComponent(s.key)}/current`,
          method: "GET",
        });
        presence[s.key] = true;
      } catch {
        presence[s.key] = false;
      }
    }
    setSectionPresence(presence);
  }, [call, residentId]);

  useEffect(() => {
    if (!residentId) return;
    loadCurrent();
    loadVersions();
  }, [residentId, section, loadCurrent, loadVersions]);

  useEffect(() => {
    if (!residentId) return;
    loadSectionPresence();
  }, [residentId, loadSectionPresence]);

  // ── Edit Actions ──

  const handleEdit = useCallback(async () => {
    setSaving(true);
    try {
      const draft = await call<CarePlanVersionResponse>({
        url: `${basePath}/draft`,
        method: "POST",
      });
      setCurrent(draft);
      setDraftAssessment(draft.assessmentData ?? []);
      setDraftCareActions(draft.careActionsData ?? {});
      setSignOffForm({ residentInvolved: "", nextReviewDate: "", notes: "" });
      setEditing(true);
      setActiveTab("care_actions");
    } catch (err: any) {
      setError(err?.response?.data?.error ?? "Failed to create draft.");
    } finally {
      setSaving(false);
    }
  }, [call, basePath]);

  const handleCancel = useCallback(() => {
    setEditing(false);
    loadCurrent();
  }, [loadCurrent]);

  const handleSaveDraft = useCallback(async () => {
    setSaving(true);
    try {
      const updated = await call<CarePlanVersionResponse>({
        url: `${basePath}/draft`,
        method: "PUT",
        data: {
          assessmentData: draftAssessment,
          careActionsData: draftCareActions,
        },
      });
      setCurrent(updated);
    } catch (err: any) {
      setError(err?.response?.data?.error ?? "Failed to save draft.");
    } finally {
      setSaving(false);
    }
  }, [call, basePath, draftAssessment, draftCareActions]);

  const handleSignOff = useCallback(async () => {
    if (!signOffForm.residentInvolved) {
      setError("Please indicate resident/representative involvement.");
      return;
    }
    if (!signOffForm.nextReviewDate) {
      setError("Please set the next review date.");
      return;
    }

    setSaving(true);
    setError(null);
    try {
      const signed = await call<CarePlanVersionResponse>({
        url: `${basePath}/sign-off`,
        method: "POST",
        data: {
          assessmentData: draftAssessment,
          careActionsData: draftCareActions,
          residentInvolved: signOffForm.residentInvolved,
          nextReviewDate: signOffForm.nextReviewDate,
          notes: signOffForm.notes,
        },
      });
      setCurrent(signed);
      setEditing(false);
      setActiveTab("care_actions");
      loadVersions();

      // Update cache
      const cacheKey = `${residentId}::${section}`;
      await db.carePlans.put({
        key: cacheKey,
        residentId,
        section,
        data: JSON.stringify(signed),
        syncedAt: new Date().toISOString(),
      });
    } catch (err: any) {
      setError(err?.response?.data?.error ?? "Failed to sign off.");
    } finally {
      setSaving(false);
    }
  }, [call, basePath, draftAssessment, draftCareActions, signOffForm, residentId, section, loadVersions]);

  const handleViewVersion = useCallback(
    async (versionId: string) => {
      try {
        const data = await call<CarePlanVersionResponse>({
          url: `${basePath}/versions/${versionId}`,
          method: "GET",
        });
        setSelectedVersion(data);
        setEditing(false);
      } catch (err: any) {
        setError("Failed to load version.");
      }
    },
    [call, basePath]
  );

  const handleBackToCurrent = useCallback(() => {
    setSelectedVersion(null);
  }, []);

  const handlePrint = useCallback(() => {
    window.print();
  }, []);

  // ── Helpers ──

  const displayVersion = selectedVersion ?? current;
  const assessmentFields = editing ? draftAssessment : (displayVersion?.assessmentData ?? []);
  const careActions = editing ? draftCareActions : (displayVersion?.careActionsData ?? {});
  const signOff = displayVersion?.signOff;
  const statusLabel = editing ? "Draft" : displayVersion?.status === "signed_off" ? "Signed Off" : "Draft";
  const statusClass = editing || displayVersion?.status !== "signed_off" ? "draft" : "signed-off";

  const cqcCount = useMemo(() => {
    let count = 0;
    if (assessmentFields.length > 0) count++;
    if ((careActions.routines?.length ?? 0) > 0) count++;
    if (signOff) count++;
    return count;
  }, [assessmentFields, careActions, signOff]);

  const prevSignedVersion = useMemo(() => {
    return versions.find((v) => v.status === "signed_off");
  }, [versions]);

  // ── Draft field updaters ──

  const updateAssessmentField = useCallback((key: string, value: string) => {
    setDraftAssessment((prev) =>
      prev.map((f) => (f.key === key ? { ...f, value } : f))
    );
  }, []);

  const updateRoutineStep = useCallback(
    (routineIdx: number, stepIdx: number, field: keyof CareStep, value: string) => {
      setDraftCareActions((prev) => {
        const routines = [...(prev.routines ?? [])];
        const routine = { ...routines[routineIdx] };
        const steps = [...(routine.steps ?? [])];
        steps[stepIdx] = { ...steps[stepIdx], [field]: value };
        routine.steps = steps;
        routines[routineIdx] = routine;
        return { ...prev, routines };
      });
    },
    []
  );

  const updateCriticalPreference = useCallback((idx: number, value: string) => {
    setDraftCareActions((prev) => {
      const prefs = [...(prev.criticalPreferences ?? [])];
      prefs[idx] = value;
      return { ...prev, criticalPreferences: prefs };
    });
  }, []);

  const addRoutine = useCallback(() => {
    setDraftCareActions((prev) => ({
      ...prev,
      routines: [
        ...(prev.routines ?? []),
        { title: "New Routine", frequency: "Daily", time: null, steps: [] },
      ],
    }));
  }, []);

  const addStepToRoutine = useCallback((routineIdx: number) => {
    setDraftCareActions((prev) => {
      const routines = [...(prev.routines ?? [])];
      const routine = { ...routines[routineIdx] };
      routine.steps = [...(routine.steps ?? []), { action: "", who: "All care staff" }];
      routines[routineIdx] = routine;
      return { ...prev, routines };
    });
  }, []);

  const addCriticalPreference = useCallback(() => {
    setDraftCareActions((prev) => ({
      ...prev,
      criticalPreferences: [...(prev.criticalPreferences ?? []), ""],
    }));
  }, []);

  const updateRoutineField = useCallback(
    (routineIdx: number, field: keyof CareRoutine, value: string) => {
      setDraftCareActions((prev) => {
        const routines = [...(prev.routines ?? [])];
        routines[routineIdx] = { ...routines[routineIdx], [field]: value };
        return { ...prev, routines };
      });
    },
    []
  );

  // ── Render: Sidebar ──

  const sidebar = (
    <aside className="cp-sidebar">
      <div className="cp-resident-summary">
        <div className="cp-avatar">{initials(residentId)}</div>
        <h3 className="cp-resident-name">{residentId}</h3>
        <p className="cp-resident-detail">Care Plan</p>
      </div>
      <ul className="cp-section-nav">
        {CARE_PLAN_SECTIONS.map((s) => (
          <li key={s.key}>
            <button
              className={section === s.key ? "active" : ""}
              onClick={() =>
                navigate(
                  `/care-plans/${encodeURIComponent(residentId)}/${s.key}`
                )
              }
            >
              <span
                className={`cp-section-dot ${sectionPresence[s.key] ? "" : "empty"}`}
              />
              <span className="cp-section-label">{s.label}</span>
            </button>
          </li>
        ))}
      </ul>
    </aside>
  );

  // ── Render: Top Bar ──

  const topBar = (
    <div className="cp-topbar">
      <div className="cp-topbar-row">
        <div className="cp-topbar-left">
          <h1 className="cp-plan-title">{sectionLabel(section)}</h1>
          <span className={`cp-status-badge ${statusClass}`}>{statusLabel}</span>
          {cqcCount > 0 && (
            <span className="cp-cqc-count">{cqcCount} CQC statement{cqcCount !== 1 ? "s" : ""}</span>
          )}
        </div>
        <div className="cp-topbar-actions">
          {selectedVersion && (
            <button className="cp-btn ghost" onClick={handleBackToCurrent}>
              Back to Current
            </button>
          )}
          {!editing && !isCarer && !selectedVersion && (
            <>
              <button className="cp-btn ghost" onClick={() => setActiveTab("sign_off")}>
                Version History
              </button>
              <button className="cp-btn ghost" onClick={handlePrint}>
                Print
              </button>
              <button className="cp-btn amber" onClick={handleEdit} disabled={saving}>
                {saving ? "Creating draft..." : "Edit"}
              </button>
            </>
          )}
          {editing && (
            <>
              <button className="cp-btn ghost" onClick={handleSaveDraft} disabled={saving}>
                Save Draft
              </button>
              <button className="cp-btn teal" onClick={handleSignOff} disabled={saving}>
                {saving ? "Saving..." : "Submit & Sign Off"}
              </button>
              <button className="cp-btn danger-ghost" onClick={handleCancel} disabled={saving}>
                Cancel
              </button>
            </>
          )}
        </div>
      </div>
      <div className="cp-meta-bar">
        {displayVersion && (
          <>
            <span>
              v{displayVersion.versionNumber} by{" "}
              <strong>{displayVersion.createdByName ?? "Unknown"}</strong>
            </span>
            <span>{formatDateTime(displayVersion.createdAt)}</span>
            {signOff?.nextReviewDate && (
              <span>Next review: <strong>{formatDate(signOff.nextReviewDate)}</strong></span>
            )}
          </>
        )}
        {editing && <span className="cp-draft-indicator">Editing draft — not yet signed off</span>}
      </div>
    </div>
  );

  // ── Render: Tabs ──

  const tabs = (
    <div className="cp-tabs">
      <button
        className={`cp-tab ${activeTab === "assessment" ? "active" : ""}`}
        onClick={() => setActiveTab("assessment")}
      >
        Assessment
      </button>
      <button
        className={`cp-tab ${activeTab === "care_actions" ? "active" : ""}`}
        onClick={() => setActiveTab("care_actions")}
      >
        Care Actions
      </button>
      {!isCarer && (
        <button
          className={`cp-tab ${activeTab === "sign_off" ? "active" : ""}`}
          onClick={() => setActiveTab("sign_off")}
        >
          Sign Off
        </button>
      )}
    </div>
  );

  // ── Render: Assessment Tab ──

  const assessmentTab = (
    <div className="cp-assessment-grid">
      {assessmentFields.length === 0 && (
        <div className="cp-empty-state full-width" style={{ gridColumn: "1 / -1" }}>
          <h3>No assessment data</h3>
          <p>Assessment fields will appear here once added.</p>
        </div>
      )}
      {assessmentFields.map((field) => {
        const isNarrative = field.type === "narrative";
        const cardClass = `cp-field-card ${field.isPrimary ? "primary" : isNarrative ? "full-width" : ""}`;
        return (
          <div key={field.key} className={cardClass}>
            <p className="cp-field-label">{field.label}</p>
            {editing ? (
              isNarrative ? (
                <textarea
                  className="textarea"
                  rows={4}
                  value={field.value ?? ""}
                  onChange={(e) => updateAssessmentField(field.key, e.target.value)}
                />
              ) : (
                <input
                  className="input"
                  type="text"
                  value={field.value ?? ""}
                  onChange={(e) => updateAssessmentField(field.key, e.target.value)}
                />
              )
            ) : field.value ? (
              <p className="cp-field-value">{field.value}</p>
            ) : (
              <p className="cp-field-empty">Not recorded</p>
            )}
          </div>
        );
      })}
    </div>
  );

  // ── Render: Care Actions Tab ──

  const careActionsTab = (
    <div>
      {/* Critical preferences banner */}
      {(careActions.criticalPreferences ?? []).length > 0 && (
        <div>
          {(careActions.criticalPreferences ?? []).map((pref, idx) => (
            <div key={idx} className="cp-critical-banner">
              <span className="cp-critical-banner-icon">!</span>
              {editing ? (
                <input
                  className="input"
                  type="text"
                  value={pref}
                  onChange={(e) => updateCriticalPreference(idx, e.target.value)}
                  style={{ flex: 1 }}
                />
              ) : (
                <span>{pref}</span>
              )}
            </div>
          ))}
        </div>
      )}

      {editing && (
        <button
          className="cp-btn ghost"
          style={{ marginBottom: 16 }}
          onClick={addCriticalPreference}
        >
          + Add Critical Preference
        </button>
      )}

      {/* Routine cards */}
      {(careActions.routines ?? []).map((routine, rIdx) => (
        <div key={rIdx} className="cp-routine-card">
          <div className="cp-routine-header">
            {editing ? (
              <input
                className="input"
                type="text"
                value={routine.title}
                onChange={(e) => updateRoutineField(rIdx, "title", e.target.value)}
                style={{ maxWidth: 300 }}
              />
            ) : (
              <h3 className="cp-routine-title">{routine.title}</h3>
            )}
            <span className="cp-routine-badge">
              {editing ? (
                <input
                  className="input"
                  type="text"
                  value={[routine.frequency, routine.time].filter(Boolean).join(" — ")}
                  onChange={(e) => {
                    const parts = e.target.value.split(" — ");
                    updateRoutineField(rIdx, "frequency", parts[0] ?? "");
                    if (parts[1]) updateRoutineField(rIdx, "time", parts[1]);
                  }}
                  style={{ width: 160, padding: "2px 6px", fontSize: 12 }}
                />
              ) : (
                [routine.frequency, routine.time].filter(Boolean).join(" — ")
              )}
            </span>
          </div>
          <ol className="cp-steps-list">
            {(routine.steps ?? []).map((step, sIdx) => (
              <li key={sIdx} className="cp-step">
                <span className="cp-step-number">{sIdx + 1}</span>
                <div className="cp-step-body">
                  {editing ? (
                    <>
                      <textarea
                        className="textarea"
                        rows={2}
                        value={step.action}
                        onChange={(e) => updateRoutineStep(rIdx, sIdx, "action", e.target.value)}
                        placeholder="Action..."
                      />
                      <input
                        className="input"
                        type="text"
                        value={step.who ?? ""}
                        onChange={(e) => updateRoutineStep(rIdx, sIdx, "who", e.target.value)}
                        placeholder="Who (e.g. All care staff)"
                        style={{ marginTop: 4 }}
                      />
                    </>
                  ) : (
                    <>
                      <p className="cp-step-action">{step.action}</p>
                      {step.who && <p className="cp-step-who">{step.who}</p>}
                    </>
                  )}
                </div>
              </li>
            ))}
          </ol>
          {editing && (
            <div style={{ padding: "10px 18px" }}>
              <button className="cp-btn ghost" onClick={() => addStepToRoutine(rIdx)}>
                + Add Step
              </button>
            </div>
          )}
        </div>
      ))}

      {editing && (
        <button className="cp-btn ghost" onClick={addRoutine}>
          + Add Routine
        </button>
      )}

      {!editing && (careActions.routines ?? []).length === 0 && (
        <div className="cp-empty-state">
          <h3>No care actions</h3>
          <p>Care actions and routines will appear here once added.</p>
        </div>
      )}
    </div>
  );

  // ── Render: Sign Off Tab ──

  const signOffTab = editing ? (
    // Edit mode: blank sign-off form
    <div className="cp-signoff-card">
      <div className="cp-info-notice">
        <span style={{ fontSize: 18, flexShrink: 0 }}>i</span>
        <span>
          {prevSignedVersion
            ? `The previous signed-off version (v${prevSignedVersion.versionNumber}, ${formatDate(
                prevSignedVersion.createdAt
              )}) is preserved. Complete this section and click Submit & Sign Off to create v${
                (current?.versionNumber ?? 1)
              }.`
            : "Complete this section and click Submit & Sign Off to create the first signed-off version."}
        </span>
      </div>

      <div className="cp-form-group">
        <label className="cp-form-label">Resident / Representative Involvement</label>
        <div className="cp-radio-group">
          {["Resident involved", "Representative involved", "Neither — documented reason below"].map(
            (opt) => (
              <label key={opt}>
                <input
                  type="radio"
                  name="residentInvolved"
                  value={opt}
                  checked={signOffForm.residentInvolved === opt}
                  onChange={() => setSignOffForm((f) => ({ ...f, residentInvolved: opt }))}
                />
                {opt}
              </label>
            )
          )}
        </div>
      </div>

      <div className="cp-form-group">
        <label className="cp-form-label">Next Review Date</label>
        <input
          className="input"
          type="date"
          value={signOffForm.nextReviewDate}
          onChange={(e) => setSignOffForm((f) => ({ ...f, nextReviewDate: e.target.value }))}
        />
      </div>

      <div className="cp-form-group">
        <label className="cp-form-label">Review Notes</label>
        <textarea
          className="textarea"
          rows={4}
          value={signOffForm.notes}
          onChange={(e) => setSignOffForm((f) => ({ ...f, notes: e.target.value }))}
          placeholder="Any notes about this review..."
        />
      </div>

      <p className="muted" style={{ fontSize: 13 }}>
        Completed by: <strong>{accounts[0]?.name ?? "Current user"}</strong> — timestamp will be
        recorded automatically on sign-off.
      </p>
    </div>
  ) : (
    // Read mode: show current sign-off + version history
    <div>
      {signOff ? (
        <div className="cp-signoff-card">
          <div className="cp-signoff-header">
            <h3 className="cp-signoff-title">Sign Off Details</h3>
            <span className="cp-status-badge signed-off">Signed Off</span>
          </div>
          <div className="cp-signoff-grid">
            <div className="cp-signoff-field">
              <span className="cp-signoff-field-label">Completed By</span>
              <span className="cp-signoff-field-value">{signOff.completedByName ?? "—"}</span>
            </div>
            <div className="cp-signoff-field">
              <span className="cp-signoff-field-label">Role</span>
              <span className="cp-signoff-field-value">{signOff.completedByRole ?? "—"}</span>
            </div>
            <div className="cp-signoff-field">
              <span className="cp-signoff-field-label">Date</span>
              <span className="cp-signoff-field-value">{formatDateTime(signOff.completedAt)}</span>
            </div>
            <div className="cp-signoff-field">
              <span className="cp-signoff-field-label">Next Review</span>
              <span className="cp-signoff-field-value">{formatDate(signOff.nextReviewDate)}</span>
            </div>
            <div className="cp-signoff-field">
              <span className="cp-signoff-field-label">Resident Involvement</span>
              <span className="cp-signoff-field-value">{signOff.residentInvolved ?? "—"}</span>
            </div>
            {signOff.notes && (
              <div className="cp-signoff-field full-width">
                <span className="cp-signoff-field-label">Notes</span>
                <span className="cp-signoff-field-value">{signOff.notes}</span>
              </div>
            )}
          </div>
        </div>
      ) : (
        <div className="cp-empty-state">
          <h3>Not yet signed off</h3>
          <p>This care plan section has no signed-off version yet.</p>
        </div>
      )}

      {/* Version history */}
      <div className="cp-version-list">
        <h4 className="cp-version-list-title">Version History</h4>
        {versions.length === 0 ? (
          <p className="muted" style={{ fontSize: 14 }}>No versions recorded.</p>
        ) : (
          versions.map((v) => (
            <div
              key={v.id}
              className={`cp-version-row ${selectedVersion?.id === v.id ? "active" : ""}`}
              onClick={() => handleViewVersion(v.id)}
            >
              <div className="cp-version-row-left">
                <span className="cp-version-number">v{v.versionNumber}</span>
                <span className="cp-version-author">{v.createdByName ?? "Unknown"}</span>
                <span
                  className={`cp-status-badge ${v.status === "signed_off" ? "signed-off" : "draft"}`}
                  style={{ fontSize: 10, padding: "2px 8px" }}
                >
                  {v.status === "signed_off" ? "Signed Off" : "Draft"}
                </span>
              </div>
              <span className="cp-version-date">{formatDate(v.createdAt)}</span>
            </div>
          ))
        )}
      </div>
    </div>
  );

  // ── Render: Print sections (hidden on screen, visible in print) ──

  const printSections = (
    <div style={{ display: "none" }} className="cp-print-sections">
      <div className="cp-print-section">
        <h2 className="cp-print-section-title">Assessment</h2>
        {assessmentTab}
      </div>
      <div className="cp-print-section">
        <h2 className="cp-print-section-title">Care Actions</h2>
        {careActionsTab}
      </div>
      <div className="cp-print-section">
        <h2 className="cp-print-section-title">Sign Off</h2>
        {signOff ? signOffTab : <p className="muted">Not signed off.</p>}
      </div>
    </div>
  );

  // ── Render: Main ──

  if (!residentId) {
    return (
      <div className="cp-loading">
        <p>No resident selected. Navigate to a care plan to get started.</p>
      </div>
    );
  }

  return (
    <div className={`cp-shell ${isCarer ? "carer-mode" : ""}`}>
      {sidebar}

      <div style={{ display: "flex", flexDirection: "column", minHeight: "100vh" }}>
        {offline && (
          <div className="cp-offline-banner">
            Offline — last synced {cachedAt ? formatDateTime(cachedAt) : "unknown"}
          </div>
        )}

        {topBar}
        {tabs}

        <div className="cp-content">
          {error && (
            <div className="cp-critical-banner" style={{ marginBottom: 16 }}>
              <span className="cp-critical-banner-icon">!</span>
              <span>{error}</span>
              <button
                className="cp-btn ghost"
                style={{ marginLeft: "auto", padding: "4px 12px", minHeight: 32 }}
                onClick={() => setError(null)}
              >
                Dismiss
              </button>
            </div>
          )}

          {loading ? (
            <div className="cp-loading">Loading care plan...</div>
          ) : (
            <>
              {activeTab === "assessment" && assessmentTab}
              {activeTab === "care_actions" && careActionsTab}
              {activeTab === "sign_off" && !isCarer && signOffTab}
            </>
          )}
        </div>

        {printSections}
      </div>
    </div>
  );
}
