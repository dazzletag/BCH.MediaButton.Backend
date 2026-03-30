import { useState } from "react";
import { Routes, Route, useNavigate, useLocation } from "react-router-dom";
import { InteractionStatus } from "@azure/msal-browser";
import { useIsAuthenticated, useMsal } from "@azure/msal-react";
import Dashboard from "./views/Dashboard";
import Devices from "./views/Devices";
import CarePlanViewer from "./views/CarePlanViewer";
import { loginRequest } from "./msalConfig";
import "./styles/layout.css";

type Tab = "media" | "devices" | "care-plans";

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

function AuthenticatedHome() {
  const { instance, accounts } = useMsal();
  const navigate = useNavigate();
  const location = useLocation();
  const [tab, setTab] = useState<Tab>(
    location.pathname.startsWith("/care-plans") ? "care-plans" : "media"
  );

  const signOut = () => instance.logoutRedirect();
  const accountName = accounts[0]?.name ?? "Signed in";

  const switchTab = (t: Tab) => {
    setTab(t);
    if (t === "care-plans") navigate("/care-plans");
    else if (t === "devices") navigate("/");
    else navigate("/");
  };

  return (
    <div className="page">
      <header className="nav">
        <div className="brand">
          <img src="/bch-logo.svg" alt="Bristol Care Homes" className="brand-logo" />
          <div className="brand-text">
            <span className="brand-line">HomeLight</span>
            <span className="brand-subline">BCH Systems</span>
          </div>
        </div>
        <nav style={{ display: "flex", gap: 4 }}>
          <button
            className={`btn ${tab === "media" ? "primary" : "ghost"}`}
            style={{ borderRadius: 10 }}
            onClick={() => switchTab("media")}
          >
            Media &amp; Playlists
          </button>
          <button
            className={`btn ${tab === "devices" ? "primary" : "ghost"}`}
            style={{ borderRadius: 10 }}
            onClick={() => switchTab("devices")}
          >
            Devices
          </button>
          <button
            className={`btn ${tab === "care-plans" ? "primary" : "ghost"}`}
            style={{ borderRadius: 10 }}
            onClick={() => switchTab("care-plans")}
          >
            Care Plans
          </button>
        </nav>
        <div className="nav-actions">
          <span className="muted" style={{ fontSize: 14 }}>{accountName}</span>
          <button className="btn ghost" onClick={signOut}>Sign out</button>
        </div>
      </header>
      {tab === "media" && <Dashboard />}
      {tab === "devices" && <Devices />}
      {tab === "care-plans" && <CarePlanLanding />}
    </div>
  );
}

function CarePlanLanding() {
  const navigate = useNavigate();
  const [residentId, setResidentId] = useState("");

  const handleGo = () => {
    if (residentId.trim()) {
      navigate(`/care-plans/${encodeURIComponent(residentId.trim())}/personal_information`);
    }
  };

  return (
    <div className="content" style={{ maxWidth: 600, margin: "0 auto", paddingTop: 32 }}>
      <div className="card">
        <h2 className="section-title">Care Plans</h2>
        <p className="muted" style={{ marginBottom: 16 }}>
          Enter a resident name to view or edit their care plan.
        </p>
        <div style={{ display: "flex", gap: 10 }}>
          <input
            className="input"
            type="text"
            placeholder="Resident name (e.g. Margaret Smith)"
            value={residentId}
            onChange={(e) => setResidentId(e.target.value)}
            onKeyDown={(e) => e.key === "Enter" && handleGo()}
          />
          <button className="btn primary" onClick={handleGo} disabled={!residentId.trim()}>
            Open
          </button>
        </div>
      </div>
    </div>
  );
}

function AuthenticatedApp() {
  return (
    <Routes>
      <Route path="/care-plans/:residentId/:section" element={<CarePlanViewer mode="manager" />} />
      <Route path="/care-plans/:residentId" element={<CarePlanViewer mode="manager" />} />
      <Route path="/carer/:residentId/:section" element={<CarePlanViewer mode="carer" />} />
      <Route path="/carer/:residentId" element={<CarePlanViewer mode="carer" />} />
      <Route path="*" element={<AuthenticatedHome />} />
    </Routes>
  );
}

export default function App() {
  const isAuthed = useIsAuthenticated();
  return isAuthed ? <AuthenticatedApp /> : <Landing />;
}
