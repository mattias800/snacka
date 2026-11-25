import { memo, useEffect, useLayoutEffect, useRef } from "react";
import { useVirtualizer } from "@tanstack/react-virtual";
import type { Schemas } from "./api/client";

export type Message = Schemas["MessageResponse"];

const timeFormat = new Intl.DateTimeFormat(undefined, { hour: "2-digit", minute: "2-digit", second: "2-digit" });

const MessageBody = memo(function MessageBody({ m }: { m: Message }) {
  return (
    <>
      <strong>{m.authorEffectiveDisplayName}</strong>
      <time>{timeFormat.format(new Date(m.createdAt))}</time>
      <p>{m.content}</p>
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
export function MessageList({ messages }: { messages: Message[] }) {
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
            <MessageBody m={messages[row.index]} />
          </li>
        ))}
      </ol>
    </div>
  );
}
