import type { ChannelActivityEvent, DmMessage } from "./model";

/** What a desktop notification says. */
export type Alert = { title: string; body: string };

const preview = (content: string, hasAttachments: boolean) => {
  const text = content.trim().replace(/\s+/g, " ");
  if (!text) return hasAttachments ? "Sent an attachment" : "";
  return text.length > 200 ? `${text.slice(0, 199)}…` : text;
};

/**
 * Whether a channel message is worth interrupting someone for: it mentions them (or everyone),
 * replies to their message, or answers in a thread they started. Their own messages never are.
 */
export function channelAlert(e: ChannelActivityEvent, myId: string): Alert | null {
  const m = e.message;
  if (m.authorId === myId) return null;
  const body = preview(m.content, (m.attachments?.length ?? 0) > 0);
  const where = `#${e.channelName}`;
  if (e.mentionedUserIds.includes(myId)) return { title: `${m.authorEffectiveDisplayName} mentioned you in ${where}`, body };
  if (m.replyTo?.authorId === myId) return { title: `${m.authorEffectiveDisplayName} replied to you in ${where}`, body };
  if (e.threadParentAuthorId === myId && m.threadParentMessageId) return { title: `${m.authorEffectiveDisplayName} replied in your thread in ${where}`, body };
  return null;
}

/** Every direct message from someone else is. */
export function dmAlert(m: DmMessage, myId: string): Alert | null {
  if (m.senderId === myId) return null;
  return { title: m.senderEffectiveDisplayName, body: preview(m.content, false) };
}
