import {
  DefaultHttpClient,
  HttpClient,
  HttpError,
  HubConnectionBuilder,
  LogLevel,
  NullLogger,
  type HttpRequest,
  type HttpResponse,
  type HubConnection,
} from "@microsoft/signalr";
import type { Session } from "./session";

/** Backoff for (re)connecting: 1, 2, 4 … capped at 30 s, forever. The server may be down a while (restart, laptop asleep). */
export const retryDelay = (attempt: number) => Math.min(30_000, 1000 * 2 ** attempt);

/**
 * SignalR connection to the server's hub. It reconnects for as long as it's open, and renews the
 * session when the server turns its token down (a restarted server may have a new signing key).
 */
export function createHub(session: Session): HubConnection {
  return new HubConnectionBuilder()
    .withUrl(`${session.serverUrl}/hubs/snacka`, {
      accessTokenFactory: async () => (await session.token()) ?? "",
      httpClient: new RenewingHttpClient(session),
      // Auth is a bearer token, not cookies. Without withCredentials: false the client sends
      // credentials, which browsers/WebViews reject against a wildcard CORS origin (seen on Android).
      withCredentials: false,
    })
    .withAutomaticReconnect({ nextRetryDelayInMilliseconds: (ctx) => retryDelay(ctx.previousRetryCount) })
    .configureLogging(LogLevel.Warning)
    .build();
}

/** Starts the connection, retrying with backoff until it's up or `signal` aborts. */
export async function startHub(hub: HubConnection, signal: AbortSignal, onRetry: (e: Error) => void): Promise<void> {
  for (let attempt = 0; ; attempt++) {
    try {
      await hub.start();
      return;
    } catch (e) {
      if (signal.aborted) throw e;
      onRetry(e as Error);
      await new Promise((resolve) => setTimeout(resolve, retryDelay(attempt)));
      if (signal.aborted) throw new Error("Stopped");
    }
  }
}

/** On a 401 (negotiate), renews the session once and retries with the new token. */
class RenewingHttpClient extends HttpClient {
  private readonly inner = new DefaultHttpClient(NullLogger.instance);
  private readonly session: Session;

  constructor(session: Session) {
    super();
    this.session = session;
  }

  async send(request: HttpRequest): Promise<HttpResponse> {
    try {
      return await this.inner.send(request);
    } catch (e) {
      if (!(e instanceof HttpError) || e.statusCode !== 401) throw e;
      const stale = request.headers?.Authorization?.replace(/^Bearer /, "") ?? null;
      const token = await this.session.renew(stale);
      if (!token) throw e;
      return this.inner.send({ ...request, headers: { ...request.headers, Authorization: `Bearer ${token}` } });
    }
  }

  getCookieString(url: string): string {
    return this.inner.getCookieString(url);
  }
}
