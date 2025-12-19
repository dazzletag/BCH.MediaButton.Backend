import { InteractionStatus } from "@azure/msal-browser";
import { useIsAuthenticated, useMsal } from "@azure/msal-react";
import Dashboard from "./views/Dashboard";
import { loginRequest } from "./msalConfig";
import "./styles/layout.css";

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
          <img src="/press-play-logo.svg" alt="Press & Play" className="press-play-logo" />
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
            <img src="/bch-logo.svg" alt="Bristol Care Homes logo" className="hero-logo" />
            <p className="hero-mini">
              Four colours, one promise: calm technology that keeps every resident connected to
              cherished memories.
            </p>
            <div className="logo-swatches">
              <span style={{ background: "var(--teal)" }} />
              <span style={{ background: "var(--berry)" }} />
              <span style={{ background: "var(--plum)" }} />
              <span style={{ background: "var(--olive)" }} />
            </div>
          </div>
        </div>
      </section>
    </div>
  );
}

export default function App() {
  const isAuthed = useIsAuthenticated();
  return isAuthed ? <Dashboard /> : <Landing />;
}
