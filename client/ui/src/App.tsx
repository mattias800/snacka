import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import type { HubConnection } from "@microsoft/signalr";
import { createApi, ok, type Auth, type Schemas } from "./api/client";
import { createHub } from "./api/hub";
import { StreamTest, inNativeApp } from "./StreamTest";
import { Bench } from "./Bench";
import { MessageList } from "./MessageList";
import { VoicePanel, useVoice } from "./VoicePanel";
import "./App.css";

type Community = Schemas["CommunityResponse"];
type Channel = Schemas["ChannelResponse"];
type Message = Schemas["MessageResponse"];
type VoiceParticipant = Schemas["VoiceParticipantResponse"];

/** Mirrors Snacka.Shared ChannelType (serialized as a number). */
const ChannelKind = { Text: 0, Voice: 1 } as const;

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

function Chat(props: {
  serverUrl: string;
  api: ReturnType<typeof createApi>;
  auth: Auth;
  tokenRef: React.RefObject<string | null>;
  onLogout: () => void;
}) {
  const { api, serverUrl, tokenRef } = props;
  const [communities, setCommunities] = useState<Community[]>([]);
  const [community, setCommunity] = useState<Community | null>(null);
  const [channels, setChannels] = useState<Channel[]>([]);
  const [channel, setChannel] = useState<Channel | null>(null);
  const [messages, setMessages] = useState<Message[]>([]);
  const [draft, setDraft] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [hubState, setHubState] = useState("connecting");
  const [showStreamTest, setShowStreamTest] = useState(false);
  const voice = useVoice(api);
  // Who is in which voice channel of the open community (LiveKit webhooks -> server -> hub).
  const [presence, setPresence] = useState<VoiceParticipant[]>([]);
  const communityRef = useRef<Community | null>(null);
  communityRef.current = community;
  const hubRef = useRef<HubConnection | null>(null);
  const channelRef = useRef<Channel | null>(null);
  channelRef.current = channel;

  const run = useCallback(async (f: () => Promise<void>) => {
    try {
      setError(null);
      await f();
    } catch (e) {
      setError((e as Error).message);
    }
  }, []);

  const loadCommunities = useCallback(
    () => run(async () => setCommunities(ok(await api.GET("/api/communities")))),
    [api, run],
  );

  const loadPresence = useCallback(
    async (communityId: string) => {
      const list = ok(await api.GET("/api/voice/communities/{communityId}/participants", { params: { path: { communityId } } }));
      if (communityRef.current?.id === communityId) setPresence(list);
    },
    [api],
  );

  // One hub connection per session; live messages go to the open channel.
  useEffect(() => {
    const hub = createHub(serverUrl, () => tokenRef.current);
    hubRef.current = hub;
    hub.on("ReceiveChannelMessage", (m: Message) => {
      if (m.channelId !== channelRef.current?.id) return;
      setMessages((prev) => (prev.some((p) => p.id === m.id) ? prev : [...prev, m]));
    });
    hub.on("VoiceParticipantJoined", (e: { channelId: string; participant: VoiceParticipant }) =>
      setPresence((prev) => [...prev.filter((p) => p.userId !== e.participant.userId), e.participant]),
    );
    hub.on("VoiceParticipantLeft", (e: { channelId: string; userId: string }) =>
      setPresence((prev) => prev.filter((p) => !(p.userId === e.userId && p.channelId === e.channelId))),
    );
    hub.onreconnecting(() => setHubState("reconnecting"));
    hub.onreconnected(() => {
      setHubState("connected");
      // Events may have been missed while disconnected.
      const c = communityRef.current;
      if (c) void loadPresence(c.id).catch(() => {});
    });
    hub.onclose(() => setHubState("disconnected"));
    hub
      .start()
      .then(() => setHubState("connected"))
      .catch((e: Error) => setHubState(`failed: ${e.message}`));
    return () => {
      void hub.stop();
    };
  }, [serverUrl, tokenRef, loadPresence]);

  useEffect(() => {
    void loadCommunities();
  }, [loadCommunities]);

  const openCommunity = (c: Community) =>
    run(async () => {
      setCommunity(c);
      communityRef.current = c;
      setChannel(null);
      setMessages([]);
      setPresence([]);
      void loadPresence(c.id).catch(() => {});
      const list = ok(await api.GET("/api/communities/{communityId}/Channels", { params: { path: { communityId: c.id } } }));
      setChannels(list);
      if (hubRef.current?.state === "Connected") await hubRef.current.invoke("JoinServer", c.id);
      const firstText = list.find((ch) => ch.type === ChannelKind.Text);
      if (firstText) await openChannel(firstText);
    });

  const openChannel = (ch: Channel) =>
    run(async () => {
      const previous = channelRef.current;
      setChannel(ch);
      channelRef.current = ch;
      const hub = hubRef.current;
      if (hub?.state === "Connected") {
        if (previous) await hub.invoke("LeaveChannel", previous.id);
        await hub.invoke("JoinChannel", ch.id);
      }
      setMessages(ok(await api.GET("/api/channels/{channelId}/messages", { params: { path: { channelId: ch.id } } })));
    });

  const createCommunity = () =>
    run(async () => {
      const name = prompt("Community name");
      if (!name) return;
      const c = ok(await api.POST("/api/communities", { body: { name, description: null } }));
      await loadCommunities();
      await openCommunity(c);
    });

  const send = (e: React.FormEvent) => {
    e.preventDefault();
    const content = draft.trim();
    if (!content || !channel) return;
    setDraft("");
    void run(async () => {
      const m = ok(
        await api.POST("/api/channels/{channelId}/messages", {
          params: { path: { channelId: channel.id } },
          body: { content, replyToId: null },
        }),
      );
      setMessages((prev) => (prev.some((p) => p.id === m.id) ? prev : [...prev, m]));
    });
  };

  return (
    <div className="chat">
      <nav className="communities">
        {communities.map((c) => (
          <button key={c.id} className={c.id === community?.id ? "active" : ""} title={c.name} onClick={() => void openCommunity(c)}>
            {c.name.slice(0, 2).toUpperCase()}
          </button>
        ))}
        <button title="Create community" onClick={() => void createCommunity()}>+</button>
      </nav>
      <aside className="channels">
        <header>{community?.name ?? "Snacka"}</header>
        {channels.map((ch) =>
          ch.type === ChannelKind.Text ? (
            <button key={ch.id} className={ch.id === channel?.id ? "active" : ""} onClick={() => void openChannel(ch)}>
              # {ch.name}
            </button>
          ) : (
            <div key={ch.id}>
              <button
                className={ch.id === voice.channelId ? "active" : ""}
                data-testid="voice-channel"
                onClick={() => void (ch.id === voice.channelId ? voice.leave() : voice.join(ch.id))}
              >
                🔊 {ch.name}
              </button>
              {ch.id === voice.channelId ? (
                <VoicePanel voice={voice} />
              ) : (
                <ul className="voice-presence">
                  {presence
                    .filter((p) => p.channelId === ch.id)
                    .map((p) => (
                      <li key={p.userId} data-testid="voice-presence" data-name={p.username}>
                        <span className="avatar">{p.username.slice(0, 1).toUpperCase()}</span>
                        {p.username}
                      </li>
                    ))}
                </ul>
              )}
            </div>
          ),
        )}
        <footer>
          <span>{props.auth.username}</span>
          <span className={`dot ${hubState}`} title={`Realtime: ${hubState}`} />
          {inNativeApp && (
            <button className="link" title="Native video surface test (spike #51)" onClick={() => setShowStreamTest((v) => !v)}>
              📺
            </button>
          )}
          <button className="link" onClick={props.onLogout}>Log out</button>
        </footer>
      </aside>
      {showStreamTest ? (
        <main className="messages">
          <header>Stream test</header>
          <StreamTest />
        </main>
      ) : (
      <main className="messages">
        <header>{channel ? `# ${channel.name}` : "Pick a community"}</header>
        {error && <p className="error">{error}</p>}
        <MessageList messages={messages} />
        {channel && (
          <form onSubmit={send}>
            <input value={draft} onChange={(e) => setDraft(e.target.value)} placeholder={`Message #${channel.name}`} autoFocus />
          </form>
        )}
      </main>
      )}
    </div>
  );
}
