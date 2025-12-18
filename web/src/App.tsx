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
          <div className="brand-mark">B</div>
          Bristol Care Homes • Media
        </div>
        <div className="nav-actions">
          <button className="btn primary" onClick={beginSignIn} disabled={isBusy}>
            {isBusy ? "Starting sign-in…" : "Sign in to manage"}
          </button>
        </div>
      </header>

      <section className="hero">
        <div className="hero-eyebrow">Staff & Relatives</div>
        <h1 className="hero-title">
          Curate calming playlists for every home with a few clicks.
        </h1>
        <p className="hero-copy">
          Log in with your Bristol Care Homes account to upload photos and
          videos, assemble playlists, and assign them to each Media Button. The
          Pi pulls only what you approve, keeping residents’ moments private,
          safe, and simple.
        </p>
        <div className="nav-actions">
          <button className="btn primary" onClick={beginSignIn} disabled={isBusy}>
            {isBusy ? "Starting sign-in…" : "Continue with Bristol Care Homes"}
          </button>
          <button className="btn ghost" onClick={beginSignIn} disabled={isBusy}>
            {isBusy ? "Please wait…" : "I am a relative"}
          </button>
        </div>
      </section>
    </div>
  );
}

export default function App() {
  const isAuthed = useIsAuthenticated();
  return isAuthed ? <Dashboard /> : <Landing />;
}
