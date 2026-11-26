import { ok, type Api, type Schemas } from "../api/client";

export type Preview = Schemas["LinkPreview"];
/** Fetches (and remembers) the server's preview of a URL; null when there's none. */
export type PreviewLoader = (url: string) => Promise<Preview | null>;

/** The first http(s) link in a message, if any (the same rule the markdown renderer links). */
export function firstLink(content: string): string | null {
  const m = /https?:\/\/[^\s<]+[^\s<.,:;"')\]!?]/.exec(content);
  return m ? m[0] : null;
}

export function createPreviewLoader(api: Api): PreviewLoader {
  const cache = new Map<string, Promise<Preview | null>>();
  return (url) => {
    let p = cache.get(url);
    if (!p) {
      p = api.GET("/api/link-previews", { params: { query: { url } } }).then(
        (r) => (r.response.status === 404 ? null : ok(r)),
        () => null,
      );
      cache.set(url, p);
    }
    return p;
  };
}
