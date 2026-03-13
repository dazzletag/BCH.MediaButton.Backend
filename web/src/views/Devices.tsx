import React, { useCallback, useEffect, useState } from "react";
import { useApiClient } from "../hooks/useApiClient";
import type { Device } from "../types";

interface SharePointCreds {
  tenantId: string;
  clientId: string;
  clientSecret: string;
  driveId: string;
  itemId: string;
  itemPath: string;
}

function buildInstallCommand(apiBase: string, deviceId: string, key: string, sp?: SharePointCreds) {
  let cmd = `curl -sSL ${apiBase}/install.sh \\\n  | sudo bash -s -- \\\n      --api ${apiBase} \\\n      --device "${deviceId}" \\\n      --key "${key}"`;
  if (sp?.tenantId)     cmd += ` \\\n      --tenant-id "${sp.tenantId}"`;
  if (sp?.clientId)     cmd += ` \\\n      --client-id "${sp.clientId}"`;
  if (sp?.clientSecret) cmd += ` \\\n      --client-secret "${sp.clientSecret}"`;
  if (sp?.driveId)      cmd += ` \\\n      --drive-id "${sp.driveId}"`;
  if (sp?.itemId)       cmd += ` \\\n      --item-id "${sp.itemId}"`;
  else if (sp?.itemPath) cmd += ` \\\n      --item-path "${sp.itemPath}"`;
  return cmd;
}

