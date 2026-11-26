import type { Auth } from "./client";

/** Where the signed-in session is kept between page loads (shared by all tabs of the app). */
export interface AuthStore {
  load(): Auth | null;
  save(auth: Auth | null): void;
}

export type AuthedFetch = (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>;

/** Refresh this long before the access token expires, so requests never race its expiry. */
const EARLY_MS = 60_000;

/**
 * The signed-in session with one server. Access tokens live an hour; the session swaps its refresh
 * token for a new pair before that, and once more if the server still says 401. The server rotates
 * refresh tokens on every use (#15), so tabs coordinate through the store: whichever refreshes
 * first saves the new pair, and the others pick it up instead of spending the old token again.
 */
export class Session {
  readonly serverUrl: string;
  private readonly store: AuthStore;
  /** Called whenever the session changes; null means signed out. */
  private readonly onChange: (auth: Auth | null) => void;
  private auth: Auth | null;
  private refreshing: Promise<Auth | null> | null = null;

  constructor(serverUrl: string, store: AuthStore, onChange: (auth: Auth | null) => void) {
    this.serverUrl = serverUrl;
    this.store = store;
    this.onChange = onChange;
    this.auth = store.load();
  }

  get current(): Auth | null {
    return this.auth;
  }

  /** Starts a session from a login or registration response. */
  set(auth: Auth | null): void {
    this.auth = auth;
    this.store.save(auth);
    this.onChange(auth);
  }

  /** Another tab changed the stored session. */
  adopt(auth: Auth | null): void {
    if (auth?.refreshToken === this.auth?.refreshToken) return;
    this.auth = auth;
    this.onChange(auth);
  }

  /** A usable access token, refreshing first if it is about to expire. Null when signed out. */
  async token(): Promise<string | null> {
    const a = this.auth;
    if (!a) return null;
    if (Date.parse(a.expiresAt) - Date.now() > EARLY_MS) return a.accessToken;
    return (await this.refresh())?.accessToken ?? null;
  }

  /** The server refused `stale`: a fresh access token, or null if the user has to sign in again. */
  async renew(stale: string | null): Promise<string | null> {
    return (await this.refresh(stale))?.accessToken ?? null;
  }

  /**
   * Fetch with the access token. A 401 gets one retry after a refresh. Usable as openapi-fetch's
   * `fetch` and for the multipart uploads the generated client can't express.
   */
  fetch: AuthedFetch = async (input, init) => {
    const request = new Request(input, init);
    const retry = request.clone();
    const token = await this.token();
    if (token) request.headers.set("Authorization", `Bearer ${token}`);
    const res = await fetch(request);
    if (res.status !== 401 || !this.auth) return res;
    const fresh = await this.renew(token);
    if (!fresh) return res;
    retry.headers.set("Authorization", `Bearer ${fresh}`);
    return fetch(retry);
  };

  /** Signs this device out on the server (best effort, briefly), then locally. */
  async logout(): Promise<void> {
    const refreshToken = this.auth?.refreshToken;
    if (refreshToken) {
      try {
        await fetch(`${this.serverUrl}/api/Auth/logout`, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ refreshToken }),
          signal: AbortSignal.timeout(3000),
        });
      } catch {
        /* offline: the token expires on its own */
      }
    }
    this.set(null);
  }

  /**
   * Exchanges the refresh token for a new pair, once at a time. `stale` is the access token that
   * failed: if the session has moved on since, the newer one is used without refreshing again.
   */
  private refresh(stale?: string | null): Promise<Auth | null> {
    if (stale && this.auth && this.auth.accessToken !== stale) return Promise.resolve(this.auth);
    this.refreshing ??= withLock("snacka.refresh", () => this.doRefresh()).finally(() => {
      this.refreshing = null;
    });
    return this.refreshing;
  }

  private async doRefresh(): Promise<Auth | null> {
    // Another tab may have refreshed while this one waited for the lock.
    const stored = this.store.load();
    if (stored && stored.refreshToken !== this.auth?.refreshToken && Date.parse(stored.expiresAt) - Date.now() > EARLY_MS) {
      this.adopt(stored);
      return stored;
    }
    const refreshToken = this.auth?.refreshToken;
    if (!refreshToken) return null;
    const res = await fetch(`${this.serverUrl}/api/Auth/refresh`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ refreshToken }),
    });
    if (res.status === 401) {
      // Revoked, expired or reused: the user has to sign in again.
      this.set(null);
      return null;
    }
    if (!res.ok) throw new Error(`Couldn't renew the session: ${res.status}`);
    const next = (await res.json()) as Auth;
    this.set(next);
    return next;
  }
}

/** Runs `f` holding a lock shared by all tabs, where the browser supports it. */
function withLock<T>(name: string, f: () => Promise<T>): Promise<T> {
  const locks = typeof navigator !== "undefined" ? navigator.locks : undefined;
  return locks ? locks.request(name, f) : f();
}
