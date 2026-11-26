import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import type { HubConnection } from "@microsoft/signalr";
import { ok, type Api, type Auth, type Schemas } from "../api/client";
import type { Session } from "../api/session";
import { createHub, startHub } from "../api/hub";
import { MessageList, type MessageActions } from "../MessageList";
import { StreamTest, inNativeApp } from "../StreamTest";
import { VoicePanel, useVoice } from "../VoicePanel";
import { BrowseCommunities, InviteFriends } from "./Onboarding";
import { Avatar } from "./Avatar";
import { createFileLoader, formatSize } from "./files";
import { Settings, type Profile } from "./Settings";
import { channelActions } from "./channelActions";
import { createPreviewLoader } from "./previews";
import { channelAlert, dmAlert, type Alert } from "./alerts";
import { notifier, notificationsMuted } from "../notifier";
import { PinnedPane, ThreadPane } from "./SidePane";
import {
  applyPin,
  applyProfile,
  applyReaction,
  fromChannel,
  fromDm,
  replace,
  upsert,
  type ChannelMessage,
  type ChatMessage,
  type DmMessage,
  type MessagePinned,
  type ProfileUpdated,
  type ReactionUpdated,
  type ThreadMetadataEvent,
  type ChannelActivityEvent,
} from "./model";

type Community = Schemas["CommunityResponse"];
type Channel = Schemas["ChannelResponse"];
type Member = Schemas["CommunityMemberResponse"];
type Conversation = Schemas["ConversationResponse"];
type VoiceParticipant = Schemas["VoiceParticipantResponse"];

/** Mirrors Snacka.Shared ChannelType and UserRole (serialized as numbers). */
const ChannelKind = { Text: 0, Voice: 1 } as const;
const Role = { Owner: 0, Admin: 1, Moderator: 2, Member: 3 } as const;
const ROLE_NAMES: Record<number, string> = { [Role.Owner]: "Owner", [Role.Admin]: "Admin", [Role.Moderator]: "Mod", [Role.Member]: "Member" };

/** What the message pane shows: a text channel of the open community, or a DM conversation. */
type View = { kind: "channel"; channel: Channel } | { kind: "dm"; conversation: Conversation } | null;

/** Name of a conversation as the current user sees it: the group name, or the other people. */
function conversationName(c: Conversation, myId: string): string {
  if (c.name) return c.name;
  const others = c.participants.filter((p) => p.userId !== myId).map((p) => p.effectiveDisplayName);
  return others.join(", ") || "Just you";
}

