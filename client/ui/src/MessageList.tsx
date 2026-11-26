import { memo, useEffect, useLayoutEffect, useRef, useState } from "react";
import { useVirtualizer } from "@tanstack/react-virtual";
import type { Attachment, ChatMessage } from "./chat/model";
import { formatSize, type FileLoader } from "./chat/files";
import { renderMarkdown } from "./chat/markdown";
import { Avatar } from "./chat/Avatar";

export type Message = ChatMessage;

/** What the current user may do to messages in this list (all optional: the benchmark has none). */
export type MessageActions = {
  myId: string;
  /** Owners, admins and moderators may delete anyone's message. */
  canModerate: boolean;
  onEdit?: (id: string, content: string) => void;
  onDelete?: (id: string) => void;
  onReact?: (id: string, emoji: string, add: boolean) => void;
  onReply?: (m: Message) => void;
  /** Pin or unpin (authors, and moderators for anyone's message). */
  onPin?: (id: string, pin: boolean) => void;
  /** Opens the message's thread (absent inside a thread: no nested threads). */
  onOpenThread?: (m: Message) => void;
  /** Loads attachments (they need the user's token). */
  loadFile?: FileLoader;
};

/** An attachment: images and audio inline, other files as a download link. */
function AttachmentView({ a, loadFile }: { a: Attachment; loadFile?: FileLoader }) {
  const [url, setUrl] = useState<string | null>(null);
  const [failed, setFailed] = useState(false);
  const inline = a.isImage || a.isAudio;
  useEffect(() => {
    if (!inline || !loadFile) return;
    let live = true;
    loadFile(a.path).then((u) => live && setUrl(u), () => live && setFailed(true));
    return () => {
      live = false;
    };
  }, [a.path, inline, loadFile]);

  const download = async () => {
    if (!loadFile) return;
    const link = document.createElement("a");
    link.href = await loadFile(a.path);
    link.download = a.name;
    link.click();
  };

  if (a.isImage && url) return <img className="attachment-image" src={url} alt={a.name} data-testid="attachment-image" onClick={() => void download()} />;
  if (a.isAudio && url) return <audio className="attachment-audio" src={url} controls data-testid="attachment-audio" />;
  return (
    <button className="attachment-file" data-testid="attachment-file" onClick={() => void download()} title="Download">
      📄 {a.name} <span className="muted">{failed ? "(unavailable)" : formatSize(a.size)}</span>
    </button>
  );
}

const QUICK_REACTIONS = ["👍", "❤️", "😂"];

const timeFormat = new Intl.DateTimeFormat(undefined, { hour: "2-digit", minute: "2-digit", second: "2-digit" });

