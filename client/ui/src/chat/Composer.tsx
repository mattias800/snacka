import { useRef, useState } from "react";
import { EmojiPicker } from "./EmojiPicker";
import { formatSize } from "./files";
import type { ChatMessage } from "./model";

/**
 * The message box under a channel or DM: multi-line text (Enter sends, Shift+Enter breaks the
 * line), emoji, attachments (channels only: button, paste; drag-and-drop is handled by the
 * pane around it), the message being replied to, and who's typing.
 */
export function Composer(props: {
  placeholder: string;
  canAttach: boolean;
  replyingTo: ChatMessage | null;
  onCancelReply: () => void;
  files: File[];
  onAddFiles: (files: Iterable<File>) => void;
  onRemoveFile: (index: number) => void;
  typingNames: string[];
  /** Sends the message; files (if any) go with it. */
  onSend: (content: string) => void;
  /** Called while the user types, at most every 3 s. */
  onTyping: () => void;
}) {
  const { canAttach, files, onAddFiles, typingNames } = props;
  const [draft, setDraft] = useState("");
  const fileInputRef = useRef<HTMLInputElement>(null);
  const textRef = useRef<HTMLTextAreaElement>(null);
  const lastTypingRef = useRef(0);

  const change = (value: string) => {
    setDraft(value);
    if (!value || Date.now() - lastTypingRef.current < 3000) return;
    lastTypingRef.current = Date.now();
    props.onTyping();
  };

  const submit = (e: React.FormEvent) => {
    e.preventDefault();
    const content = draft.trim();
    if (!content && files.length === 0) return;
    setDraft("");
    lastTypingRef.current = 0;
    props.onSend(content);
  };

  /** Puts an emoji where the cursor is, and the cursor after it. */
  const insertEmoji = (emoji: string) => {
    const el = textRef.current;
    const start = el?.selectionStart ?? draft.length;
    const end = el?.selectionEnd ?? draft.length;
    change(draft.slice(0, start) + emoji + draft.slice(end));
    requestAnimationFrame(() => {
      el?.focus();
      el?.setSelectionRange(start + emoji.length, start + emoji.length);
    });
  };

  return (
    <form onSubmit={submit}>
      {props.replyingTo && (
        <div className="replying" data-testid="replying">
          Replying to <strong>{props.replyingTo.authorName}</strong>
          <button type="button" className="link" title="Cancel reply" onClick={props.onCancelReply}>✕</button>
        </div>
      )}
      {files.length > 0 && (
        <ul className="pending-files">
          {files.map((f, i) => (
            <li key={`${f.name}-${i}`} data-testid="pending-file">
              📄 {f.name} <span className="muted">{formatSize(f.size)}</span>
              <button type="button" className="link" title={`Remove ${f.name}`} onClick={() => props.onRemoveFile(i)}>✕</button>
            </li>
          ))}
        </ul>
      )}
      <div className="composer">
        {canAttach && (
          <>
            <button type="button" className="attach" title="Attach files" onClick={() => fileInputRef.current?.click()}>📎</button>
            <input
              ref={fileInputRef}
              type="file"
              multiple
              hidden
              data-testid="file-input"
              onChange={(e) => {
                onAddFiles(e.target.files ?? []);
                e.target.value = "";
              }}
            />
          </>
        )}
        <EmojiPicker icon="😀" label="Insert emoji" testId="composer-emoji" onPick={insertEmoji} />
        <textarea
          ref={textRef}
          rows={Math.min(8, draft.split("\n").length)}
          value={draft}
          onChange={(e) => change(e.target.value)}
          onKeyDown={(e) => {
            // Enter sends; Shift+Enter is a new line (and IME composition is left alone).
            if (e.key === "Enter" && !e.shiftKey && !e.nativeEvent.isComposing) {
              e.preventDefault();
              e.currentTarget.form?.requestSubmit();
            }
          }}
          onPaste={(e) => {
            const pasted = [...e.clipboardData.files];
            if (pasted.length > 0 && canAttach) {
              e.preventDefault();
              onAddFiles(pasted);
            }
          }}
          placeholder={props.placeholder}
          aria-label="Message"
          autoFocus
        />
      </div>
      <div className="typing" data-testid="typing">
        {typingNames.length === 1 && <><strong>{typingNames[0]}</strong> is typing…</>}
        {typingNames.length > 1 && <><strong>{typingNames.join(", ")}</strong> are typing…</>}
      </div>
    </form>
  );
}
