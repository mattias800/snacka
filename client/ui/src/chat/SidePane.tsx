import { useEffect, useMemo, useState, type Dispatch, type RefObject, type SetStateAction } from "react";
import type { HubConnection } from "@microsoft/signalr";
import { ok, type Api } from "../api/client";
import { MessageList, type MessageActions } from "../MessageList";
import {
  applyPin,
  applyProfile,
  applyReaction,
  fromChannel,
  replace,
  upsert,
  type ChannelMessage,
  type ChatMessage,
  type MessagePinned,
  type ProfileUpdated,
  type ReactionUpdated,
  type ThreadReplyEvent,
} from "./model";

type Handlers = Record<string, (...args: never[]) => void>;

/** Subscribes to hub events for as long as the pane is open (the hub itself belongs to Chat). */
function useHubEvents(hubRef: RefObject<HubConnection | null>, handlers: Handlers) {
  useEffect(() => {
    const hub = hubRef.current;
    if (!hub) return;
    const entries = Object.entries(handlers) as [string, (...args: unknown[]) => void][];
    for (const [name, h] of entries) hub.on(name, h);
    return () => {
      for (const [name, h] of entries) hub.off(name, h);
    };
  }, [hubRef, handlers]);
}

type MakeActions = (setList: Dispatch<SetStateAction<ChatMessage[]>>) => MessageActions;

/** A message's thread: the message itself, then its replies, live, with a composer for new replies. */
export function ThreadPane(props: { api: Api; hubRef: RefObject<HubConnection | null>; parent: ChatMessage; myId: string; makeActions: MakeActions; onClose: () => void }) {
  const { api, hubRef, parent, myId, makeActions, onClose } = props;
  // The parent first, then replies: one list so edits, reactions and pins apply to both alike.
  const [items, setItems] = useState<ChatMessage[]>([parent]);
  const [draft, setDraft] = useState("");
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let live = true;
    api
      .GET("/api/messages/{parentMessageId}/thread", { params: { path: { parentMessageId: parent.id }, query: { pageSize: 100 } } })
      .then(ok)
      .then(
        (t) => {
          if (!live) return;
          const loaded = [fromChannel(t.parentMessage), ...t.replies.map(fromChannel)];
          // Keep replies that arrived live while loading.
          setItems((prev) => [...loaded, ...prev.filter((p) => !loaded.some((l) => l.id === p.id))].sort((a, b) => a.createdAt.localeCompare(b.createdAt)));
        },
        (e: Error) => live && setError(e.message),
      );
    return () => {
      live = false;
    };
  }, [api, parent.id]);

  const handlers = useMemo<Handlers>(() => {
    const inThread = (m: ChannelMessage) => m.id === parent.id || m.threadParentMessageId === parent.id;
    return {
      ReceiveThreadReply: (e: ThreadReplyEvent) => e.parentMessageId === parent.id && setItems((prev) => upsert(prev, fromChannel(e.reply))),
      ChannelMessageEdited: (m: ChannelMessage) => inThread(m) && setItems((prev) => replace(prev, fromChannel(m))),
      ChannelMessageDeleted: (e: { messageId: string }) => {
        if (e.messageId === parent.id) onClose();
        else setItems((prev) => prev.filter((m) => m.id !== e.messageId));
      },
      ReactionUpdated: (e: ReactionUpdated) => setItems((prev) => applyReaction(prev, e, myId)),
      MessagePinned: (e: MessagePinned) => setItems((prev) => applyPin(prev, e)),
      UserProfileUpdated: (e: ProfileUpdated) => setItems((prev) => applyProfile(prev, e)),
    };
  }, [parent.id, myId, onClose]);
  useHubEvents(hubRef, handlers);

  const actions = useMemo(() => makeActions(setItems), [makeActions]);

  const send = async (e: React.FormEvent) => {
    e.preventDefault();
    const content = draft.trim();
    if (!content) return;
    try {
      setError(null);
      const reply = ok(await api.POST("/api/messages/{parentMessageId}/replies", { params: { path: { parentMessageId: parent.id } }, body: { content, replyToId: null } }));
      setDraft("");
      setItems((prev) => upsert(prev, fromChannel(reply)));
    } catch (err) {
      setError((err as Error).message);
    }
  };

  const [root, ...replies] = items;
  return (
    <aside className="side-pane" data-testid="thread">
      <header>
        <span>Thread</span>
        <span className="muted">{replies.length === 1 ? "1 reply" : `${replies.length} replies`}</span>
        <button className="link" title="Close thread" onClick={onClose}>✕</button>
      </header>
      {error && <p className="error">{error}</p>}
      <MessageList messages={root ? [root, ...replies] : replies} actions={actions} />
      <form onSubmit={(e) => void send(e)}>
        <textarea
          rows={Math.min(6, draft.split("\n").length)}
          value={draft}
          onChange={(e) => setDraft(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Enter" && !e.shiftKey && !e.nativeEvent.isComposing) {
              e.preventDefault();
              e.currentTarget.form?.requestSubmit();
            }
          }}
          placeholder="Reply in thread"
          aria-label="Thread reply"
          autoFocus
        />
      </form>
    </aside>
  );
}

/** The channel's pinned messages, kept current as people pin and unpin. */
export function PinnedPane(props: { api: Api; hubRef: RefObject<HubConnection | null>; channelId: string; makeActions: MakeActions; onClose: () => void }) {
  const { api, hubRef, channelId, makeActions, onClose } = props;
  const [pinned, setPinned] = useState<ChatMessage[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [version, setVersion] = useState(0);

  useEffect(() => {
    let live = true;
    api
      .GET("/api/channels/{channelId}/messages/pinned", { params: { path: { channelId } } })
      .then(ok)
      .then(
        (list) => live && setPinned(list.map(fromChannel)),
        (e: Error) => live && setError(e.message),
      );
    return () => {
      live = false;
    };
  }, [api, channelId, version]);

  const handlers = useMemo<Handlers>(
    () => ({
      MessagePinned: (e: MessagePinned) => e.channelId === channelId && setVersion((v) => v + 1),
      ChannelMessageDeleted: (e: { messageId: string }) => setPinned((prev) => prev && prev.filter((m) => m.id !== e.messageId)),
      ChannelMessageEdited: (m: ChannelMessage) => setPinned((prev) => prev && replace(prev, fromChannel(m))),
    }),
    [channelId],
  );
  useHubEvents(hubRef, handlers);

  // Unpinning here drops the message from this list right away.
  const actions = useMemo<MessageActions>(() => {
    const base = makeActions((update) => setPinned((prev) => (prev ? (typeof update === "function" ? update(prev) : update).filter((m) => m.pinned) : prev)));
    return { myId: base.myId, myUsername: base.myUsername, canModerate: base.canModerate, loadFile: base.loadFile, onPin: base.onPin };
  }, [makeActions]);

  return (
    <aside className="side-pane" data-testid="pins">
      <header>
        <span>📌 Pinned</span>
        <button className="link" title="Close pinned messages" onClick={onClose}>✕</button>
      </header>
      {error && <p className="error">{error}</p>}
      {pinned?.length === 0 && <p className="muted hint">Nothing pinned yet. Hover a message and press 📌.</p>}
      {pinned && pinned.length > 0 && <MessageList messages={pinned} actions={actions} />}
    </aside>
  );
}
