import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import type { HubConnection } from "@microsoft/signalr";
import { ok, type Api, type Auth, type Schemas } from "../api/client";
import { createHub } from "../api/hub";
import { MessageList, type MessageActions } from "../MessageList";
import { StreamTest, inNativeApp } from "../StreamTest";
import { VoicePanel, useVoice } from "../VoicePanel";
import { BrowseCommunities, InviteFriends } from "./Onboarding";
import { applyReaction, fromChannel, fromDm, upsert, type ChannelMessage, type ChatMessage, type DmMessage, type ReactionUpdated } from "./model";

type Community = Schemas["CommunityResponse"];
type Channel = Schemas["ChannelResponse"];
type Member = Schemas["CommunityMemberResponse"];
type Conversation = Schemas["ConversationResponse"];
type VoiceParticipant = Schemas["VoiceParticipantResponse"];

/** Mirrors Snacka.Shared ChannelType and UserRole (serialized as numbers). */
const ChannelKind = { Text: 0, Voice: 1 } as const;
const Role = { Owner: 0, Admin: 1, Moderator: 2, Member: 3 } as const;

/** What the message pane shows: a text channel of the open community, or a DM conversation. */
type View = { kind: "channel"; channel: Channel } | { kind: "dm"; conversation: Conversation } | null;

/** Name of a conversation as the current user sees it: the group name, or the other people. */
function conversationName(c: Conversation, myId: string): string {
  if (c.name) return c.name;
  const others = c.participants.filter((p) => p.userId !== myId).map((p) => p.effectiveDisplayName);
  return others.join(", ") || "Just you";
}

