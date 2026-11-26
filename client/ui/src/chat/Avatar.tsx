import { useEffect, useState } from "react";
import type { FileLoader } from "./files";

/** Where a user's avatar is served. The file name busts the loader's cache when the avatar changes. */
const avatarPath = (userId: string, avatar: string) => `/api/users/${userId}/avatar?v=${encodeURIComponent(avatar)}`;

const COLORS = ["#5865f2", "#3ba55c", "#faa61a", "#ed4245", "#eb459e", "#00a8fc", "#9b59b6", "#1abc9c"];

function colorFor(id: string): string {
  let h = 0;
  for (const c of id) h = (h * 31 + c.charCodeAt(0)) | 0;
  return COLORS[Math.abs(h) % COLORS.length];
}

/** A user's picture, or their initial on a colour derived from their id while it loads or if they have none. */
export function Avatar(props: { userId: string; name: string; avatar: string | null | undefined; loadFile?: FileLoader; size?: number }) {
  const { userId, name, avatar, loadFile, size = 32 } = props;
  const [loaded, setLoaded] = useState<{ key: string; url: string } | null>(null);
  const key = avatar ? avatarPath(userId, avatar) : null;

  useEffect(() => {
    if (!key || !loadFile) return;
    let live = true;
    loadFile(key).then(
      (url) => live && setLoaded({ key, url }),
      () => {},
    );
    return () => {
      live = false;
    };
  }, [key, loadFile]);

  const style = { width: size, height: size, fontSize: Math.round(size * 0.42) };
  if (key && loaded?.key === key) return <img className="user-avatar" style={style} src={loaded.url} alt="" data-testid="avatar-image" />;
  return (
    <span className="user-avatar" style={{ ...style, background: colorFor(userId) }} aria-hidden>
      {name.slice(0, 1).toUpperCase()}
    </span>
  );
}
