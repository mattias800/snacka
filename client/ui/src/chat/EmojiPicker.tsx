import { useEffect, useRef, useState } from "react";
import { createPortal } from "react-dom";

/** A short, hand-picked set: what friends actually react and chat with. No dependency, no network. */
const GROUPS: { name: string; emoji: string[] }[] = [
  { name: "Smileys", emoji: ["😀", "😃", "😄", "😁", "😆", "😅", "🤣", "😂", "🙂", "😉", "😊", "😇", "🥰", "😍", "😘", "😋", "😛", "😜", "🤪", "🤔", "🤨", "😐", "😑", "😶", "🙄", "😏", "😬", "😌", "😴", "🤤", "😷", "🤒", "🥵", "🥶", "🤯", "🥳", "😎", "🤓", "😕", "😟", "😮", "😲", "😳", "🥺", "😢", "😭", "😱", "😤", "😡", "🤬", "💀", "🤡", "👻", "👽", "🤖"] },
  { name: "Gestures", emoji: ["👍", "👎", "👌", "🤌", "✌️", "🤞", "🤟", "🤘", "👋", "👏", "🙌", "🙏", "🤝", "💪", "👀", "🫡", "🤷", "🤦", "🙈"] },
  { name: "Hearts", emoji: ["❤️", "🧡", "💛", "💚", "💙", "💜", "🖤", "🤍", "💔", "💯", "🔥", "✨", "⭐", "🎉", "🎊"] },
  { name: "Games", emoji: ["🎮", "🕹️", "🎲", "🏆", "🥇", "⚔️", "🛡️", "🏹", "💣", "🚀", "🏁", "🧩", "♟️", "🃏", "👾"] },
  { name: "Food", emoji: ["🍕", "🍔", "🍟", "🌮", "🍣", "🍜", "🍿", "🍩", "🍪", "🎂", "☕", "🍺", "🍻", "🥂", "🧃"] },
  { name: "Other", emoji: ["✅", "❌", "⚠️", "❓", "❗", "💤", "💬", "📌", "🔔", "⏰", "🌙", "☀️", "🌧️", "❄️", "🐐", "🐸", "🐱", "🐶", "🦀", "🍀"] },
];

/**
 * A button that opens a grid of emoji; picking one calls onPick and closes it. The grid is
 * rendered into <body> and positioned against the window: message rows are transformed (the
 * virtualized list), which would otherwise make "fixed" relative to the row.
 */
export function EmojiPicker(props: { onPick: (emoji: string) => void; icon: string; label: string; testId?: string }) {
  const [at, setAt] = useState<{ x: number; y: number } | null>(null);
  const buttonRef = useRef<HTMLButtonElement>(null);
  const panelRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!at) return;
    const close = (e: Event) => {
      if (e instanceof KeyboardEvent ? e.key === "Escape" : !panelRef.current?.contains(e.target as Node) && !buttonRef.current?.contains(e.target as Node)) setAt(null);
    };
    document.addEventListener("mousedown", close);
    document.addEventListener("keydown", close);
    return () => {
      document.removeEventListener("mousedown", close);
      document.removeEventListener("keydown", close);
    };
  }, [at]);

  const open = () => {
    const r = buttonRef.current?.getBoundingClientRect();
    if (!r) return;
    const width = 300;
    const height = 280;
    // Above the button when there's room (the composer is at the bottom), else below.
    const y = r.top > height + 8 ? r.top - height - 4 : r.bottom + 4;
    setAt({ x: Math.max(8, Math.min(r.right - width, window.innerWidth - width - 8)), y });
  };

  return (
    <>
      <button ref={buttonRef} type="button" className="emoji-button" title={props.label} aria-label={props.label} data-testid={props.testId} onClick={() => (at ? setAt(null) : open())}>
        {props.icon}
      </button>
      {at &&
        createPortal(
        <div ref={panelRef} className="emoji-picker" role="dialog" aria-label="Emoji" data-testid="emoji-picker" style={{ left: at.x, top: at.y }}>
          {GROUPS.map((g) => (
            <section key={g.name}>
              <h4>{g.name}</h4>
              <div className="emoji-grid">
                {g.emoji.map((e) => (
                  <button
                    key={e}
                    type="button"
                    title={e}
                    onClick={() => {
                      props.onPick(e);
                      setAt(null);
                    }}
                  >
                    {e}
                  </button>
                ))}
              </div>
            </section>
          ))}
        </div>,
          document.body,
        )}
    </>
  );
}
