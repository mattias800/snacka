/**
 * `@username` mentions, matched the way the server matches them (ChannelActivityService): an `@`
 * that starts a word, then letters, digits, `_`, `.` or `-` (not ending in `.` or `-`).
 * `@everyone` names every member.
 */
export const MENTION = /(?<![\p{L}\p{N}_@])@([\p{L}\p{N}_.-]*[\p{L}\p{N}_])/u;

/** Whether `content` mentions `username` (any case), or everyone. */
export function mentionsUser(content: string, username: string): boolean {
  const me = username.toLowerCase();
  for (const m of content.matchAll(new RegExp(MENTION, "gu"))) {
    const name = m[1].toLowerCase();
    if (name === me || name === "everyone") return true;
  }
  return false;
}
