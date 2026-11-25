import { memo } from "react";
import type { Schemas } from "./api/client";

export type Message = Schemas["MessageResponse"];

const MessageItem = memo(function MessageItem({ m }: { m: Message }) {
  return (
    <li>
      <strong>{m.authorEffectiveDisplayName}</strong>
      <time>{new Date(m.createdAt).toLocaleTimeString()}</time>
      <p>{m.content}</p>
    </li>
  );
});

/** The chat message list. Used by the chat view and by the UI benchmark, so both measure the same thing. */
export function MessageList({ messages }: { messages: Message[] }) {
  return (
    <ol className="message-list">
      {messages.map((m) => (
        <MessageItem key={m.id} m={m} />
      ))}
    </ol>
  );
}
