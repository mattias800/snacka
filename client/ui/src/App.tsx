import { useEffect, useMemo, useRef, useState } from "react";
import { createApi, ok, type Auth, type Schemas } from "./api/client";
import { StreamTest, inNativeApp } from "./StreamTest";
import { Bench } from "./Bench";
import { Chat } from "./chat/Chat";
import "./App.css";

const SERVER_KEY = "snacka.serverUrl";
const AUTH_KEY = "snacka.auth";

function load<T>(key: string): T | null {
  try {
    const raw = localStorage.getItem(key);
    return raw ? (JSON.parse(raw) as T) : null;
  } catch {
    return null;
  }
}

function save(key: string, value: unknown): void {
  try {
    if (value === null) localStorage.removeItem(key);
    else localStorage.setItem(key, JSON.stringify(value));
  } catch {
    /* storage unavailable (private mode): stay logged in for this session only */
  }
}

/** `#stream-test-<port>` opens the native video surface test directly (used by automated runs). */
function useStreamTestHash(): number | null {
  const parse = () => {
    const m = /^#stream-test-(\d+)$/.exec(window.location.hash);
    return m ? Number(m[1]) : null;
  };
  const [port, setPort] = useState<number | null>(parse);
  useEffect(() => {
    const onHash = () => setPort(parse());
    window.addEventListener("hashchange", onHash);
    return () => window.removeEventListener("hashchange", onHash);
  }, []);
  return port;
}

export default function App() {
  const streamTestPort = useStreamTestHash();
  const [serverUrl, setServerUrl] = useState<string>(load<string>(SERVER_KEY) ?? "http://localhost:5117");
  const [auth, setAuth] = useState<Auth | null>(load<Auth>(AUTH_KEY));
  const tokenRef = useRef<string | null>(auth?.accessToken ?? null);
  tokenRef.current = auth?.accessToken ?? null;
  const api = useMemo(() => createApi(serverUrl, () => tokenRef.current), [serverUrl]);

  const onAuth = (a: Auth | null) => {
    setAuth(a);
    save(AUTH_KEY, a);
    save(SERVER_KEY, serverUrl);
  };

  if (window.location.hash === "#bench") return <Bench />;

  if (streamTestPort !== null && inNativeApp) {
    return (
      <main className="messages" style={{ height: "100vh" }}>
        <StreamTest autoStartPort={streamTestPort} />
      </main>
    );
  }

  return auth ? (
    <Chat serverUrl={serverUrl} api={api} auth={auth} tokenRef={tokenRef} onLogout={() => onAuth(null)} />
  ) : (
    <Login serverUrl={serverUrl} setServerUrl={setServerUrl} api={api} onAuth={onAuth} />
  );
}

function Login(props: {
  serverUrl: string;
  setServerUrl: (s: string) => void;
  api: ReturnType<typeof createApi>;
  onAuth: (a: Auth) => void;
}) {
  const { api } = props;
  const [mode, setMode] = useState<"login" | "register">("login");
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [username, setUsername] = useState("");
  const [invite, setInvite] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [info, setInfo] = useState<Schemas["SetupServerInfoResponse"] | null>(null);

  useEffect(() => {
    let cancelled = false;
    api
      .GET("/api/Auth/server-info")
      .then(ok)
      .then((i) => {
        if (cancelled) return;
        setInfo(i);
        setError(null);
        if (!i.hasUsers) setMode("register");
      })
      .catch((e: Error) => !cancelled && setError(`Can't reach server: ${e.message}`));
    return () => {
      cancelled = true;
    };
  }, [api]);

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    setError(null);
    try {
      const result =
        mode === "login"
          ? await api.POST("/api/Auth/login", { body: { email, password } })
          : await api.POST("/api/Auth/register", {
              body: { email, password, username, inviteCode: invite || null },
            });
      props.onAuth(ok(result));
    } catch (err) {
      setError((err as Error).message);
    }
  };

  return (
    <form className="login" onSubmit={submit}>
      <h1>Snacka</h1>
      <label>
        Server
        <input value={props.serverUrl} onChange={(e) => props.setServerUrl(e.target.value)} />
      </label>
      {info && !info.hasUsers && (
        <p className="muted">New server: the first account becomes admin. The invite code is printed in the server console.</p>
      )}
      {mode === "register" && (
        <label>
          Username
          <input value={username} onChange={(e) => setUsername(e.target.value)} required />
        </label>
      )}
      <label>
        Email
        <input type="email" value={email} onChange={(e) => setEmail(e.target.value)} required />
      </label>
      <label>
        Password
        <input type="password" value={password} onChange={(e) => setPassword(e.target.value)} required />
      </label>
      {mode === "register" && (
        <label>
          Invite code
          <input value={invite} onChange={(e) => setInvite(e.target.value)} />
        </label>
      )}
      {error && <p className="error">{error}</p>}
      <button type="submit">{mode === "login" ? "Log in" : "Create account"}</button>
      <button type="button" className="link" onClick={() => setMode(mode === "login" ? "register" : "login")}>
        {mode === "login" ? "Create an account" : "I already have an account"}
      </button>
    </form>
  );
}
