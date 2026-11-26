import type { AuthedFetch } from "../api/session";

/**
 * Attachments are served only to signed-in members (bearer token), which <img src> can't send.
 * Files are fetched with the token and shown from object URLs, cached per path.
 */
export type FileLoader = (path: string) => Promise<string>;

export function createFileLoader(serverUrl: string, authedFetch: AuthedFetch): FileLoader {
  const cache = new Map<string, Promise<string>>();
  return (path) => {
    let url = cache.get(path);
    if (!url) {
      url = authedFetch(`${serverUrl}${path}`).then(async (res) => {
        if (!res.ok) throw new Error(`${res.status} ${res.statusText}`);
        return URL.createObjectURL(await res.blob());
      });
      url.catch(() => cache.delete(path)); // retry next time
      cache.set(path, url);
    }
    return url;
  };
}

/** The server returns absolute URLs built from its own view of the host; keep only the path. */
export function attachmentPath(url: string): string {
  try {
    const u = new URL(url);
    return u.pathname + u.search;
  } catch {
    return url;
  }
}

export function formatSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(0)} KB`;
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
}
