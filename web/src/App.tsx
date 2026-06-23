import { useEffect, useState } from "react";
import { InteractionStatus } from "@azure/msal-browser";
import { useIsAuthenticated, useMsal } from "@azure/msal-react";
import Dashboard from "./views/Dashboard";
import Devices from "./views/Devices";
import Users from "./views/Users";
import { useApiClient } from "./hooks/useApiClient";
import { loginRequest } from "./msalConfig";
import "./styles/layout.css";

type Tab = "media" | "devices" | "users";

function Landing() {
  const { instance, inProgress } = useMsal();
  const isBusy = inProgress !== InteractionStatus.None;

  const beginSignIn = () => {
    if (isBusy) return;
    instance.loginRedirect(loginRequest);
  };

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
          <button className="btn primary" onClick={beginSignIn} disabled={isBusy}>
            {isBusy ? "Starting sign-in." : "Sign in to manage"}
          </button>
        </div>
      </header>

      <section className="hero">
        <div className="hero-left">
          <div className="hero-eyebrow">Staff & Relatives</div>
          <h1 className="hero-title">
            Curate calming playlists for every home with a few clicks.
          </h1>
          <p className="hero-copy">
            Log in with your Bristol Care Homes account to upload photos and videos, assemble
            playlists, and assign them to each Media Button. The Pi pulls only what you approve,
            keeping residents' moments private, safe, and simple.
          </p>
          <div className="pill brand-pill">Providing top quality, best value, holistic care.</div>
          <div className="nav-actions">
            <button className="btn primary" onClick={beginSignIn} disabled={isBusy}>
              {isBusy ? "Starting sign-in." : "Continue with Bristol Care Homes"}
            </button>
            <button className="btn ghost" onClick={beginSignIn} disabled={isBusy}>
              {isBusy ? "Please wait." : "I am a relative"}
            </button>
          </div>
        </div>

        <div className="hero-right">
          <div className="logo-card glass">
            <img src="/press-play-hero.svg" alt="Press & Play Media Button" className="hero-logo" />
            <p className="hero-mini">
              Press &amp; Play Media Button keeps residents' memories safe, simple, and ready to enjoy.
            </p>
          </div>
        </div>
      </section>
    </div>
  );
}

function AuthenticatedApp() {
  const { instance, accounts } = useMsal();
  const { call } = useApiClient();
  const [tab, setTab] = useState<Tab>("media");
  const [isAdmin, setIsAdmin] = useState(false);

  const signOut = () => instance.logoutRedirect();
  const account = accounts[0];
  const accountName = account?.name ?? "Signed in";

  useEffect(() => {
    call<{ isAdmin: boolean }>({ url: "/api/admin/me", method: "GET" })
      .then((res) => setIsAdmin(res.isAdmin))
      .catch(() => setIsAdmin(false));
  }, [call]);

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
        <nav style={{ display: "flex", gap: 4 }}>
          <button
            className={`btn ${tab === "media" ? "primary" : "ghost"}`}
            style={{ borderRadius: 10 }}
            onClick={() => setTab("media")}
          >
            Media &amp; Playlists
          </button>
          <button
            className={`btn ${tab === "devices" ? "primary" : "ghost"}`}
            style={{ borderRadius: 10 }}
            onClick={() => setTab("devices")}
          >
            Devices
          </button>
          {isAdmin && (
            <button
              className={`btn ${tab === "users" ? "primary" : "ghost"}`}
              style={{ borderRadius: 10 }}
              onClick={() => setTab("users")}
            >
              Users
            </button>
          )}
        </nav>
        <div className="nav-actions">
          <span className="muted" style={{ fontSize: 14 }}>{accountName}</span>
          <button className="btn ghost" onClick={signOut}>Sign out</button>
        </div>
      </header>
      {tab === "media" && <Dashboard isAdmin={isAdmin} />}
      {tab === "devices" && <Devices />}
      {tab === "users" && <Users />}
    </div>
  );
}

export default function App() {
  const isAuthed = useIsAuthenticated();
  return isAuthed ? <AuthenticatedApp /> : <Landing />;
}
