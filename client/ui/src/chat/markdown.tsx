import { Fragment, type ReactNode } from "react";
import { MENTION } from "./mentions";

/**
 * The small Markdown subset chat needs, rendered to React elements (never HTML strings, so
 * message content can't inject markup): ```code blocks```, `code`, **bold**, *italic* or
 * _italic_, ~~strikethrough~~, bare http(s) links and @mentions. No nesting, by design.
 */
const INLINE = new RegExp(
  [/(`[^`\n]+`)/, /(\*\*[^*\n]+\*\*)/, /(~~[^~\n]+~~)/, /(\*[^*\s][^*\n]*\*|\b_[^_\s][^_\n]*_\b)/, /(https?:\/\/[^\s<]+[^\s<.,:;"')\]!?])/, new RegExp(`(${MENTION.source})`)]
    .map((r) => r.source)
    .join("|"),
  "gu",
);

function inline(text: string, key: string, myName: string | undefined): ReactNode[] {
  const out: ReactNode[] = [];
  let last = 0;
  let i = 0;
  for (const m of text.matchAll(INLINE)) {
    const start = m.index ?? 0;
    if (start > last) out.push(text.slice(last, start));
    const [token, code, bold, strike, italic, url, mention, mentionName] = m;
    const k = `${key}-${i++}`;
    if (code) out.push(<code key={k}>{code.slice(1, -1)}</code>);
    else if (bold) out.push(<strong key={k}>{bold.slice(2, -2)}</strong>);
    else if (strike) out.push(<s key={k}>{strike.slice(2, -2)}</s>);
    else if (italic) out.push(<em key={k}>{italic.slice(1, -1)}</em>);
    else if (url)
      out.push(
        <a key={k} href={url} target="_blank" rel="noopener noreferrer">
          {url}
        </a>,
      );
    else if (mention) {
      const name = mentionName.toLowerCase();
      const me = name === "everyone" || name === myName?.toLowerCase();
      out.push(
        <span key={k} className={me ? "mention me" : "mention"}>
          {mention}
        </span>,
      );
    } else out.push(token);
    last = start + token.length;
  }
  if (last < text.length) out.push(text.slice(last));
  return out;
}

/** `myName` (the reader's username) highlights mentions of them, and of everyone. */
export function renderMarkdown(content: string, myName?: string): ReactNode {
  const parts = content.split(/```(?:[a-zA-Z0-9_+-]*\n)?([\s\S]*?)```/g);
  return parts.map((part, i) =>
    i % 2 === 1 ? (
      <pre key={i}>
        <code>{part.replace(/\n$/, "")}</code>
      </pre>
    ) : (
      <Fragment key={i}>{inline(part, String(i), myName)}</Fragment>
    ),
  );
}
