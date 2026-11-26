import type { Schemas } from "../api/client";
import { attachmentPath } from "./files";

/**
 * One message as the UI shows it, whether it came from a channel or a DM. Server DTOs differ
 * (MessageResponse vs ConversationMessageResponse); the list and composer only see this.
 */
export type ChatMessage = {
  id: string;
  authorId: string;
  authorName: string;
  /** Avatar file name (see Avatar), null when the author has none. */
  authorAvatar: string | null;
  content: string;
  createdAt: string;
  edited: boolean;
  /** null where reactions aren't supported (DMs). */
  reactions: Reaction[] | null;
  /** The message this one replies to (channels only). */
  replyTo: { authorName: string; content: string } | null;
  attachments: Attachment[];
  /** Thread replies under this message (channels only). */
  replyCount: number;
  pinned: boolean;
  /** Set on thread replies: the message whose thread this is. */
  threadParentId: string | null;
};

export type Attachment = { id: string; name: string; path: string; size: number; isImage: boolean; isAudio: boolean };

export type Reaction = { emoji: string; count: number; mine: boolean };

export type ChannelMessage = Schemas["MessageResponse"];
export type DmMessage = NonNullable<Schemas["ConversationMessageResponse"]>;
export type ReactionUpdated = Schemas["ReactionUpdatedEvent"];
export type MessagePinned = Schemas["MessagePinnedEvent"];
/** Hub events "ReceiveThreadReply" and "ThreadMetadataUpdated" (anonymous objects on the server). */
export type ThreadReplyEvent = { channelId: string; parentMessageId: string; reply: ChannelMessage };
export type ThreadMetadataEvent = { channelId: string; messageId: string; replyCount: number; lastReplyAt: string | null };

export const fromChannel = (m: ChannelMessage): ChatMessage => ({
  id: m.id,
  authorId: m.authorId,
  authorName: m.authorEffectiveDisplayName,
  authorAvatar: m.authorAvatar ?? null,
  content: m.content,
  createdAt: m.createdAt,
  edited: m.isEdited,
  reactions: (m.reactions ?? []).map((r) => ({ emoji: r.emoji, count: r.count, mine: r.hasReacted })),
  replyTo: m.replyTo ? { authorName: m.replyTo.authorEffectiveDisplayName, content: m.replyTo.content } : null,
  attachments: (m.attachments ?? []).map((a) => ({ id: a.id, name: a.fileName, path: attachmentPath(a.url), size: a.fileSize, isImage: a.isImage, isAudio: a.isAudio })),
  replyCount: m.replyCount ?? 0,
  pinned: m.isPinned ?? false,
  threadParentId: m.threadParentMessageId ?? null,
});

export const fromDm = (m: DmMessage): ChatMessage => ({
  id: m.id,
  authorId: m.senderId,
  authorName: m.senderEffectiveDisplayName,
  authorAvatar: m.senderAvatar ?? null,
  content: m.content,
  createdAt: m.createdAt,
  edited: m.updatedAt != null,
  reactions: null,
  replyTo: null,
  attachments: [],
  replyCount: 0,
  pinned: false,
  threadParentId: null,
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

/** Replaces a message that's in the list; never adds (an edit of something not shown here). */
export const replace = (list: ChatMessage[], m: ChatMessage): ChatMessage[] => (list.some((x) => x.id === m.id) ? list.map((x) => (x.id === m.id ? m : x)) : list);

export const applyPin = (list: ChatMessage[], e: MessagePinned): ChatMessage[] =>
  list.some((m) => m.id === e.messageId) ? list.map((m) => (m.id === e.messageId ? { ...m, pinned: e.isPinned } : m)) : list;

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

/** Hub event "UserProfileUpdated" (server UserProfileUpdatedEvent): someone's name or picture changed. */
export type ProfileUpdated = { userId: string; username: string; effectiveDisplayName: string; avatar: string | null };

/** Shows a changed name and picture on that user's messages already on screen. */
export const applyProfile = (list: ChatMessage[], e: ProfileUpdated): ChatMessage[] =>
  list.some((m) => m.authorId === e.userId)
    ? list.map((m) => (m.authorId === e.userId ? { ...m, authorName: e.effectiveDisplayName, authorAvatar: e.avatar } : m))
    : list;
