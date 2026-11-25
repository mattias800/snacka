import { HubConnectionBuilder, LogLevel, type HubConnection } from "@microsoft/signalr";

/** SignalR connection to the server's hub, with automatic reconnect. */
export function createHub(serverUrl: string, getToken: () => string | null): HubConnection {
  return new HubConnectionBuilder()
    // Auth is a bearer token, not cookies. Without withCredentials: false the client sends
    // credentials, which browsers/WebViews reject against a wildcard CORS origin (seen on Android).
    .withUrl(`${serverUrl}/hubs/snacka`, { accessTokenFactory: () => getToken() ?? "", withCredentials: false })
    .withAutomaticReconnect()
    .configureLogging(LogLevel.Warning)
    .build();
}