export function Chat(props: { serverUrl: string; api: Api; auth: Auth; session: Session }) {
  const { api, serverUrl, session, auth } = props;
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
  /** Files to send with the next message (channels only). */
  const [pendingFiles, setPendingFiles] = useState<File[]>([]);
  const [dragging, setDragging] = useState(false);
  const fileInputRef = useRef<HTMLInputElement>(null);
  const loadFile = useMemo(() => createFileLoader(serverUrl, session.fetch), [serverUrl, session]);
  const loadPreview = useMemo(() => createPreviewLoader(api), [api]);
  const [newChannel, setNewChannel] = useState<{ name: string; voice: boolean } | null>(null);
  /** Renaming the open channel: the name being typed. */
  const [renaming, setRenaming] = useState<string | null>(null);
  /** The view whose realtime events we're receiving (joined its channel group while connected). */
  const [liveView, setLiveView] = useState<View>(null);
  /** Who is typing in the open channel/DM, and until when (ms) to show it. */
  const [typing, setTyping] = useState<Record<string, { name: string; until: number }>>({});
  const lastTypingSentRef = useRef(0);
  /** Replaces the message pane: joining/creating communities, or inviting friends. */
  const [panel, setPanel] = useState<"browse" | "invite" | "settings" | null>(null);
  /** Right-hand pane next to a channel: a message's thread, or the pinned messages. */
  const [side, setSide] = useState<{ kind: "thread"; parent: ChatMessage } | { kind: "pins" } | null>(null);
  /** The signed-in user's own profile (name, picture), kept current by UserProfileUpdated. */
  const [profile, setProfile] = useState<Profile | null>(null);
  const voice = useVoice(api);
  // Who is in which voice channel of the open community (LiveKit webhooks -> server -> hub).
  const [presence, setPresence] = useState<VoiceParticipant[]>([]);
  /** Unread messages in communities other than the open one (the open one's come from its channels). */
  const [communityUnread, setCommunityUnread] = useState<Record<string, number>>({});
  /** Mentions of me (and replies to me) since each channel was last read, this session. */
  const [mentions, setMentions] = useState<Record<string, { communityId: string; count: number }>>({});
  const communityRef = useRef<Community | null>(null);
  const communitiesRef = useRef<Community[]>([]);
  const conversationsRef = useRef<Conversation[]>([]);
  /** Opens what a clicked notification was about. */
  const jumpRef = useRef<(to: { communityId: string; channelId: string } | { conversationId: string }) => void>(() => {});
  const viewRef = useRef<View>(null);
  const hubRef = useRef<HubConnection | null>(null);
  const openViewRef = useRef<(v: View) => Promise<void>>(async () => {});
  useEffect(() => {
    communityRef.current = community;
    viewRef.current = view;
    communitiesRef.current = communities;
    conversationsRef.current = conversations;
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
  const loadCommunityUnread = useCallback(
    async () => setCommunityUnread(Object.fromEntries(ok(await api.GET("/api/communities/unread")).map((u) => [u.communityId, u.unreadCount]))),
    [api],
  );

  const loadPresence = useCallback(
    async (communityId: string) => {
      const list = ok(await api.GET("/api/voice/communities/{communityId}/participants", { params: { path: { communityId } } }));
      if (communityRef.current?.id === communityId) setPresence(list);
    },
    [api],
  );

  /** Marks a channel read on the server (and here). */
  const markChannelRead = useCallback(
    (communityId: string, channelId: string) => {
      setChannels((prev) => prev.map((c) => (c.id === channelId && c.unreadCount > 0 ? { ...c, unreadCount: 0 } : c)));
      setMentions((prev) => {
        if (!(channelId in prev)) return prev;
        const next = { ...prev };
        delete next[channelId];
        return next;
      });
      void api.POST("/api/communities/{communityId}/Channels/{channelId}/read", { params: { path: { communityId, channelId } } }).catch(() => {});
    },
    [api],
  );

  const isOpen = (kind: "channel" | "dm", id: string) => {
    const v = viewRef.current;
    return v?.kind === kind && (v.kind === "channel" ? v.channel.id : v.conversation.id) === id;
  };
  /** Open, and actually in front of the user. */
  const isSeen = (kind: "channel" | "dm", id: string) => isOpen(kind, id) && !document.hidden && document.hasFocus();
  const alert = (a: Alert | null, tag: string, to: Parameters<typeof jumpRef.current>[0]) => {
    if (a && !notificationsMuted()) notifier.show({ ...a, tag, onClick: () => jumpRef.current(to) });
  };

  // One hub connection per session. Events for the open channel/DM update the message list;
  // DM events also keep the conversation list (last message, unread count) current.
  useEffect(() => {
    const hub = createHub(session);
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

    // Thread replies belong to the thread pane (SidePane), not the channel's list.
    hub.on("ReceiveChannelMessage", (m: ChannelMessage) => {
      if (!isOpen("channel", m.channelId) || m.threadParentMessageId) return;
      stopTyping(m.authorId);
      setMessages((prev) => upsert(prev, fromChannel(m)));
    });
    hub.on("ChannelMessageEdited", (m: ChannelMessage) => isOpen("channel", m.channelId) && setMessages((prev) => replace(prev, fromChannel(m))));
    hub.on("ThreadMetadataUpdated", (e: ThreadMetadataEvent) =>
      setMessages((prev) => prev.map((m) => (m.id === e.messageId ? { ...m, replyCount: e.replyCount } : m))),
    );
    // Unread counts, mentions and notifications for messages anywhere in my communities. The open
    // channel stays read while it's on screen; in a hidden tab it counts up and is marked read
    // when the tab is shown again. Thread replies don't count as unread in the channel.
    hub.on("ChannelActivity", (e: ChannelActivityEvent) => {
      if (e.message.authorId === myId) return;
      const seen = isSeen("channel", e.channelId);
      const a = channelAlert(e, myId);
      if (!seen) alert(a, e.message.id, { communityId: e.communityId, channelId: e.channelId });
      if (a && !seen) setMentions((prev) => ({ ...prev, [e.channelId]: { communityId: e.communityId, count: (prev[e.channelId]?.count ?? 0) + 1 } }));
      if (e.message.threadParentMessageId) return;
      if (communityRef.current?.id !== e.communityId) setCommunityUnread((prev) => ({ ...prev, [e.communityId]: (prev[e.communityId] ?? 0) + 1 }));
      else if (isOpen("channel", e.channelId) && !document.hidden) markChannelRead(e.communityId, e.channelId);
      else setChannels((prev) => prev.map((c) => (c.id === e.channelId ? { ...c, unreadCount: c.unreadCount + 1 } : c)));
    });
    hub.on("MessagePinned", (e: MessagePinned) => setMessages((prev) => applyPin(prev, e)));
    hub.on("ChannelMessageDeleted", (e: { channelId: string; messageId: string }) => setMessages((prev) => prev.filter((m) => m.id !== e.messageId)));
    // Someone's name or picture changed: update it wherever it's on screen. A community nickname wins over the global name.
    hub.on("UserProfileUpdated", (e: ProfileUpdated) => {
      setMessages((prev) => applyProfile(prev, e));
      setMembers((prev) =>
        prev.map((m) => (m.userId === e.userId ? { ...m, username: e.username, avatar: e.avatar, effectiveDisplayName: m.displayNameOverride ?? e.effectiveDisplayName } : m)),
      );
      setConversations((prev) =>
        prev.map((c) =>
          c.participants.some((p) => p.userId === e.userId)
            ? { ...c, participants: c.participants.map((p) => (p.userId === e.userId ? { ...p, username: e.username, effectiveDisplayName: e.effectiveDisplayName, avatar: e.avatar } : p)) }
            : c,
        ),
      );
      if (e.userId === myId) void api.GET("/api/Users/me").then(ok).then(setProfile, () => {});
    });
    hub.on("ReactionUpdated", (e: ReactionUpdated) => isOpen("channel", e.channelId) && setMessages((prev) => applyReaction(prev, e, myId)));

    hub.on("ConversationMessageReceived", (m: DmMessage) => {
      const open = isOpen("dm", m.conversationId);
      if (!isSeen("dm", m.conversationId)) alert(dmAlert(m, myId), m.id, { conversationId: m.conversationId });
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

    hub.on("ChannelCreated", (ch: Channel) => ch.communityId === communityRef.current?.id && setChannels((prev) => (prev.some((c) => c.id === ch.id) ? prev : [...prev, ch])));
    // The event's unread count is the server's default, not ours: keep ours.
    hub.on("ChannelUpdated", (ch: Channel) => setChannels((prev) => prev.map((c) => (c.id === ch.id ? { ...ch, unreadCount: c.unreadCount } : c))));
    hub.on("ChannelDeleted", (e: { channelId: string }) => {
      setChannels((prev) => {
        const rest = prev.filter((c) => c.id !== e.channelId);
        // Looking at it: move to the first text channel left.
        const v = viewRef.current;
        if (v?.kind === "channel" && v.channel.id === e.channelId) {
          const next = rest.find((c) => c.type === ChannelKind.Text);
          void openViewRef.current(next ? { kind: "channel", channel: next } : null);
        }
        return rest;
      });
    });
    // Roles change what people may do (and see): pin, delete others' messages, manage channels.
    hub.on("MemberRoleUpdated", (m: Member) => setMembers((prev) => prev.map((x) => (x.userId === m.userId ? { ...x, role: m.role } : x))));
    hub.on("CommunityMemberAdded", (communityId: string) => {
      if (communityRef.current?.id !== communityId) return;
      void api.GET("/api/communities/{communityId}/members", { params: { path: { communityId } } }).then(ok).then(setMembers).catch(() => {});
    });
    // The server's notification inbox (legacy client); this client notifies from the events above.
    hub.on("PendingNotifications", () => {});
    hub.on("UnreadNotificationCount", () => {});
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
      if (c) {
        void loadPresence(c.id).catch(() => {});
        void api.GET("/api/communities/{communityId}/Channels", { params: { path: { communityId: c.id } } }).then(ok).then(
          (list) => communityRef.current?.id === c.id && setChannels(list),
          () => {},
        );
      }
      void loadConversations().catch(() => {});
      void loadCommunityUnread().catch(() => {});
      const v = viewRef.current;
      if (v) void openViewRef.current(v);
    });
    hub.onclose(() => setHubState("disconnected"));
    const stop = new AbortController();
    startHub(hub, stop.signal, () => setHubState("reconnecting"))
      .then(() => {
        setHubState("connected");
        // A channel opened before the connection was up never joined its realtime group
        // (and may have missed messages): open it again now.
        const v = viewRef.current;
        if (v) void openViewRef.current(v);
        const c = communityRef.current;
        if (c) void hub.invoke("JoinServer", c.id).catch(() => {});
      })
      .catch(() => {});
    return () => {
      stop.abort();
      void hub.stop();
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps -- one connection per server/session
  }, [serverUrl, session, myId, api, markChannelRead]);

  // Back to a tab with the open channel unread: it's been seen now.
  useEffect(() => {
    const onVisible = () => {
      const v = viewRef.current;
      const c = communityRef.current;
      if (!document.hidden && v?.kind === "channel" && c) markChannelRead(c.id, v.channel.id);
    };
    document.addEventListener("visibilitychange", onVisible);
    return () => document.removeEventListener("visibilitychange", onVisible);
  }, [markChannelRead]);

  useEffect(() => {
    void loadCommunities();
    void loadConversations().catch(() => {});
    void loadCommunityUnread().catch(() => {});
    void api.GET("/api/Users/me").then(ok).then(setProfile, () => {});
  }, [api, loadCommunities, loadConversations, loadCommunityUnread]);

  /** Shows a channel or DM: joins its realtime group (channels) and loads its history. */
  const openView = (next: View) =>
    run(async () => {
      const previous = viewRef.current;
      if (previous !== next) setSide(null); // reopening the same view (reconnect) keeps the pane
      setView(next);
      viewRef.current = next;
      setMessages([]);
      setReplyingTo(null);
      setPendingFiles([]);
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
        markChannelRead(next.channel.communityId, next.channel.id);
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

  /** Leaving the open community: its unread count now lives in communityUnread. */
  const rememberUnread = () => {
    const prev = communityRef.current;
    if (prev) setCommunityUnread((u) => ({ ...u, [prev.id]: channels.reduce((n, ch) => n + ch.unreadCount, 0) }));
  };

  const openCommunity = (c: Community, channelId?: string) =>
    run(async () => {
      setPanel(null);
      if (communityRef.current?.id !== c.id) rememberUnread();
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
      const target = list.find((ch) => ch.id === channelId) ?? list.find((ch) => ch.type === ChannelKind.Text);
      await openView(target ? { kind: "channel", channel: target } : null);
    });

  const openHome = () =>
    run(async () => {
      setPanel(null);
      rememberUnread();
      setCommunity(null);
      communityRef.current = null;
      await loadConversations();
      await openView(null);
    });

  const messageUser = (userId: string) =>
    run(async () => {
      setPanel(null);
      const conversation = ok(await api.GET("/api/conversations/direct/{otherUserId}", { params: { path: { otherUserId: userId } } }));
      rememberUnread();
      setCommunity(null);
      communityRef.current = null;
      setConversations((prev) => (prev.some((c) => c.id === conversation.id) ? prev : [conversation, ...prev]));
      await openView({ kind: "dm", conversation });
    });

  /** A clicked notification: open the channel or conversation it was about. */
  const jumpTo = (to: Parameters<typeof jumpRef.current>[0]) => {
    if ("conversationId" in to) {
      const { conversationId } = to;
      void run(async () => {
        // A brand-new conversation may not be in the list yet.
        const conversation =
          conversationsRef.current.find((c) => c.id === conversationId) ??
          ok(await api.GET("/api/conversations/{conversationId}", { params: { path: { conversationId } } }));
        setPanel(null);
        rememberUnread();
        setCommunity(null);
        communityRef.current = null;
        await openView({ kind: "dm", conversation });
      });
      return;
    }
    const c = communitiesRef.current.find((x) => x.id === to.communityId);
    if (!c) return;
    if (communityRef.current?.id !== c.id) void openCommunity(c, to.channelId);
    else {
      setPanel(null);
      const channel = channels.find((ch) => ch.id === to.channelId);
      if (channel) void openView({ kind: "channel", channel });
    }
  };
  useEffect(() => {
    jumpRef.current = jumpTo;
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
    const files = v?.kind === "channel" ? pendingFiles : [];
    if ((!content && files.length === 0) || !v) return;
    setDraft("");
    setPendingFiles([]);
    const replyToId = replyingTo?.id ?? null;
    setReplyingTo(null);
    lastTypingSentRef.current = 0;
    void run(async () => {
      const m =
        v.kind === "channel" && files.length > 0
          ? fromChannel(await uploadMessage(v.channel.id, content, replyToId, files))
          : v.kind === "channel"
          ? fromChannel(ok(await api.POST("/api/channels/{channelId}/messages", { params: { path: { channelId: v.channel.id } }, body: { content, replyToId } })))
          : fromDm(ok(await api.POST("/api/conversations/{conversationId}/messages", { params: { path: { conversationId: v.conversation.id } }, body: { content } })));
      if (viewRef.current === v) setMessages((prev) => upsert(prev, m));
    });
  };

  /** Sends a message with attachments (multipart; not expressible in the generated client). */
  const uploadMessage = async (channelId: string, content: string, replyToId: string | null, files: File[]): Promise<ChannelMessage> => {
    const form = new FormData();
    form.append("content", content);
    if (replyToId) form.append("replyToId", replyToId);
    for (const f of files) form.append("files", f, f.name);
    const res = await session.fetch(`${serverUrl}/api/channels/${channelId}/messages/with-attachments`, { method: "POST", body: form });
    if (!res.ok) {
      const body = (await res.json().catch(() => null)) as { error?: string } | null;
      throw new Error(body?.error ?? `Upload failed: ${res.status}`);
    }
    return (await res.json()) as ChannelMessage;
  };

  const addFiles = (files: Iterable<File>) => {
    if (view?.kind !== "channel") return;
    // Copy now: FileLists are live and get emptied when the input/drop event is reset.
    const list = [...files];
    setPendingFiles((prev) => [...prev, ...list].slice(0, 10));
  };

  const createChannel = (e: React.FormEvent) => {
    e.preventDefault();
    const c = community;
    const draftChannel = newChannel;
    if (!c || !draftChannel?.name.trim()) return;
    setNewChannel(null);
    void run(async () => {
      const ch = ok(
        await api.POST("/api/communities/{communityId}/Channels", {
          params: { path: { communityId: c.id } },
          body: { name: draftChannel.name.trim(), topic: null, type: draftChannel.voice ? ChannelKind.Voice : ChannelKind.Text },
        }),
      );
      setChannels((prev) => (prev.some((x) => x.id === ch.id) ? prev : [...prev, ch]));
      if (!draftChannel.voice) await openView({ kind: "channel", channel: ch });
    });
  };

  /** Owners only: admins manage channels, moderators manage messages (the server checks this too). */
  const setRole = (userId: string, role: number) => {
    const c = community;
    if (!c) return;
    void run(async () => {
      const updated = ok(await api.PUT("/api/communities/{communityId}/members/{memberId}/role", { params: { path: { communityId: c.id, memberId: userId } }, body: { role } }));
      setMembers((prev) => prev.map((m) => (m.userId === updated.userId ? { ...m, role: updated.role } : m)));
    });
  };

  const renameChannel = (e: React.FormEvent) => {
    e.preventDefault();
    const name = renaming?.trim();
    const c = community;
    const ch = openChannel;
    if (!name || !c || !ch) return;
    setRenaming(null);
    if (name === ch.name) return;
    void run(async () => {
      const updated = ok(
        await api.PUT("/api/communities/{communityId}/Channels/{channelId}", {
          params: { path: { communityId: c.id, channelId: ch.id } },
          body: { name, topic: null, position: null },
        }),
      );
      setChannels((prev) => prev.map((x) => (x.id === updated.id ? { ...updated, unreadCount: x.unreadCount } : x)));
    });
  };

  const deleteChannel = (ch: Channel) => {
    const c = community;
    if (!c || !confirm(`Delete #${ch.name} and all its messages? This can't be undone.`)) return;
    void run(async () => {
      const res = await api.DELETE("/api/communities/{communityId}/Channels/{channelId}", { params: { path: { communityId: c.id, channelId: ch.id } } });
      if (!res.response.ok) throw new Error(`Couldn't delete the channel: ${res.response.status}`);
      // The ChannelDeleted event updates the list and moves us off it, here as for everyone.
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
  const canModerate = me !== undefined && me.role !== Role.Member;
  /** Creating, renaming and deleting channels: owners and admins (as the server checks). */
  const canManage = me !== undefined && (me.role === Role.Owner || me.role === Role.Admin);
  const openChannelId = view?.kind === "channel" ? view.channel.id : null;
  const closeSide = useCallback(() => setSide(null), []);
  /** Message actions in the open channel, for the channel's list (with replies and threads) or a side pane's. */
  const makeChannelActions = useCallback(
    (setList: React.Dispatch<React.SetStateAction<ChatMessage[]>>, main = false): MessageActions =>
      channelActions({
        api,
        channelId: openChannelId ?? "",
        myId,
        myUsername: auth.username,
        canModerate,
        loadFile,
        loadPreview,
        run,
        setList,
        onReply: main ? (m) => setReplyingTo(m) : undefined,
        onOpenThread: main ? (m) => setSide({ kind: "thread", parent: m }) : undefined,
      }),
    [api, openChannelId, myId, auth.username, canModerate, loadFile, loadPreview, run],
  );
  const makeSideActions = useCallback((setList: React.Dispatch<React.SetStateAction<ChatMessage[]>>) => makeChannelActions(setList), [makeChannelActions]);
  const actions = useMemo<MessageActions | undefined>(() => {
    const v = view;
    if (!v) return undefined;
    if (v.kind === "channel") return makeChannelActions(setMessages, true);
    const path = (messageId: string) => ({ params: { path: { conversationId: v.conversation.id, messageId } } });
    return {
      myId,
      loadFile,
      loadPreview,
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
  }, [view, myId, api, run, loadFile, loadPreview, makeChannelActions]);

  // The conversation as currently known (names in it change live), not the copy taken when it was opened.
  const openConversation = view?.kind === "dm" ? (conversations.find((c) => c.id === view.conversation.id) ?? view.conversation) : null;
  // Likewise the open channel as currently known (it may have been renamed).
  const openChannel = view?.kind === "channel" ? (channels.find((c) => c.id === view.channel.id) ?? view.channel) : null;
  const title = !view ? (community ? community.name : "Direct messages") : view.kind === "channel" ? `# ${(openChannel ?? view.channel).name}` : `@ ${conversationName(openConversation ?? view.conversation, myId)}`;
  const placeholder = openChannel ? `Message #${openChannel.name}` : openConversation ? `Message ${conversationName(openConversation, myId)}` : "";
  const showSide = side !== null && !panel && !showStreamTest && view?.kind === "channel";
  const unreadTotal = conversations.reduce((n, c) => n + c.unreadCount, 0);
  const mentionTotal = Object.values(mentions).reduce((n, m) => n + m.count, 0);
  // Unread DMs and mentions in the tab title, for when the tab is in the background.
  useEffect(() => {
    const n = unreadTotal + mentionTotal;
    document.title = n > 0 ? `(${n}) Snacka` : "Snacka";
  }, [unreadTotal, mentionTotal]);

  return (
    <div className={`chat${showSide ? " with-side" : ""}`}>
      <nav className="communities">
        <button className={community === null ? "active" : ""} title="Direct messages" data-testid="home" onClick={() => void openHome()}>
          💬{unreadTotal > 0 && <span className="badge" data-testid="unread-total">{unreadTotal}</span>}
        </button>
        <hr />
        {communities.map((c) => {
          const unread = c.id === community?.id ? channels.reduce((n, ch) => n + ch.unreadCount, 0) : (communityUnread[c.id] ?? 0);
          const mentioned = Object.values(mentions).reduce((n, m) => (m.communityId === c.id ? n + m.count : n), 0);
          return (
            <button
              key={c.id}
              className={[c.id === community?.id && "active", unread > 0 && "unread"].filter(Boolean).join(" ")}
              title={c.name}
              data-testid="community"
              data-name={c.name}
              onClick={() => void openCommunity(c)}
            >
              {c.name.slice(0, 2).toUpperCase()}
              {mentioned > 0 ? (
                <span className="badge" data-testid="community-mentions">
                  {mentioned}
                </span>
              ) : (
                unread > 0 && <span className="unread-dot" data-testid="community-unread" />
              )}
            </button>
          );
        })}
        <button className={panel === "browse" ? "active" : ""} title="Join or create a community" data-testid="browse" onClick={() => setPanel("browse")}>+</button>
      </nav>
      <aside className="channels">
        <header className="sidebar-header">
          <span>{community?.name ?? "Direct messages"}</span>
          {community && canManage && (
            <button className="link" title="Create channel" data-testid="new-channel" onClick={() => setNewChannel(newChannel ? null : { name: "", voice: false })}>
              ＋
            </button>
          )}
        </header>
        {newChannel && (
          <form className="new-channel" onSubmit={createChannel}>
            <input autoFocus placeholder="new-channel" aria-label="Channel name" value={newChannel.name} onChange={(e) => setNewChannel({ ...newChannel, name: e.target.value })} />
            <label>
              <input type="checkbox" checked={newChannel.voice} onChange={(e) => setNewChannel({ ...newChannel, voice: e.target.checked })} /> Voice
            </label>
            <button className="primary" type="submit">Create</button>
          </form>
        )}
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
                <button
                  key={ch.id}
                  data-testid="channel"
                  data-name={ch.name}
                  className={`${view?.kind === "channel" && view.channel.id === ch.id ? "active" : ""}${ch.unreadCount > 0 ? " unread" : ""}`}
                  onClick={() => void openView({ kind: "channel", channel: ch })}
                >
                  # {ch.name}
                  {mentions[ch.id] ? (
                    <span className="badge" data-testid="channel-mentions" title="Mentions of you">
                      @{mentions[ch.id].count}
                    </span>
                  ) : (
                    ch.unreadCount > 0 && <span className="badge quiet" data-testid="channel-unread">{ch.unreadCount}</span>
                  )}
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
                  <span className="member-avatar">
                    <Avatar userId={m.userId} name={m.effectiveDisplayName} avatar={m.avatar} loadFile={loadFile} size={28} />
                    <span className={`dot ${m.isOnline ? "connected" : ""}`} />
                  </span>
                  <span className="name">{m.effectiveDisplayName}</span>
                  {me?.role === Role.Owner && m.role !== Role.Owner ? (
                    <select
                      className="member-role"
                      data-testid="member-role"
                      aria-label={`Role of ${m.effectiveDisplayName}`}
                      value={m.role}
                      onChange={(e) => setRole(m.userId, Number(e.target.value))}
                    >
                      <option value={Role.Admin}>Admin</option>
                      <option value={Role.Moderator}>Moderator</option>
                      <option value={Role.Member}>Member</option>
                    </select>
                  ) : (
                    m.role !== Role.Member && (
                      <span className="role-tag" data-testid="role-tag">
                        {ROLE_NAMES[m.role]}
                      </span>
                    )
                  )}
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
          <button className={`me${panel === "settings" ? " active" : ""}`} title="Profile and settings" data-testid="open-settings" onClick={() => setPanel(panel === "settings" ? null : "settings")}>
            <Avatar userId={myId} name={profile?.effectiveDisplayName ?? auth.username} avatar={profile?.avatar} loadFile={loadFile} size={28} />
            <span className="name">{profile?.effectiveDisplayName ?? auth.username}</span>
          </button>
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
          <button className="link" onClick={() => void session.logout()}>Log out</button>
        </footer>
      </aside>
      {panel ? (
        <main className="messages">
          {panel === "browse" ? (
            <BrowseCommunities api={api} onOpen={(c) => void enterCommunity(c)} />
          ) : panel === "invite" ? (
            <InviteFriends api={api} serverUrl={serverUrl} />
          ) : profile ? (
            <Settings api={api} session={session} loadFile={loadFile} profile={profile} onChange={setProfile} />
          ) : (
            <p className="muted panel">Loading…</p>
          )}
        </main>
      ) : showStreamTest ? (
        <main className="messages">
          <header>Stream test</header>
          <StreamTest />
        </main>
      ) : (
        // data-live: this view receives realtime events (tests wait for it before acting).
        <main
          className={`messages${dragging ? " dragging" : ""}`}
          data-live={view !== null && liveView === view}
          onDragOver={(e) => {
            if (view?.kind !== "channel" || !e.dataTransfer.types.includes("Files")) return;
            e.preventDefault();
            setDragging(true);
          }}
          onDragLeave={() => setDragging(false)}
          onDrop={(e) => {
            if (view?.kind !== "channel") return;
            e.preventDefault();
            setDragging(false);
            addFiles(e.dataTransfer.files);
          }}
        >
          <header>
            {openChannel && renaming !== null ? (
              <form className="grow rename-channel" onSubmit={renameChannel}>
                <input value={renaming} onChange={(e) => setRenaming(e.target.value)} aria-label="Channel name" maxLength={100} autoFocus onKeyDown={(e) => e.key === "Escape" && setRenaming(null)} />
                <button className="primary" type="submit">Save</button>
                <button type="button" className="link" onClick={() => setRenaming(null)}>Cancel</button>
              </form>
            ) : (
              <span className="grow" data-testid="view-title">{title}</span>
            )}
            {openChannel && canManage && renaming === null && (
              <>
                <button className="link" title="Rename channel" data-testid="rename-channel" onClick={() => setRenaming(openChannel.name)}>
                  ✏️
                </button>
                <button className="link" title="Delete channel" data-testid="delete-channel" onClick={() => deleteChannel(openChannel)}>
                  🗑️
                </button>
              </>
            )}
            {view?.kind === "channel" && (
              <button className={`link${side?.kind === "pins" ? " active" : ""}`} title="Pinned messages" data-testid="open-pins" onClick={() => setSide(side?.kind === "pins" ? null : { kind: "pins" })}>
                📌
              </button>
            )}
          </header>
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
              {pendingFiles.length > 0 && (
                <ul className="pending-files">
                  {pendingFiles.map((f, i) => (
                    <li key={`${f.name}-${i}`} data-testid="pending-file">
                      📄 {f.name} <span className="muted">{formatSize(f.size)}</span>
                      <button type="button" className="link" title={`Remove ${f.name}`} onClick={() => setPendingFiles((prev) => prev.filter((_, j) => j !== i))}>✕</button>
                    </li>
                  ))}
                </ul>
              )}
              <div className="composer">
                {view.kind === "channel" && (
                  <>
                    <button type="button" className="attach" title="Attach files" onClick={() => fileInputRef.current?.click()}>📎</button>
                    <input
                      ref={fileInputRef}
                      type="file"
                      multiple
                      hidden
                      data-testid="file-input"
                      onChange={(e) => {
                        addFiles(e.target.files ?? []);
                        e.target.value = "";
                      }}
                    />
                  </>
                )}
                <textarea
                  rows={Math.min(8, draft.split("\n").length)}
                  value={draft}
                  onChange={(e) => onDraftChange(e.target.value)}
                  onKeyDown={(e) => {
                    // Enter sends; Shift+Enter is a new line (and IME composition is left alone).
                    if (e.key === "Enter" && !e.shiftKey && !e.nativeEvent.isComposing) {
                      e.preventDefault();
                      e.currentTarget.form?.requestSubmit();
                    }
                  }}
                  onPaste={(e) => {
                    const files = [...e.clipboardData.files];
                    if (files.length > 0 && view.kind === "channel") {
                      e.preventDefault();
                      addFiles(files);
                    }
                  }}
                  placeholder={placeholder}
                  aria-label="Message"
                  autoFocus
                />
              </div>
              <div className="typing" data-testid="typing">
                {typingNames.length === 1 && <><strong>{typingNames[0]}</strong> is typing…</>}
                {typingNames.length > 1 && <><strong>{typingNames.join(", ")}</strong> are typing…</>}
              </div>
            </form>
          )}
        </main>
      )}
      {showSide && view?.kind === "channel" && side?.kind === "thread" && (
        <ThreadPane key={side.parent.id} api={api} hubRef={hubRef} parent={side.parent} myId={myId} makeActions={makeSideActions} onClose={closeSide} />
      )}
      {showSide && view?.kind === "channel" && side?.kind === "pins" && (
        <PinnedPane key={view.channel.id} api={api} hubRef={hubRef} channelId={view.channel.id} makeActions={makeSideActions} onClose={closeSide} />
      )}
    </div>
  );
}