const MessageBody = memo(function MessageBody({ m, actions }: { m: Message; actions?: MessageActions }) {
  const [editing, setEditing] = useState(false);
  const mine = actions?.myId === m.authorId;
  const canDelete = actions?.onDelete && (mine || actions.canModerate);
  return (
    <>
      <Avatar userId={m.authorId} name={m.authorName} avatar={m.authorAvatar} loadFile={actions?.loadFile} size={36} />
      <div className="message-main">
      {m.replyTo && (
        <div className="reply-quote" data-testid="reply-quote">
          ↪ <strong>{m.replyTo.authorName}</strong> <span>{m.replyTo.content}</span>
        </div>
      )}
      <strong data-testid="author">{m.authorName}</strong>
      <time>{timeFormat.format(new Date(m.createdAt))}</time>
      {m.edited && <span className="muted edited"> (edited)</span>}
      {m.pinned && (
        <span className="pinned-mark" title="Pinned" data-testid="pinned-mark">
          📌
        </span>
      )}
      {actions && !editing && (
        <span className="message-actions">
          {actions.onReact && m.reactions !== null &&
            QUICK_REACTIONS.map((emoji) => (
              <button key={emoji} title={`React ${emoji}`} onClick={() => actions.onReact?.(m.id, emoji, !m.reactions?.some((r) => r.emoji === emoji && r.mine))}>
                {emoji}
              </button>
            ))}
          {actions.onReply && <button title="Reply" onClick={() => actions.onReply?.(m)}>↩️</button>}
          {actions.onOpenThread && <button title="Reply in thread" onClick={() => actions.onOpenThread?.(m)}>🧵</button>}
          {actions.onPin && (mine || actions.canModerate) && (
            <button title={m.pinned ? "Unpin" : "Pin"} onClick={() => actions.onPin?.(m.id, !m.pinned)}>
              📌
            </button>
          )}
          {mine && actions.onEdit && <button title="Edit" onClick={() => setEditing(true)}>✏️</button>}
          {canDelete && (
            <button title="Delete" onClick={() => confirm("Delete this message?") && actions.onDelete?.(m.id)}>
              🗑️
            </button>
          )}
        </span>
      )}
      {editing ? (
        <form
          className="edit-message"
          onSubmit={(e) => {
            e.preventDefault();
            const content = String(new FormData(e.currentTarget).get("content") ?? "").trim();
            if (content && content !== m.content) actions?.onEdit?.(m.id, content);
            setEditing(false);
          }}
        >
          <input name="content" defaultValue={m.content} autoFocus onKeyDown={(e) => e.key === "Escape" && setEditing(false)} aria-label="Edit message" />
        </form>
      ) : (
        m.content && <div className="content">{renderMarkdown(m.content)}</div>
      )}
      {m.attachments.length > 0 && (
        <div className="attachments">
          {m.attachments.map((a) => (
            <AttachmentView key={a.id} a={a} loadFile={actions?.loadFile} />
          ))}
        </div>
      )}
      {m.reactions && m.reactions.length > 0 && (
        <div className="reactions">
          {m.reactions.map((r) => (
            <button
              key={r.emoji}
              className={r.mine ? "mine" : ""}
              data-testid="reaction"
              data-emoji={r.emoji}
              data-count={r.count}
              onClick={() => actions?.onReact?.(m.id, r.emoji, !r.mine)}
            >
              {r.emoji} {r.count}
            </button>
          ))}
        </div>
      )}
      {m.replyCount > 0 && actions?.onOpenThread && (
        <button className="thread-link" data-testid="thread-link" onClick={() => actions.onOpenThread?.(m)}>
          💬 {m.replyCount} {m.replyCount === 1 ? "reply" : "replies"}
        </button>
      )}
      </div>
    </>
  );
});

/** Pixels from the bottom that still count as "at the bottom" (sticky scrolling). */
const STICKY_THRESHOLD = 48;

/**
 * The chat message list, virtualized: only visible rows (plus overscan) are in the DOM, so
 * 10k+ messages cost the same as 50. Opens at the newest message and stays pinned to the
 * bottom when new messages arrive, unless the user has scrolled up.
 *
 * Used by the chat view and by the UI benchmark, so both measure the same thing.
 */
export function MessageList({ messages, actions }: { messages: Message[]; actions?: MessageActions }) {
  const scrollerRef = useRef<HTMLDivElement>(null);
  const atBottomRef = useRef(true);

  const virtualizer = useVirtualizer({
    count: messages.length,
    getScrollElement: () => scrollerRef.current,
    estimateSize: () => 52,
    overscan: 12,
    getItemKey: (index) => messages[index].id,
  });

  useEffect(() => {
    const el = scrollerRef.current;
    if (!el) return;
    const onScroll = () => {
      atBottomRef.current = el.scrollHeight - el.scrollTop - el.clientHeight < STICKY_THRESHOLD;
    };
    el.addEventListener("scroll", onScroll, { passive: true });
    return () => el.removeEventListener("scroll", onScroll);
  }, []);

  // New messages (or a freshly opened channel): follow the bottom if we were there.
  useLayoutEffect(() => {
    if (messages.length > 0 && atBottomRef.current) {
      virtualizer.scrollToIndex(messages.length - 1, { align: "end" });
    }
  }, [messages.length, virtualizer]);

  return (
    <div ref={scrollerRef} className="message-scroller">
      <ol className="message-list" style={{ height: virtualizer.getTotalSize() }}>
        {virtualizer.getVirtualItems().map((row) => (
          <li
            key={row.key}
            data-index={row.index}
            data-testid="message"
            data-message-id={messages[row.index].id}
            ref={virtualizer.measureElement}
            style={{ transform: `translateY(${row.start}px)` }}
          >
            <MessageBody m={messages[row.index]} actions={actions} />
          </li>
        ))}
      </ol>
    </div>
  );
}
