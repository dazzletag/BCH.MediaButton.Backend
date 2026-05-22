import { useCallback, useEffect, useState } from "react";
import { useApiClient } from "../hooks/useApiClient";
import type { CareHome, UserProfile } from "../types";

export default function Users() {
  const { call } = useApiClient();

  // Care homes
  const [careHomes, setCareHomes] = useState<CareHome[]>([]);
  const [newHomeName, setNewHomeName] = useState("");
  const [addingHome, setAddingHome] = useState(false);
  const [editHomeId, setEditHomeId] = useState<string | null>(null);
  const [editHomeName, setEditHomeName] = useState("");

  // Users
  const [users, setUsers] = useState<UserProfile[]>([]);
  const [usersLoading, setUsersLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [saveMsg, setSaveMsg] = useState<string | null>(null);

  // Resident list (for dropdowns)
  const [residentList, setResidentList] = useState<string[]>([]);

  // New user form
  const [newEmail, setNewEmail] = useState("");
  const [newName, setNewName] = useState("");
  const [newRole, setNewRole] = useState<"Activities" | "Relative">("Relative");
  const [newCareHomeId, setNewCareHomeId] = useState<string>("");
  const [addingUser, setAddingUser] = useState(false);

  // Editing a user
  const [editUserId, setEditUserId] = useState<string | null>(null);
  const [editEmail, setEditEmail] = useState("");
  const [editDisplayName, setEditDisplayName] = useState("");
  const [editRole, setEditRole] = useState<"Activities" | "Relative">("Relative");
  const [editCareHomeId, setEditCareHomeId] = useState<string>("");
  const [savingUser, setSavingUser] = useState(false);

  // Adding a resident grant
  const [grantUserId, setGrantUserId] = useState<string | null>(null);
  const [grantResident, setGrantResident] = useState("");
  const [addingGrant, setAddingGrant] = useState(false);

  const loadCareHomes = useCallback(async () => {
    try {
      const data = await call<CareHome[]>({ url: "/api/admin/care-homes", method: "GET" });
      setCareHomes(data ?? []);
    } catch {
      setError("Failed to load care homes.");
    }
  }, [call]);

  const loadUsers = useCallback(async () => {
    setUsersLoading(true);
    try {
      const data = await call<UserProfile[]>({ url: "/api/admin/users", method: "GET" });
      setUsers(data ?? []);
    } catch {
      setError("Failed to load users. Admin role required.");
    } finally {
      setUsersLoading(false);
    }
  }, [call]);

  const loadResidents = useCallback(async () => {
    try {
      const data = await call<string[]>({ url: "/api/admin/residents", method: "GET" });
      setResidentList(data ?? []);
    } catch { /* non-critical */ }
  }, [call]);

  useEffect(() => {
    loadCareHomes();
    loadUsers();
    loadResidents();
  }, [loadCareHomes, loadUsers, loadResidents]);

  const addCareHome = async () => {
    if (!newHomeName.trim()) return;
    setAddingHome(true);
    setError(null);
    try {
      await call({ url: "/api/admin/care-homes", method: "POST", data: { name: newHomeName.trim() } });
      setNewHomeName("");
      setSaveMsg("Care home added.");
      await loadCareHomes();
    } catch {
      setError("Failed to add care home.");
    } finally {
      setAddingHome(false);
    }
  };

  const saveHomeName = async (id: string) => {
    if (!editHomeName.trim()) return;
    try {
      await call({ url: `/api/admin/care-homes/${id}`, method: "PUT", data: { name: editHomeName.trim() } });
      setEditHomeId(null);
      setSaveMsg("Care home updated.");
      await loadCareHomes();
    } catch {
      setError("Failed to update care home.");
    }
  };

  const deleteCareHome = async (id: string, name: string) => {
    if (!window.confirm(`Delete care home "${name}"? This will un-assign any users linked to it.`)) return;
    try {
      await call({ url: `/api/admin/care-homes/${id}`, method: "DELETE" });
      setSaveMsg("Care home deleted.");
      await loadCareHomes();
      await loadUsers();
    } catch {
      setError("Failed to delete care home.");
    }
  };

  const addUser = async () => {
    if (!newEmail.trim()) { setError("Email is required."); return; }
    setAddingUser(true);
    setError(null);
    try {
      await call({
        url: "/api/admin/users",
        method: "POST",
        data: {
          azureAdEmail: newEmail.trim(),
          displayName: newName.trim() || null,
          role: newRole,
          careHomeId: newRole === "Activities" && newCareHomeId ? newCareHomeId : null,
        },
      });
      setNewEmail(""); setNewName(""); setNewRole("Relative"); setNewCareHomeId("");
      setSaveMsg("User profile created.");
      await loadUsers();
    } catch (err: any) {
      setError(err?.response?.data?.message ?? "Failed to create user.");
    } finally {
      setAddingUser(false);
    }
  };

  const startEdit = (u: UserProfile) => {
    setEditUserId(u.id);
    setEditEmail(u.azureAdEmail);
    setEditDisplayName(u.displayName ?? "");
    setEditRole(u.role);
    setEditCareHomeId(u.careHomeId ?? "");
  };

  const saveUser = async () => {
    if (!editUserId) return;
    setSavingUser(true);
    setError(null);
    try {
      await call({
        url: `/api/admin/users/${editUserId}`,
        method: "PUT",
        data: {
          azureAdEmail: editEmail.trim(),
          displayName: editDisplayName.trim() || null,
          role: editRole,
          careHomeId: editRole === "Activities" && editCareHomeId ? editCareHomeId : null,
        },
      });
      setEditUserId(null);
      setSaveMsg("User profile updated.");
      await loadUsers();
    } catch {
      setError("Failed to update user.");
    } finally {
      setSavingUser(false);
    }
  };

  const deleteUser = async (id: string, email: string) => {
    if (!window.confirm(`Remove portal profile for ${email}? They will lose access.`)) return;
    try {
      await call({ url: `/api/admin/users/${id}`, method: "DELETE" });
      setSaveMsg("User profile deleted.");
      await loadUsers();
    } catch {
      setError("Failed to delete user.");
    }
  };

  const addGrant = async () => {
    if (!grantUserId || !grantResident) return;
    setAddingGrant(true);
    setError(null);
    try {
      await call({
        url: `/api/admin/users/${grantUserId}/residents`,
        method: "POST",
        data: { residentKey: grantResident },
      });
      setGrantResident("");
      setSaveMsg("Resident access granted.");
      await loadUsers();
    } catch (err: any) {
      const msg = err?.response?.status === 409 ? "Already granted." : "Failed to grant access.";
      setError(msg);
    } finally {
      setAddingGrant(false);
    }
  };

  const removeGrant = async (userId: string, residentKey: string) => {
    try {
      await call({
        url: `/api/admin/users/${userId}/residents/${encodeURIComponent(residentKey)}`,
        method: "DELETE",
      });
      setSaveMsg("Resident access removed.");
      await loadUsers();
    } catch {
      setError("Failed to remove access.");
    }
  };

  return (
    <>
      <section className="hero">
        <div className="hero-left">
          <div className="hero-eyebrow">Bristol Care Homes</div>
          <h1 className="hero-title">User & access management</h1>
          <p className="hero-copy">
            Set up care homes, create portal profiles for relatives and activities teams, and control
            which residents each person can see.
          </p>
        </div>
      </section>

      <main className="content">
        {error && (
          <div className="card" style={{ borderColor: "rgba(217,72,15,0.3)" }}>
            <strong style={{ color: "var(--warning)" }}>Error: </strong>
            <span className="muted">{error}</span>
            <button className="btn ghost" style={{ marginLeft: 12, padding: "2px 8px" }} onClick={() => setError(null)}>✕</button>
          </div>
        )}
        {saveMsg && (
          <div className="card glass">
            <strong style={{ color: "var(--brand-strong)" }}>Done: </strong>
            <span className="muted">{saveMsg}</span>
            <button className="btn ghost" style={{ marginLeft: 12, padding: "2px 8px" }} onClick={() => setSaveMsg(null)}>✕</button>
          </div>
        )}

        {/* ── Care homes ── */}
        <div className="card glass">
          <div className="card-header">
            <p className="card-title">Care homes</p>
            <span className="tag">{careHomes.length} home(s)</span>
          </div>

          <div className="list" style={{ marginBottom: 16 }}>
            {careHomes.map((h) => (
              <div className="row" key={h.id}>
                {editHomeId === h.id ? (
                  <>
                    <input
                      className="input"
                      style={{ flex: 1 }}
                      value={editHomeName}
                      onChange={(e) => setEditHomeName(e.target.value)}
                      onKeyDown={(e) => { if (e.key === "Enter") saveHomeName(h.id); }}
                      autoFocus
                    />
                    <div className="nav-actions" style={{ gap: 6 }}>
                      <button className="btn primary" style={{ padding: "4px 10px" }} onClick={() => saveHomeName(h.id)}>Save</button>
                      <button className="btn ghost" style={{ padding: "4px 10px" }} onClick={() => setEditHomeId(null)}>Cancel</button>
                    </div>
                  </>
                ) : (
                  <>
                    <span style={{ fontWeight: 600 }}>{h.name}</span>
                    <div className="nav-actions" style={{ gap: 6 }}>
                      <button
                        className="btn ghost"
                        style={{ padding: "4px 10px" }}
                        onClick={() => { setEditHomeId(h.id); setEditHomeName(h.name); }}
                      >
                        Rename
                      </button>
                      <button
                        className="btn ghost"
                        style={{ padding: "4px 10px", color: "var(--warning)" }}
                        onClick={() => deleteCareHome(h.id, h.name)}
                      >
                        Delete
                      </button>
                    </div>
                  </>
                )}
              </div>
            ))}
          </div>

          <div className="form-row">
            <input
              className="input"
              placeholder="New care home name"
              value={newHomeName}
              onChange={(e) => setNewHomeName(e.target.value)}
              onKeyDown={(e) => { if (e.key === "Enter") addCareHome(); }}
            />
            <button className="btn primary" disabled={addingHome || !newHomeName.trim()} onClick={addCareHome}>
              {addingHome ? "Adding..." : "Add care home"}
            </button>
          </div>
        </div>

        {/* ── User profiles ── */}
        <div className="card glass">
          <div className="card-header">
            <p className="card-title">Portal user profiles</p>
            <span className="tag">{users.length} user(s)</span>
          </div>

          <p className="muted" style={{ fontSize: 13, marginBottom: 16 }}>
            Add profiles for relatives and activities teams. Admins always have full access and
            don't need a profile here. Relatives must log in with a BCH Azure AD account.
          </p>

          {usersLoading ? (
            <div className="muted">Loading...</div>
          ) : (
            <div className="list" style={{ marginBottom: 20 }}>
              {users.map((u) => (
                <div key={u.id} style={{ borderBottom: "1px solid rgba(255,255,255,0.07)", paddingBottom: 12, marginBottom: 12 }}>
                  {editUserId === u.id ? (
                    <div className="grid" style={{ gap: 10 }}>
                      <div className="form-row">
                        <label className="grid" style={{ flex: 2 }}>
                          <span className="muted">Email</span>
                          <input className="input" value={editEmail} onChange={(e) => setEditEmail(e.target.value)} />
                        </label>
                        <label className="grid" style={{ flex: 2 }}>
                          <span className="muted">Display name</span>
                          <input className="input" value={editDisplayName} onChange={(e) => setEditDisplayName(e.target.value)} placeholder="Optional" />
                        </label>
                        <label className="grid" style={{ flex: 1 }}>
                          <span className="muted">Role</span>
                          <select className="select" value={editRole} onChange={(e) => setEditRole(e.target.value as "Activities" | "Relative")}>
                            <option value="Relative">Relative</option>
                            <option value="Activities">Activities</option>
                          </select>
                        </label>
                        {editRole === "Activities" && (
                          <label className="grid" style={{ flex: 2 }}>
                            <span className="muted">Care home</span>
                            <select className="select" value={editCareHomeId} onChange={(e) => setEditCareHomeId(e.target.value)}>
                              <option value="">— Select care home —</option>
                              {careHomes.map((h) => <option key={h.id} value={h.id}>{h.name}</option>)}
                            </select>
                          </label>
                        )}
                      </div>
                      <div className="nav-actions" style={{ gap: 8 }}>
                        <button className="btn primary" disabled={savingUser} onClick={saveUser}>
                          {savingUser ? "Saving..." : "Save changes"}
                        </button>
                        <button className="btn ghost" onClick={() => setEditUserId(null)}>Cancel</button>
                      </div>
                    </div>
                  ) : (
                    <div style={{ display: "flex", justifyContent: "space-between", alignItems: "flex-start", gap: 16 }}>
                      <div style={{ flex: 1 }}>
                        <div style={{ fontWeight: 600, marginBottom: 2 }}>
                          {u.displayName || u.azureAdEmail}
                          <span className="tag" style={{ marginLeft: 8 }}>{u.role}</span>
                        </div>
                        <div className="muted" style={{ fontSize: 13 }}>{u.azureAdEmail}</div>
                        {u.role === "Activities" && (
                          <div className="muted" style={{ fontSize: 13 }}>
                            Care home: {u.careHomeName ?? <em>none assigned</em>}
                          </div>
                        )}
                        {u.role === "Relative" && (
                          <div style={{ marginTop: 8 }}>
                            <div className="muted" style={{ fontSize: 12, marginBottom: 4 }}>
                              Residents: {u.residents.length === 0 ? <em>none</em> : null}
                            </div>
                            <div style={{ display: "flex", flexWrap: "wrap", gap: 6 }}>
                              {u.residents.map((r) => (
                                <span key={r} className="pill" style={{ fontSize: 12 }}>
                                  {r}
                                  <button
                                    style={{ background: "none", border: "none", cursor: "pointer", marginLeft: 4, color: "var(--muted)", padding: 0, lineHeight: 1 }}
                                    onClick={() => removeGrant(u.id, r)}
                                    title={`Remove access to ${r}`}
                                  >
                                    ×
                                  </button>
                                </span>
                              ))}
                            </div>
                            {grantUserId === u.id ? (
                              <div className="form-row" style={{ marginTop: 8 }}>
                                <select
                                  className="select"
                                  value={grantResident}
                                  onChange={(e) => setGrantResident(e.target.value)}
                                  style={{ flex: 1 }}
                                >
                                  <option value="">— Select resident —</option>
                                  {residentList
                                    .filter((r) => !u.residents.includes(r))
                                    .map((r) => <option key={r} value={r}>{r}</option>)}
                                </select>
                                <button
                                  className="btn primary"
                                  style={{ padding: "4px 10px" }}
                                  disabled={addingGrant || !grantResident}
                                  onClick={addGrant}
                                >
                                  {addingGrant ? "Adding..." : "Add"}
                                </button>
                                <button className="btn ghost" style={{ padding: "4px 10px" }} onClick={() => { setGrantUserId(null); setGrantResident(""); }}>
                                  Cancel
                                </button>
                              </div>
                            ) : (
                              <button
                                className="btn ghost"
                                style={{ marginTop: 6, padding: "3px 10px", fontSize: 12 }}
                                onClick={() => { setGrantUserId(u.id); setGrantResident(""); }}
                              >
                                + Add resident
                              </button>
                            )}
                          </div>
                        )}
                      </div>
                      <div className="nav-actions" style={{ gap: 6, flexShrink: 0 }}>
                        <button className="btn ghost" style={{ padding: "4px 10px" }} onClick={() => startEdit(u)}>Edit</button>
                        <button
                          className="btn ghost"
                          style={{ padding: "4px 10px", color: "var(--warning)" }}
                          onClick={() => deleteUser(u.id, u.azureAdEmail)}
                        >
                          Delete
                        </button>
                      </div>
                    </div>
                  )}
                </div>
              ))}
              {users.length === 0 && !usersLoading && (
                <div className="muted">No user profiles yet. Add one below.</div>
              )}
            </div>
          )}

          {/* Add new user form */}
          <div style={{ borderTop: "1px solid rgba(255,255,255,0.1)", paddingTop: 16 }}>
            <p className="card-title" style={{ fontSize: 14, marginBottom: 12 }}>Add new user profile</p>
            <div className="form-row" style={{ flexWrap: "wrap", gap: 10 }}>
              <label className="grid" style={{ flex: "2 1 200px" }}>
                <span className="muted">Azure AD email (UPN)</span>
                <input
                  className="input"
                  placeholder="relative@example.com"
                  value={newEmail}
                  onChange={(e) => setNewEmail(e.target.value)}
                />
              </label>
              <label className="grid" style={{ flex: "2 1 160px" }}>
                <span className="muted">Display name (optional)</span>
                <input
                  className="input"
                  placeholder="Jane Smith"
                  value={newName}
                  onChange={(e) => setNewName(e.target.value)}
                />
              </label>
              <label className="grid" style={{ flex: "1 1 120px" }}>
                <span className="muted">Role</span>
                <select className="select" value={newRole} onChange={(e) => setNewRole(e.target.value as "Activities" | "Relative")}>
                  <option value="Relative">Relative</option>
                  <option value="Activities">Activities</option>
                </select>
              </label>
              {newRole === "Activities" && (
                <label className="grid" style={{ flex: "2 1 180px" }}>
                  <span className="muted">Care home</span>
                  <select className="select" value={newCareHomeId} onChange={(e) => setNewCareHomeId(e.target.value)}>
                    <option value="">— Select care home —</option>
                    {careHomes.map((h) => <option key={h.id} value={h.id}>{h.name}</option>)}
                  </select>
                </label>
              )}
            </div>
            <button
              className="btn primary"
              style={{ marginTop: 12 }}
              disabled={addingUser || !newEmail.trim()}
              onClick={addUser}
            >
              {addingUser ? "Adding..." : "Create user profile"}
            </button>
          </div>
        </div>
      </main>
    </>
  );
}