export function Chat(props: { serverUrl: string; api: Api; auth: Auth; tokenRef: React.RefObject<string | null>; onLogout: () => void }) {
  const { api, serverUrl, tokenRef, auth } = props;
  const myId = auth.userId;
  const [communities, setCommunities] = useState<Community[]>([]);
  /** null = Home (direct messages). */
  const [community, setCommunity] = useState<Community | null>(null);
  const [channels, setChannels] = useState<Channel[]>([]);
  const [members, setMembers] = useState<Member[]>([]);
  const [conversations, setConversations] = useState<Conversation[]>([]);
  const [view, setView] = useState<View>(null);
  const [messages, setMessages] = useState<ChatMessage[]>([]);
  const [draft, setDraft] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [hubState, setHubState] = useState("connecting");
  const [showStreamTest, setShowStreamTest] = useState(false);
  const [replyingTo, setReplyingTo] = useState<ChatMessage | null>(null);
  /** The view whose realtime events we're receiving (joined its channel group while connected). */
  const [liveView, setLiveView] = useState<View>(null);
  /** Who is typing in the open channel/DM, and until when (ms) to show it. */
  const [typing, setTyping] = useState<Record<string, { name: string; until: number }>>({});
  const lastTypingSentRef = useRef(0);
  /** Replaces the message pane: joining/creating communities, or inviting friends. */
  const [panel, setPanel] = useState<"browse" | "invite" | null>(null);
  const voice = useVoice(api);
  // Who is in which voice channel of the open community (LiveKit webhooks -> server -> hub).
  const [presence, setPresence] = useState<VoiceParticipant[]>([]);
  const communityRef = useRef<Community | null>(null);
  const viewRef = useRef<View>(null);
  const hubRef = useRef<HubConnection | null>(null);
  const openViewRef = useRef<(v: View) => Promise<void>>(async () => {});
  useEffect(() => {
    communityRef.current = community;
    viewRef.current = view;
  });

  const run = useCallback(async (f: () => Promise<void>) => {
    try {
      setError(null);
      await f();
    } catch (e) {
      setError((e as Error).message);
    }
  }, []);

  const loadCommunities = useCallback(() => run(async () => setCommunities(ok(await api.GET("/api/communities")))), [api, run]);
  const loadConversations = useCallback(async () => setConversations(ok(await api.GET("/api/conversations"))), [api]);

  const loadPresence = useCallback(
    async (communityId: string) => {
      const list = ok(await api.GET("/api/voice/communities/{communityId}/participants", { params: { path: { communityId } } }));
      if (communityRef.current?.id === communityId) setPresence(list);
    },
    [api],
  );

  const isOpen = (kind: "channel" | "dm", id: string) => {
    const v = viewRef.current;
    return v?.kind === kind && (v.kind === "channel" ? v.channel.id : v.conversation.id) === id;
  };

  // One hub connection per session. Events for the open channel/DM update the message list;
  // DM events also keep the conversation list (last message, unread count) current.
  useEffect(() => {
    const hub = createHub(serverUrl, () => tokenRef.current);
    hubRef.current = hub;
    const stopTyping = (userId: string) =>
      setTyping((prev) => {
        if (!(userId in prev)) return prev;
        const next = { ...prev };
        delete next[userId];
        return next;
      });
    const startTyping = (userId: string, name: string) =>
      userId !== myId && setTyping((prev) => ({ ...prev, [userId]: { name, until: Date.now() + 5000 } }));
    hub.on("UserTyping", (e: { channelId: string; userId: string; effectiveDisplayName: string }) => isOpen("channel", e.channelId) && startTyping(e.userId, e.effectiveDisplayName));
    hub.on("ConversationUserTyping", (e: { conversationId: string; userId: string; effectiveDisplayName: string }) => isOpen("dm", e.conversationId) && startTyping(e.userId, e.effectiveDisplayName));
    // Online status of members (the hub sends this for every user; we only show members).
    const setOnline = (online: boolean) => (e: { userId: string }) => setMembers((prev) => prev.map((m) => (m.userId === e.userId ? { ...m, isOnline: online } : m)));
    hub.on("UserOnline", setOnline(true));
    hub.on("UserOffline", setOnline(false));

    hub.on("ReceiveChannelMessage", (m: ChannelMessage) => {
      if (!isOpen("channel", m.channelId)) return;
      stopTyping(m.authorId);
      setMessages((prev) => upsert(prev, fromChannel(m)));
    });
    hub.on("ChannelMessageEdited", (m: ChannelMessage) => isOpen("channel", m.channelId) && setMessages((prev) => upsert(prev, fromChannel(m))));
    hub.on("ChannelMessageDeleted", (e: { channelId: string; messageId: string }) => setMessages((prev) => prev.filter((m) => m.id !== e.messageId)));
    hub.on("ReactionUpdated", (e: ReactionUpdated) => isOpen("channel", e.channelId) && setMessages((prev) => applyReaction(prev, e, myId)));

    hub.on("ConversationMessageReceived", (m: DmMessage) => {
      const open = isOpen("dm", m.conversationId);
      if (open) {
        stopTyping(m.senderId);
        setMessages((prev) => upsert(prev, fromDm(m)));
      }
      setConversations((prev) => {
        if (!prev.some((c) => c.id === m.conversationId)) {
          void loadConversations().catch(() => {}); // a conversation we haven't seen yet
          return prev;
        }
        return prev.map((c) =>
          c.id === m.conversationId
            ? { ...c, lastMessage: m, unreadCount: open || m.senderId === myId ? c.unreadCount : c.unreadCount + 1 }
            : c,
        );
      });
      if (open && m.senderId !== myId) void api.POST("/api/conversations/{conversationId}/read", { params: { path: { conversationId: m.conversationId } } });
    });
    hub.on("ConversationMessageUpdated", (m: DmMessage) => isOpen("dm", m.conversationId) && setMessages((prev) => upsert(prev, fromDm(m))));
    hub.on("ConversationMessageDeleted", (e: { conversationId: string; messageId: string }) => setMessages((prev) => prev.filter((m) => m.id !== e.messageId)));
    hub.on("ConversationCreated", () => void loadConversations().catch(() => {}));

    hub.on("CommunityMemberAdded", (communityId: string) => {
      if (communityRef.current?.id !== communityId) return;
      void api.GET("/api/communities/{communityId}/members", { params: { path: { communityId } } }).then(ok).then(setMembers).catch(() => {});
    });
    hub.on("VoiceParticipantJoined", (e: { channelId: string; participant: VoiceParticipant }) =>
      setPresence((prev) => [...prev.filter((p) => p.userId !== e.participant.userId), e.participant]),
    );
    hub.on("VoiceParticipantLeft", (e: { channelId: string; userId: string }) =>
      setPresence((prev) => prev.filter((p) => !(p.userId === e.userId && p.channelId === e.channelId))),
    );
    hub.onreconnecting(() => {
      setHubState("reconnecting");
      setLiveView(null);
    });
    hub.onreconnected(() => {
      setHubState("connected");
      // Events may have been missed while disconnected: refetch what's on screen.
      const c = communityRef.current;
      if (c) void loadPresence(c.id).catch(() => {});
      void loadConversations().catch(() => {});
      const v = viewRef.current;
      if (v) void openViewRef.current(v);
    });
    hub.onclose(() => setHubState("disconnected"));
    hub
      .start()
      .then(() => {
        setHubState("connected");
        // A channel opened before the connection was up never joined its realtime group
        // (and may have missed messages): open it again now.
        const v = viewRef.current;
        if (v) void openViewRef.current(v);
        const c = communityRef.current;
        if (c) void hub.invoke("JoinServer", c.id).catch(() => {});
      })
      .catch((e: Error) => setHubState(`failed: ${e.message}`));
    return () => {
      void hub.stop();
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps -- one connection per server/session
  }, [serverUrl, tokenRef, myId]);

  useEffect(() => {
    void loadCommunities();
    void loadConversations().catch(() => {});
  }, [loadCommunities, loadConversations]);

  /** Shows a channel or DM: joins its realtime group (channels) and loads its history. */
  const openView = (next: View) =>
    run(async () => {
      const previous = viewRef.current;
      setView(next);
      viewRef.current = next;
      setMessages([]);
      setReplyingTo(null);
      setTyping({});
      setLiveView(null);
      if (!next) return;
      const hub = hubRef.current;
      const connected = hub?.state === "Connected";
      if (next.kind === "channel") {
        if (hub && connected) {
          if (previous?.kind === "channel" && previous.channel.id !== next.channel.id) await hub.invoke("LeaveChannel", previous.channel.id);
          await hub.invoke("JoinChannel", next.channel.id);
        }
        const list = ok(await api.GET("/api/channels/{channelId}/messages", { params: { path: { channelId: next.channel.id } } }));
        if (viewRef.current === next) setMessages(list.map(fromChannel));
      } else {
        const id = next.conversation.id;
        const list = ok(await api.GET("/api/conversations/{conversationId}/messages", { params: { path: { conversationId: id } } }));
        if (viewRef.current === next) setMessages(list.map(fromDm));
        setConversations((prev) => prev.map((c) => (c.id === id ? { ...c, unreadCount: 0 } : c)));
        await api.POST("/api/conversations/{conversationId}/read", { params: { path: { conversationId: id } } });
      }
      // DMs need no join: their events go to the participants directly.
      if (connected && viewRef.current === next) setLiveView(next);
    });

  useEffect(() => {
    openViewRef.current = openView;
  });

  const openCommunity = (c: Community) =>
    run(async () => {
      setPanel(null);
      setCommunity(c);
      communityRef.current = c;
      setPresence([]);
      setMembers([]);
      void loadPresence(c.id).catch(() => {});
      const [list, people] = await Promise.all([
        api.GET("/api/communities/{communityId}/Channels", { params: { path: { communityId: c.id } } }).then(ok),
        api.GET("/api/communities/{communityId}/members", { params: { path: { communityId: c.id } } }).then(ok),
      ]);
      setChannels(list);
      setMembers(people);
      if (hubRef.current?.state === "Connected") await hubRef.current.invoke("JoinServer", c.id);
      const firstText = list.find((ch) => ch.type === ChannelKind.Text);
      await openView(firstText ? { kind: "channel", channel: firstText } : null);
    });

  const openHome = () =>
    run(async () => {
      setPanel(null);
      setCommunity(null);
      communityRef.current = null;
      await loadConversations();
      await openView(null);
    });

  const messageUser = (userId: string) =>
    run(async () => {
      setPanel(null);
      const conversation = ok(await api.GET("/api/conversations/direct/{otherUserId}", { params: { path: { otherUserId: userId } } }));
      setCommunity(null);
      communityRef.current = null;
      setConversations((prev) => (prev.some((c) => c.id === conversation.id) ? prev : [conversation, ...prev]));
      await openView({ kind: "dm", conversation });
    });

  /** After joining or creating a community from the browse panel. */
  const enterCommunity = (c: Community) =>
    run(async () => {
      setCommunities(ok(await api.GET("/api/communities")));
      await openCommunity(c);
    });

  const send = (e: React.FormEvent) => {
    e.preventDefault();
    const content = draft.trim();
    const v = view;
    if (!content || !v) return;
    setDraft("");
    const replyToId = replyingTo?.id ?? null;
    setReplyingTo(null);
    lastTypingSentRef.current = 0;
    void run(async () => {
      const m =
        v.kind === "channel"
          ? fromChannel(ok(await api.POST("/api/channels/{channelId}/messages", { params: { path: { channelId: v.channel.id } }, body: { content, replyToId } })))
          : fromDm(ok(await api.POST("/api/conversations/{conversationId}/messages", { params: { path: { conversationId: v.conversation.id } }, body: { content } })));
      if (viewRef.current === v) setMessages((prev) => upsert(prev, m));
    });
  };

  /** Tells the others we're typing, at most every 3 s while the draft changes. */
  const onDraftChange = (value: string) => {
    setDraft(value);
    const hub = hubRef.current;
    const v = view;
    if (!value || !v || hub?.state !== "Connected" || Date.now() - lastTypingSentRef.current < 3000) return;
    lastTypingSentRef.current = Date.now();
    void (v.kind === "channel" ? hub.invoke("SendTyping", v.channel.id) : hub.invoke("SendConversationTyping", v.conversation.id)).catch(() => {});
  };

  // Typing indicators expire 5 s after the last event.
  const typingNames = Object.values(typing).map((t) => t.name);
  useEffect(() => {
    if (typingNames.length === 0) return;
    const timer = setInterval(() => {
      const now = Date.now();
      setTyping((prev) => Object.fromEntries(Object.entries(prev).filter(([, t]) => t.until > now)));
    }, 1000);
    return () => clearInterval(timer);
  }, [typingNames.length]);

  const me = members.find((m) => m.userId === myId);
  const actions = useMemo<MessageActions | undefined>(() => {
    const v = view;
    if (!v) return undefined;
    if (v.kind === "channel") {
      const path = (messageId: string) => ({ params: { path: { channelId: v.channel.id, messageId } } });
      return {
        myId,
        canModerate: me !== undefined && me.role !== Role.Member,
        onEdit: (id, content) =>
          void run(async () => {
            const m = ok(await api.PUT("/api/channels/{channelId}/messages/{messageId}", { ...path(id), body: { content } }));
            setMessages((prev) => upsert(prev, fromChannel(m)));
          }),
        onDelete: (id) =>
          void run(async () => {
            const res = await api.DELETE("/api/channels/{channelId}/messages/{messageId}", path(id));
            if (!res.response.ok) throw new Error(`Delete failed: ${res.response.status}`);
            setMessages((prev) => prev.filter((m) => m.id !== id));
          }),
        onReply: (m) => setReplyingTo(m),
        onReact: (id, emoji, add) =>
          void run(async () => {
            if (add) {
              const e = ok(await api.POST("/api/channels/{channelId}/messages/{messageId}/reactions", { ...path(id), body: { emoji } }));
              setMessages((prev) => applyReaction(prev, e, myId));
            } else {
              // 204: the new count arrives as a ReactionUpdated event.
              const res = await api.DELETE("/api/channels/{channelId}/messages/{messageId}/reactions/{emoji}", { params: { path: { channelId: v.channel.id, messageId: id, emoji } } });
              if (!res.response.ok) throw new Error(`Removing reaction failed: ${res.response.status}`);
            }
          }),
      };
    }
    const path = (messageId: string) => ({ params: { path: { conversationId: v.conversation.id, messageId } } });
    return {
      myId,
      canModerate: false,
      onEdit: (id, content) =>
        void run(async () => {
          const m = ok(await api.PUT("/api/conversations/{conversationId}/messages/{messageId}", { ...path(id), body: { content } }));
          setMessages((prev) => upsert(prev, fromDm(m)));
        }),
      onDelete: (id) =>
        void run(async () => {
          const res = await api.DELETE("/api/conversations/{conversationId}/messages/{messageId}", path(id));
          if (!res.response.ok) throw new Error(`Delete failed: ${res.response.status}`);
          setMessages((prev) => prev.filter((m) => m.id !== id));
        }),
    };
  }, [view, myId, me, api, run]);

  const title = !view ? (community ? community.name : "Direct messages") : view.kind === "channel" ? `# ${view.channel.name}` : `@ ${conversationName(view.conversation, myId)}`;
  const placeholder = view?.kind === "channel" ? `Message #${view.channel.name}` : view ? `Message ${conversationName(view.conversation, myId)}` : "";
  const unreadTotal = conversations.reduce((n, c) => n + c.unreadCount, 0);

  return (
    <div className="chat">
      <nav className="communities">
        <button className={community === null ? "active" : ""} title="Direct messages" data-testid="home" onClick={() => void openHome()}>
          💬{unreadTotal > 0 && <span className="badge" data-testid="unread-total">{unreadTotal}</span>}
        </button>
        <hr />
        {communities.map((c) => (
          <button key={c.id} className={c.id === community?.id ? "active" : ""} title={c.name} onClick={() => void openCommunity(c)}>
            {c.name.slice(0, 2).toUpperCase()}
          </button>
        ))}
        <button className={panel === "browse" ? "active" : ""} title="Join or create a community" data-testid="browse" onClick={() => setPanel("browse")}>+</button>
      </nav>
      <aside className="channels">
        <header>{community?.name ?? "Direct messages"}</header>
        {community === null ? (
          conversations.length === 0 ? (
            <p className="muted hint">No conversations yet. Open a community and pick someone under Members.</p>
          ) : (
            conversations.map((c) => (
              <button
                key={c.id}
                data-testid="conversation"
                data-name={conversationName(c, myId)}
                className={view?.kind === "dm" && view.conversation.id === c.id ? "active" : ""}
                onClick={() => void openView({ kind: "dm", conversation: c })}
              >
                @ {conversationName(c, myId)}
                {c.unreadCount > 0 && <span className="badge" data-testid="unread">{c.unreadCount}</span>}
              </button>
            ))
          )
        ) : (
          <>
            {channels.map((ch) =>
              ch.type === ChannelKind.Text ? (
                <button key={ch.id} className={view?.kind === "channel" && view.channel.id === ch.id ? "active" : ""} onClick={() => void openView({ kind: "channel", channel: ch })}>
                  # {ch.name}
                </button>
              ) : (
                <div key={ch.id}>
                  <button className={ch.id === voice.channelId ? "active" : ""} data-testid="voice-channel" onClick={() => void (ch.id === voice.channelId ? voice.leave() : voice.join(ch.id))}>
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
            <h3 className="section">Members — {members.length}</h3>
            <ul className="members">
              {members.map((m) => (
                <li key={m.userId} data-testid="member" data-name={m.username}>
                  <span className={`dot ${m.isOnline ? "connected" : ""}`} />
                  <span className="name">{m.effectiveDisplayName}</span>
                  {m.userId !== myId && (
                    <button className="link" title={`Message ${m.effectiveDisplayName}`} onClick={() => void messageUser(m.userId)}>
                      Message
                    </button>
                  )}
                </li>
              ))}
            </ul>
          </>
        )}
        <footer>
          <span>{auth.username}</span>
          {auth.isServerAdmin && (
            <button className="link" title="Invite a friend to this server" onClick={() => setPanel("invite")}>
              Invite
            </button>
          )}
          <span className={`dot ${hubState}`} title={`Realtime: ${hubState}`} />
          {inNativeApp && (
            <button className="link" title="Native video surface test (spike #51)" onClick={() => setShowStreamTest((v) => !v)}>
              📺
            </button>
          )}
          <button className="link" onClick={props.onLogout}>Log out</button>
        </footer>
      </aside>
      {panel ? (
        <main className="messages">
          {panel === "browse" ? <BrowseCommunities api={api} onOpen={(c) => void enterCommunity(c)} /> : <InviteFriends api={api} serverUrl={serverUrl} />}
        </main>
      ) : showStreamTest ? (
        <main className="messages">
          <header>Stream test</header>
          <StreamTest />
        </main>
      ) : (
        // data-live: this view receives realtime events (tests wait for it before acting).
        <main className="messages" data-live={view !== null && liveView === view}>
          <header>{title}</header>
          {error && <p className="error">{error}</p>}
          <MessageList messages={messages} actions={actions} />
          {view && (
            <form onSubmit={send}>
              {replyingTo && (
                <div className="replying" data-testid="replying">
                  Replying to <strong>{replyingTo.authorName}</strong>
                  <button type="button" className="link" title="Cancel reply" onClick={() => setReplyingTo(null)}>✕</button>
                </div>
              )}
              <input value={draft} onChange={(e) => onDraftChange(e.target.value)} placeholder={placeholder} aria-label="Message" autoFocus />
              <div className="typing" data-testid="typing">
                {typingNames.length === 1 && <><strong>{typingNames[0]}</strong> is typing…</>}
                {typingNames.length > 1 && <><strong>{typingNames.join(", ")}</strong> are typing…</>}
              </div>
            </form>
          )}
        </main>
      )}
    </div>
  );
}
