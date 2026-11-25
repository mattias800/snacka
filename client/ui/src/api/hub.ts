import { HubConnectionBuilder, LogLevel, type HubConnection } from "@microsoft/signalr";

/** SignalR connection to the server's hub, with automatic reconnect. */
export function createHub(serverUrl: string, getToken: () => string | null): HubConnection {
  return new HubConnectionBuilder()
    .withUrl(`${serverUrl}/hubs/snacka`, { accessTokenFactory: () => getToken() ?? "" })
    .withAutomaticReconnect()
    .configureLogging(LogLevel.Warning)
    .build();
}
