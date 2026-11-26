import { useEffect, useState } from "react";
import type { Preview, PreviewLoader } from "./previews";

/** A card under a message for its first link: site, title, description and image. */
export function LinkPreviewCard({ url, load }: { url: string; load: PreviewLoader }) {
  const [preview, setPreview] = useState<Preview | null>(null);
  useEffect(() => {
    let live = true;
    void load(url).then((p) => live && setPreview(p));
    return () => {
      live = false;
    };
  }, [url, load]);
  if (!preview || (!preview.title && !preview.description && !preview.imageUrl)) return null;
  const isImage = preview.type === "image";
  return (
    <div className="link-preview" data-testid="link-preview">
      {!isImage && (
        <div className="link-preview-text">
          {preview.siteName && <div className="muted site">{preview.siteName}</div>}
          {preview.title && (
            <a href={preview.url} target="_blank" rel="noopener noreferrer" className="title">
              {preview.title}
            </a>
          )}
          {preview.description && <p className="description">{preview.description}</p>}
        </div>
      )}
      {preview.imageUrl && <img src={preview.imageUrl} alt="" loading="lazy" referrerPolicy="no-referrer" className={isImage ? "full" : "thumb"} />}
    </div>
  );
}
