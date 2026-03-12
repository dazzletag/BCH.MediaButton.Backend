import { useCallback, useEffect, useState } from "react";
import { useApiClient } from "../hooks/useApiClient";
import type { Device } from "../types";

const INSTALL_BASE =
  "https://raw.githubusercontent.com/dazzletag/BCH.MediaButton.Backend/main/publish/pi/install.sh";

function buildInstallCommand(apiBase: string, key: string) {
  return `curl -sSL ${INSTALL_BASE} \\\n  | sudo bash -s -- \\\n      --api ${apiBase} \\\n      --key "${key}"`;
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
    const cmd = buildInstallCommand(apiBase, device.deviceKey);
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
            {buildInstallCommand(apiBase, devices[devices.length - 1].deviceKey!)}
          </pre>
        </div>
      )}
    </div>
  );
}