export default function Devices() {
  const { call } = useApiClient();
  const apiBase = window.location.origin;

  const [devices, setDevices] = useState<Device[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const [newDeviceId, setNewDeviceId] = useState("");
  const [newDisplayName, setNewDisplayName] = useState("");
  const [adding, setAdding] = useState(false);
  const [addError, setAddError] = useState<string | null>(null);

  const [copiedId, setCopiedId] = useState<string | null>(null);

  const [sp, setSp] = useState<SharePointCreds>({
    tenantId: "", clientId: "", clientSecret: "", driveId: "", itemId: "", itemPath: "",
  });
  const setSPField = (field: keyof SharePointCreds) => (e: React.ChangeEvent<HTMLInputElement>) =>
    setSp((prev) => ({ ...prev, [field]: e.target.value }));

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const data = await call<Device[]>({ url: "/api/admin/devices", method: "GET" });
      setDevices(data);
    } catch {
      setError("Failed to load devices. Admin role required.");
    } finally {
      setLoading(false);
    }
  }, [call]);

  useEffect(() => {
    load();
  }, [load]);

  const handleAdd = async () => {
    if (!newDeviceId.trim()) {
      setAddError("Pi serial number is required.");
      return;
    }
    setAdding(true);
    setAddError(null);
    try {
      const created = await call<Device>({
        url: "/api/admin/devices",
        method: "POST",
        data: { deviceId: newDeviceId.trim(), displayName: newDisplayName.trim() || null },
      });
      setDevices((prev) => [...prev, created]);
      setNewDeviceId("");
      setNewDisplayName("");
    } catch (err: unknown) {
      const msg =
        err && typeof err === "object" && "response" in err
          ? ((err as { response?: { data?: string } }).response?.data ?? "Failed to add device.")
          : "Failed to add device.";
      setAddError(typeof msg === "string" ? msg : "Failed to add device.");
    } finally {
      setAdding(false);
    }
  };

  const handleDelete = async (deviceId: string) => {
    if (!confirm(`Remove device "${deviceId}"? The Pi will no longer be able to connect.`)) return;
    try {
      await call({ url: `/api/admin/devices/${deviceId}`, method: "DELETE" });
      setDevices((prev) => prev.filter((d) => d.deviceId !== deviceId));
    } catch {
      alert("Failed to delete device.");
    }
  };

  const copyCommand = async (device: Device) => {
    if (!device.deviceKey) return;
    const cmd = buildInstallCommand(apiBase, device.deviceId, device.deviceKey, sp);
    await navigator.clipboard.writeText(cmd);
    setCopiedId(device.deviceId);
    setTimeout(() => setCopiedId(null), 2000);
  };

  return (
    <div className="content">
      <div className="card">
        <div className="card-header">
          <h2 className="card-title">Pi Devices</h2>
        </div>
        <p className="muted" style={{ marginBottom: 16 }}>
          Register a Raspberry Pi by entering its serial number. Copy the install command and run it
          on the Pi to complete setup.
        </p>

        {/* Add device form */}
        <div className="card" style={{ background: "rgba(11,111,147,0.04)", marginBottom: 20 }}>
          <div className="form-row" style={{ alignItems: "end", gap: 10 }}>
            <div>
              <label className="muted" style={{ display: "block", marginBottom: 4 }}>
                Pi serial number
              </label>
              <input
                className="input"
                placeholder="e.g. 10000000abcdef12"
                value={newDeviceId}
                onChange={(e) => setNewDeviceId(e.target.value)}
                onKeyDown={(e) => e.key === "Enter" && handleAdd()}
              />
              <span className="muted" style={{ fontSize: 12 }}>
                Run <code>grep Serial /proc/cpuinfo</code> on the Pi
              </span>
            </div>
            <div>
              <label className="muted" style={{ display: "block", marginBottom: 4 }}>
                Display name (optional)
              </label>
              <input
                className="input"
                placeholder="e.g. Beech Wing — Room 4"
                value={newDisplayName}
                onChange={(e) => setNewDisplayName(e.target.value)}
                onKeyDown={(e) => e.key === "Enter" && handleAdd()}
              />
            </div>
            <button className="btn primary" onClick={handleAdd} disabled={adding}>
              {adding ? "Adding…" : "Add Device"}
            </button>
          </div>
          {addError && <p style={{ color: "var(--error, #c0392b)", marginTop: 8 }}>{addError}</p>}
        </div>

        {/* SharePoint / OneDrive credentials */}
        <details style={{ marginBottom: 20 }}>
          <summary style={{ cursor: "pointer", fontWeight: 600, marginBottom: 8 }}>
            SharePoint / OneDrive credentials{" "}
            <span className="muted" style={{ fontWeight: 400, fontSize: 13 }}>
              (optional — needed for survey spreadsheet sync)
            </span>
          </summary>
          <div className="card" style={{ background: "rgba(11,111,147,0.04)", marginTop: 8 }}>
            <div className="form-row" style={{ flexWrap: "wrap", gap: 10 }}>
              <div>
                <label className="muted" style={{ display: "block", marginBottom: 4 }}>Tenant ID</label>
                <input className="input" placeholder="xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx" value={sp.tenantId} onChange={setSPField("tenantId")} />
              </div>
              <div>
                <label className="muted" style={{ display: "block", marginBottom: 4 }}>Client ID</label>
                <input className="input" placeholder="xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx" value={sp.clientId} onChange={setSPField("clientId")} />
              </div>
              <div>
                <label className="muted" style={{ display: "block", marginBottom: 4 }}>Client Secret</label>
                <input className="input" type="password" placeholder="Azure app secret" value={sp.clientSecret} onChange={setSPField("clientSecret")} />
              </div>
              <div>
                <label className="muted" style={{ display: "block", marginBottom: 4 }}>Drive ID</label>
                <input className="input" placeholder="b!xxxx..." value={sp.driveId} onChange={setSPField("driveId")} />
              </div>
              <div>
                <label className="muted" style={{ display: "block", marginBottom: 4 }}>Item ID <span className="muted">(preferred)</span></label>
                <input className="input" placeholder="01XXXX..." value={sp.itemId} onChange={setSPField("itemId")} />
              </div>
              <div>
                <label className="muted" style={{ display: "block", marginBottom: 4 }}>Item Path <span className="muted">(if no Item ID)</span></label>
                <input className="input" placeholder="/BCH/survey.xlsx" value={sp.itemPath} onChange={setSPField("itemPath")} />
              </div>
            </div>
            <p className="muted" style={{ marginTop: 8, fontSize: 12 }}>
              These values are embedded in the install command and written to <code>/etc/media-button/env</code> on the Pi.
            </p>
          </div>
        </details>

        {/* Device list */}
        {loading && <p className="muted">Loading…</p>}
        {error && <p style={{ color: "var(--error, #c0392b)" }}>{error}</p>}
        {!loading && !error && devices.length === 0 && (
          <p className="muted">No devices registered yet.</p>
        )}
        {devices.length > 0 && (
          <table className="table">
            <thead>
              <tr>
                <th>Device ID (serial)</th>
                <th>Name</th>
                <th>Playlist</th>
                <th>Install command</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              {devices.map((d) => (
                <tr key={d.deviceId}>
                  <td>
                    <code style={{ fontSize: 13 }}>{d.deviceId}</code>
                  </td>
                  <td>{d.displayName ?? <span className="muted">—</span>}</td>
                  <td>{d.playlistName ?? <span className="muted">None assigned</span>}</td>
                  <td>
                    {d.deviceKey ? (
                      <button
                        className="btn ghost"
                        style={{ fontSize: 13, padding: "6px 12px" }}
                        onClick={() => copyCommand(d)}
                      >
                        {copiedId === d.deviceId ? "Copied!" : "Copy install command"}
                      </button>
                    ) : (
                      <span className="muted">No key (legacy device)</span>
                    )}
                  </td>
                  <td>
                    <button
                      className="btn ghost"
                      style={{ fontSize: 13, padding: "6px 12px", color: "var(--error, #c0392b)" }}
                      onClick={() => handleDelete(d.deviceId)}
                    >
                      Remove
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>

      {/* Install command preview for newly added device */}
      {devices.length > 0 && devices[devices.length - 1]?.deviceKey && (
        <div className="card" style={{ fontFamily: "monospace", fontSize: 13 }}>
          <p className="muted" style={{ marginBottom: 8 }}>
            Last generated install command — copy and run on the Pi:
          </p>
          <pre
            style={{
              background: "rgba(0,0,0,0.04)",
              padding: 12,
              borderRadius: 8,
              overflowX: "auto",
              margin: 0,
            }}
          >
            {buildInstallCommand(apiBase, devices[devices.length - 1].deviceId, devices[devices.length - 1].deviceKey!, sp)}
          </pre>
        </div>
      )}
    </div>
  );
}
