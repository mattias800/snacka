import type { Schemas } from "../api/client";

/**
 * One message as the UI shows it, whether it came from a channel or a DM. Server DTOs differ
 * (MessageResponse vs ConversationMessageResponse); the list and composer only see this.
 */
export type ChatMessage = {
  id: string;
  authorId: string;
  authorName: string;
  content: string;
  createdAt: string;
  edited: boolean;
  /** null where reactions aren't supported (DMs). */
  reactions: Reaction[] | null;
  /** The message this one replies to (channels only). */
  replyTo: { authorName: string; content: string } | null;
};

export type Reaction = { emoji: string; count: number; mine: boolean };

export type ChannelMessage = Schemas["MessageResponse"];
export type DmMessage = NonNullable<Schemas["ConversationMessageResponse"]>;
export type ReactionUpdated = Schemas["ReactionUpdatedEvent"];

export const fromChannel = (m: ChannelMessage): ChatMessage => ({
  id: m.id,
  authorId: m.authorId,
  authorName: m.authorEffectiveDisplayName,
  content: m.content,
  createdAt: m.createdAt,
  edited: m.isEdited,
  reactions: (m.reactions ?? []).map((r) => ({ emoji: r.emoji, count: r.count, mine: r.hasReacted })),
  replyTo: m.replyTo ? { authorName: m.replyTo.authorEffectiveDisplayName, content: m.replyTo.content } : null,
});

export const fromDm = (m: DmMessage): ChatMessage => ({
  id: m.id,
  authorId: m.senderId,
  authorName: m.senderEffectiveDisplayName,
  content: m.content,
  createdAt: m.createdAt,
  edited: m.updatedAt != null,
  reactions: null,
  replyTo: null,
});

/** Adds or replaces a message by id, keeping the list in time order. */
export function upsert(list: ChatMessage[], m: ChatMessage): ChatMessage[] {
  const i = list.findIndex((x) => x.id === m.id);
  if (i >= 0) return list.map((x) => (x.id === m.id ? m : x));
  const next = [...list, m];
  return next.length > 1 && next[next.length - 2].createdAt > m.createdAt
    ? next.sort((a, b) => a.createdAt.localeCompare(b.createdAt))
    : next;
}

/** Applies a ReactionUpdated event: the server sends the new count; `mine` follows who reacted. */
export function applyReaction(list: ChatMessage[], e: ReactionUpdated, myId: string): ChatMessage[] {
  return list.map((m) => {
    if (m.id !== e.messageId || m.reactions === null) return m;
    const existing = m.reactions.find((r) => r.emoji === e.emoji);
    const mine = e.userId === myId ? e.added : (existing?.mine ?? false);
    const others = m.reactions.filter((r) => r.emoji !== e.emoji);
    const reactions = e.count > 0 ? [...others, { emoji: e.emoji, count: e.count, mine }] : others;
    // Keep chips in a stable order (first reaction first).
    const order = m.reactions.map((r) => r.emoji);
    reactions.sort((a, b) => (order.indexOf(a.emoji) + 1 || 1e9) - (order.indexOf(b.emoji) + 1 || 1e9));
    return { ...m, reactions };
  });
}
