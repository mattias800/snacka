# Port Forwarding Implementation Plan

## Overview

Allow a user in a voice channel to share a local development server (e.g. Vite on `localhost:5173`) with other voice channel members, tunneled through the Snacka server. This enables pair programming workflows where one developer shares their running app for others to interact with in real-time.

### Goals

- One-click sharing of a local port with voice channel members
- Other members get a clickable link that opens in their browser
- Traffic flows through the Snacka server (not peer-to-peer)
- Supports HTTP, WebSocket (Vite HMR), and static assets
- Auto-detect common dev server ports
- Secure: only current voice channel members can access

### Non-Goals (for now)

- UDP forwarding (game servers, etc.)
- Persistent tunnels that survive voice channel disconnects
- Custom domain names per tunnel
- Bandwidth throttling / usage quotas

---

## Architecture

```
┌──────────────────┐         ┌──────────────────┐         ┌──────────────────┐
│  Sharing User's  │         │   Snacka Server   │         │  Viewing User's  │
│  Snacka Client   │         │                  │         │    Browser       │
│                  │         │                  │         │                  │
│  localhost:5173 ◄├────┐    │                  │    ┌────►│  Opens link:     │
│  (Vite server)   │    │    │                  │    │    │  /tunnel/{id}/   │
│                  │    │    │                  │    │    │                  │
│  TunnelClient ───┼──WS───►│  TunnelService   │◄──HTTP──┼──────────────────┘
│  (control+data)  │    │    │  (proxy bridge)  │    │
│                  │    │    │                  │    │
└──────────────────┘    │    └──────────────────┘    │
                        │                            │
                        │    Control: SignalR         │
                        │    Data: WebSocket tunnel   │
                        └────────────────────────────┘
```

### Tunnel Protocol

The tunnel uses a **TCP-over-WebSocket** approach, which transparently supports HTTP requests, WebSocket upgrades (Vite HMR), and any other TCP traffic.

**Control plane** — SignalR (existing connection):
- Announce/retract shared ports to voice channel members
- Request tunnel access tokens

**Data plane** — Dedicated WebSocket connections:
1. Sharing client opens a **control WebSocket** to `/ws/tunnel/{tunnelId}/control`
2. When a browser request arrives at `/tunnel/{tunnelId}/{**path}`:
   a. Server generates a `connectionId` and sends `{"type":"open","connectionId":"..."}` on the control WebSocket
   b. Sharing client opens a **data WebSocket** to `/ws/tunnel/{tunnelId}/data/{connectionId}`
   c. Sharing client opens a local TCP connection to `localhost:{port}`
   d. Server bridges: browser TCP ↔ data WebSocket ↔ local TCP
3. When either side closes, the bridge tears down cleanly

This approach is how ngrok, bore, and similar tools work. It handles HTTP and WebSocket traffic transparently since we're tunneling at the TCP level.

### Authentication & Access Control

1. **Tunnel creation**: Authenticated via existing SignalR JWT
2. **Tunnel access** (browser):
   - User clicks link in Snacka client → client calls `RequestTunnelAccess(tunnelId)` via SignalR
   - Server validates: is this user in the same voice channel as the tunnel owner?
   - Server returns a short-lived access token (5 min expiry, single-tunnel scope)
   - Client opens browser: `https://server/tunnel/{tunnelId}/?_tunnel_token={token}`
   - Server validates token, sets an HTTP-only session cookie `tunnel_{tunnelId}` (30 min TTL)
   - All subsequent requests (assets, HMR WebSocket) use the cookie automatically
3. **Cleanup**: When tunnel owner leaves voice or stops sharing, all active connections are terminated

---

## UX Design

### Sharing a Port

The share button lives in the **voice connected panel** (bottom bar), next to the existing screen share and camera buttons.

```
┌─────────────────────────────────────────────────┐
│ 🟢 Connected · #dev-voice                       │
│                                                 │
│  [🎥] [🖥️] [🌐 Share Port] [📋] [🔴 Disconnect] │
└─────────────────────────────────────────────────┘
```

**Flow:**

1. User clicks **"Share Port"** button (globe icon 🌐)
2. A **port picker popover** appears:

```
┌──────────────────────────────────┐
│  Share a local port              │
│                                  │
│  Detected servers:               │
│  ┌────────────────────────────┐  │
│  │ ● localhost:5173 (Vite)    │  │  ← auto-detected, highlighted
│  │ ○ localhost:3000           │  │
│  │ ○ localhost:8080           │  │
│  └────────────────────────────┘  │
│                                  │
│  Or enter manually:              │
│  [ Port: 5173          ]         │
│  [ Label: My Vite App  ]         │  ← optional friendly name
│                                  │
│         [Cancel] [Share]         │
└──────────────────────────────────┘
```

3. User selects a port and clicks **Share**
4. The button changes to active state (green, like camera/screen share when active)
5. A **toast/status message** appears: "Sharing localhost:5173 with voice channel"

### Viewing a Shared Port (Other Members)

When someone shares a port, other voice channel members see:

1. **Voice participant list** — A small link icon appears next to the sharing user's name:

```
  #dev-voice
  ├── Alice 🎤 🌐          ← port sharing indicator
  ├── Bob 🎤
  └── Charlie 🎤 🖥️
```

2. **Notification banner** at the top of the voice channel content area:

```
┌──────────────────────────────────────────────────┐
│ 🌐 Alice is sharing "My Vite App" (port 5173)   │
│                              [Open in Browser]   │
└──────────────────────────────────────────────────┘
```

3. Clicking **"Open in Browser"**:
   - Client requests a tunnel access token via SignalR
   - Opens the system browser with the authenticated tunnel URL
   - The shared app loads in the browser, with HMR working

### Multiple Shared Ports

A user can share multiple ports simultaneously. The voice channel content area shows a list:

```
┌──────────────────────────────────────────────────┐
│  Shared Ports                                    │
│                                                  │
│  🌐 Alice · My Vite App (5173)  [Open in Browser]│
│  🌐 Alice · API Server (3000)   [Open in Browser]│
│  🌐 Bob   · Storybook (6006)    [Open in Browser]│
└──────────────────────────────────────────────────┘
```

### Stopping a Share

- Click the active (green) **Share Port** button → shows list of active shares with **Stop** buttons
- Or: leave the voice channel → all shares stop automatically
- Other members see the port disappear from the list and get a brief toast: "Alice stopped sharing port 5173"

---

## Implementation Phases

### Phase 1: Tunnel Infrastructure (Server)

This is the core TCP-over-WebSocket tunnel engine on the server side.

#### 1.1 TunnelService (Singleton)

**File:** `src/Snacka.Server/Services/TunnelService.cs`

Manages active tunnels and their connections. Runs as a singleton since it holds in-memory state.

```csharp
public interface ITunnelService
{
    TunnelInfo CreateTunnel(Guid ownerId, Guid channelId, int localPort, string? label);
    void RemoveTunnel(string tunnelId);
    void RemoveAllTunnelsForUser(Guid userId);
    TunnelInfo? GetTunnel(string tunnelId);
    IReadOnlyList<TunnelInfo> GetTunnelsForChannel(Guid channelId);
    IReadOnlyList<TunnelInfo> GetTunnelsForUser(Guid userId);

    // Access tokens
    string GenerateAccessToken(string tunnelId, Guid userId);
    TunnelAccessClaim? ValidateAccessToken(string token);

    // Connection management (data plane)
    void RegisterControlConnection(string tunnelId, WebSocket controlWs);
    void UnregisterControlConnection(string tunnelId);
    Task<WebSocket?> WaitForDataConnection(string tunnelId, string connectionId, TimeSpan timeout);
    void RegisterDataConnection(string tunnelId, string connectionId, WebSocket dataWs);
}

public record TunnelInfo(
    string TunnelId,           // Short unique ID (e.g., "a3xk9m")
    Guid OwnerId,
    string OwnerUsername,
    Guid ChannelId,
    int LocalPort,
    string? Label,
    DateTime CreatedAt,
    bool IsControlConnected
);

public record TunnelAccessClaim(string TunnelId, Guid UserId, DateTime ExpiresAt);
```

**Key design:**
- `TunnelId` is a short random string (8 chars, URL-safe) — not a GUID, for clean URLs
- Tunnels are purely in-memory — no database persistence needed
- Access tokens are JWT-like signed strings with 5-minute expiry
- `WaitForDataConnection` uses a `TaskCompletionSource` — the proxy middleware awaits it after sending the "open" command to the sharing client

#### 1.2 Tunnel WebSocket Endpoints

**File:** `src/Snacka.Server/Controllers/TunnelWsController.cs`

Two WebSocket endpoints for the data plane:

```
GET /ws/tunnel/{tunnelId}/control     — Control channel (sharing client)
GET /ws/tunnel/{tunnelId}/data/{connId} — Data channel (one per proxied connection)
```

Both require JWT auth via query string `?access_token=...` (existing pattern).

**Control WebSocket protocol** (JSON messages):
```jsonc
// Server → Client: new incoming connection
{"type": "open", "connectionId": "abc123"}

// Server → Client: connection closed by browser
{"type": "close", "connectionId": "abc123"}

// Client → Server: heartbeat
{"type": "ping"}

// Server → Client: heartbeat response
{"type": "pong"}
```

**Data WebSocket**: Raw binary frames, bridged byte-for-byte to/from the browser's TCP connection.

#### 1.3 Tunnel Proxy Middleware

**File:** `src/Snacka.Server/Middleware/TunnelProxyMiddleware.cs`

Intercepts requests to `/tunnel/{tunnelId}/{**path}` and proxies them through the tunnel.

**Flow:**
1. Extract `tunnelId` from path
2. Check auth: validate `_tunnel_token` query param or `tunnel_{tunnelId}` cookie
3. If token in query param: validate it, set cookie, redirect to clean URL (without token)
4. Look up tunnel in `TunnelService`
5. Generate a `connectionId`
6. Send `{"type":"open","connectionId":"..."}` on the tunnel's control WebSocket
7. Wait for the sharing client to connect a data WebSocket for that `connectionId` (timeout: 10s)
8. Bridge the browser's connection ↔ data WebSocket:
   - For regular HTTP: serialize the request, read the response, send it back
   - For WebSocket upgrade: upgrade both sides, bridge frames bidirectionally
9. On disconnect: clean up both sides

**WebSocket passthrough** (critical for Vite HMR):
- Detect `Upgrade: websocket` header
- Accept the WebSocket from the browser side
- Signal the sharing client to also open a WebSocket to `localhost:{port}`
- Bridge WebSocket frames bidirectionally through the data WebSocket

#### 1.4 Cookie-Based Session Auth

When a user first accesses a tunnel with a valid access token:
1. Validate the token (signed, not expired, tunnel exists, user in voice channel)
2. Set `tunnel_{tunnelId}` cookie (HTTP-only, SameSite=Lax, 30-min expiry, path=/tunnel/{tunnelId}/)
3. Redirect to the clean URL without the token query parameter

Subsequent requests (assets, HMR, etc.) are authenticated via the cookie. This avoids needing tokens on every sub-request.

---

### Phase 2: SignalR Integration

#### 2.1 DTOs

**File:** `src/Snacka.Server/DTOs/TunnelDtos.cs`

```csharp
// Request
public record SharePortRequest(
    int Port,
    string? Label    // Optional friendly name like "My Vite App"
);

// Response
public record SharedPortInfo(
    string TunnelId,
    Guid OwnerId,
    string OwnerUsername,
    int Port,
    string? Label,
    DateTime SharedAt
);

// Events (broadcast to voice channel)
public record PortSharedEvent(
    Guid ChannelId,
    string TunnelId,
    Guid OwnerId,
    string OwnerUsername,
    int Port,
    string? Label
);

public record PortShareStoppedEvent(
    Guid ChannelId,
    string TunnelId,
    Guid OwnerId
);

// Access token response
public record TunnelAccessResponse(
    string Url    // Full URL including access token
);
```

#### 2.2 Hub Methods

**In:** `src/Snacka.Server/Hubs/SnackaHub.cs`

```csharp
// Share a local port with voice channel members
public async Task<SharedPortInfo?> SharePort(SharePortRequest request)
{
    // 1. Validate: user is in a voice channel
    // 2. Validate: port is in range 1-65535
    // 3. Create tunnel via TunnelService
    // 4. Broadcast PortSharedEvent to voice:{channelId} group
    // 5. Return SharedPortInfo
}

// Stop sharing a port
public async Task StopSharingPort(string tunnelId)
{
    // 1. Validate: user owns this tunnel
    // 2. Remove tunnel, terminate all connections
    // 3. Broadcast PortShareStoppedEvent to voice:{channelId} group
}

// Get all shared ports in current voice channel
public async Task<IEnumerable<SharedPortInfo>> GetSharedPorts(Guid channelId)
{
    // 1. Validate: user is in this voice channel
    // 2. Return all active tunnels for the channel
}

// Request access to a shared port (returns URL to open in browser)
public async Task<TunnelAccessResponse?> RequestTunnelAccess(string tunnelId)
{
    // 1. Validate: tunnel exists
    // 2. Validate: user is in same voice channel as tunnel owner
    // 3. Generate short-lived access token
    // 4. Return full URL: /tunnel/{tunnelId}/?_tunnel_token={token}
}
```

#### 2.3 Cleanup on Voice Leave

**In:** `SnackaHub.OnDisconnectedAsync` and `LeaveVoiceChannel`:

When a user leaves voice (explicitly or via disconnect), call `TunnelService.RemoveAllTunnelsForUser(userId)` and broadcast `PortShareStoppedEvent` for each removed tunnel.

#### 2.4 Client SignalR Events

Add to `ISignalRService`:

```csharp
// Methods
Task<SharedPortInfo?> SharePortAsync(int port, string? label);
Task StopSharingPortAsync(string tunnelId);
Task<IEnumerable<SharedPortInfo>> GetSharedPortsAsync(Guid channelId);
Task<TunnelAccessResponse?> RequestTunnelAccessAsync(string tunnelId);

// Events
event Action<PortSharedEvent>? PortShared;
event Action<PortShareStoppedEvent>? PortShareStopped;
```

---

### Phase 3: Client Tunnel Service

#### 3.1 TunnelClientService

**File:** `src/Snacka.Client/Services/TunnelClientService.cs`

Manages the client side of the tunnel — connecting the control WebSocket and spawning data connections.

```csharp
public interface ITunnelClientService
{
    Task<string?> StartTunnelAsync(string tunnelId, int localPort, string serverBaseUrl, string accessToken);
    Task StopTunnelAsync(string tunnelId);
    void StopAllTunnels();
    IReadOnlyList<ActiveTunnel> GetActiveTunnels();
}

public record ActiveTunnel(string TunnelId, int LocalPort, string? Label, DateTime StartedAt);
```

**Control WebSocket loop:**
1. Connect to `wss://server/ws/tunnel/{tunnelId}/control?access_token={jwt}`
2. Listen for messages:
   - On `{"type":"open","connectionId":"..."}`:
     a. Open a data WebSocket to `wss://server/ws/tunnel/{tunnelId}/data/{connectionId}?access_token={jwt}`
     b. Open a TCP connection to `localhost:{localPort}`
     c. Bridge data WebSocket ↔ local TCP, bidirectionally
     d. On either side closing, close the other
   - On `{"type":"close","connectionId":"..."}`: close that data connection
3. Send periodic pings to keep the connection alive
4. On disconnect: attempt reconnect with backoff (3 attempts, then give up and notify user)

**Bridging** (data WebSocket ↔ local TCP):
- Read from WebSocket → write to TCP (and vice versa)
- Use binary WebSocket frames
- Buffer size: 64KB
- Handle backpressure: if one side is slow, pause reading from the other

#### 3.2 Port Detection Service

**File:** `src/Snacka.Client/Services/PortDetectionService.cs`

Detects commonly used development server ports that are currently listening.

```csharp
public interface IPortDetectionService
{
    Task<IReadOnlyList<DetectedPort>> DetectOpenPortsAsync();
}

public record DetectedPort(int Port, string? ServerType);
```

**Detection strategy:**
1. Try connecting to well-known dev ports: `3000, 3001, 4200, 5173, 5174, 8000, 8080, 8888, 6006, 4321, 5000, 5001`
2. For each port that accepts a TCP connection, try to identify the server:
   - Send a HEAD request, check `Server` header or response patterns
   - Common identifiers: "vite" in response → Vite, "next" → Next.js, etc.
3. Return list sorted by most likely dev server first
4. Timeout per port: 200ms (fast scan)

---

### Phase 4: Client UI

#### 4.1 VoiceStore Extensions

**In:** `src/Snacka.Client/Stores/VoiceStore.cs`

Add observable state for shared ports:

```csharp
// New observables
IObservable<IReadOnlyList<SharedPortState>> SharedPorts { get; }

// New actions
void AddSharedPort(SharedPortInfo port);
void RemoveSharedPort(string tunnelId);
void SetSharedPorts(IEnumerable<SharedPortInfo> ports);

public record SharedPortState(
    string TunnelId,
    Guid OwnerId,
    string OwnerUsername,
    int Port,
    string? Label,
    DateTime SharedAt,
    bool IsLocal   // true if OwnerId == current user
);
```

#### 4.2 Share Port Button (Voice Connected Panel)

**Modify:** `src/Snacka.Client/Controls/VoiceConnectedPanelView.axaml`

Add a **Share Port** toggle button in the control button row, between screen share and the video grid button:

```xml
<!-- Share Port toggle -->
<Button Command="{Binding ToggleSharePortCommand}"
        Classes="voiceControlButton"
        Classes.active="{Binding HasActivePortShares}"
        ToolTip.Tip="Share a local port">
    <PathIcon Data="{StaticResource globe_icon}" />
</Button>
```

When clicked, it opens the port picker popover.

#### 4.3 Port Picker (Popover/Flyout)

**File:** `src/Snacka.Client/Views/SharePortPickerView.axaml`
**ViewModel:** `src/Snacka.Client/ViewModels/SharePortPickerViewModel.cs`

A flyout/popup that appears above the Share Port button:

**Layout:**
- Header: "Share a local port"
- **Detected ports list** — radio button style, showing auto-detected servers
- **Manual entry** — port number input + optional label
- **Active shares list** — if already sharing, show active shares with Stop buttons
- **Share / Cancel** buttons

**ViewModel:**
```csharp
public class SharePortPickerViewModel : ViewModelBase
{
    // From port detection
    ObservableCollection<DetectedPort> DetectedPorts { get; }

    // User input
    int? SelectedPort { get; set; }
    string? Label { get; set; }

    // Active shares (for stopping)
    ObservableCollection<SharedPortState> ActiveShares { get; }

    // Commands
    ReactiveCommand<Unit, Unit> ShareCommand { get; }
    ReactiveCommand<string, Unit> StopShareCommand { get; }  // param: tunnelId
    ReactiveCommand<Unit, Unit> CancelCommand { get; }
    ReactiveCommand<Unit, Unit> RefreshDetectionCommand { get; }
}
```

#### 4.4 Shared Ports Panel (Voice Channel Content)

**Modify:** `src/Snacka.Client/Views/VoiceChannelContentView.axaml`

Add a **Shared Ports** section above or below the video grid, visible when any ports are shared in the channel:

```xml
<!-- Shared Ports Section -->
<ItemsControl Items="{Binding SharedPorts}" IsVisible="{Binding HasSharedPorts}">
    <ItemsControl.ItemTemplate>
        <DataTemplate>
            <Border Classes="sharedPortItem" Padding="8" Margin="0,4" CornerRadius="4">
                <DockPanel>
                    <PathIcon Data="{StaticResource globe_icon}" DockPanel.Dock="Left" />
                    <Button Content="Open in Browser"
                            Command="{Binding OpenInBrowserCommand}"
                            DockPanel.Dock="Right" />
                    <StackPanel Margin="8,0">
                        <TextBlock Text="{Binding DisplayName}" />
                        <TextBlock Text="{Binding OwnerUsername}"
                                   Classes="secondary" FontSize="11" />
                    </StackPanel>
                </DockPanel>
            </Border>
        </DataTemplate>
    </ItemsControl.ItemTemplate>
</ItemsControl>
```

**Display name** format: `"My Vite App (5173)"` or `"Port 5173"` if no label.

#### 4.5 Voice Participant Indicator

**Modify:** Voice participant list item template.

Add a small globe icon (🌐) next to users who are sharing ports, similar to the screen share (🖥️) and camera (🎥) indicators.

#### 4.6 Open in Browser Flow

When "Open in Browser" is clicked:

```csharp
async Task OpenSharedPortAsync(string tunnelId)
{
    // 1. Request access token via SignalR
    var response = await _signalR.RequestTunnelAccessAsync(tunnelId);
    if (response is null) { /* show error */ return; }

    // 2. Open system browser with the URL
    Process.Start(new ProcessStartInfo(response.Url) { UseShellExecute = true });
}
```

#### 4.7 Notifications

When a port is shared by another user, show a brief non-intrusive notification in the voice channel content area (not a system notification — keep it contextual):

```
┌────────────────────────────────────────────────────┐
│ 🌐 Alice shared "My Vite App" (port 5173)         │
│                                    [Open in Browser]│
└────────────────────────────────────────────────────┘
```

This appears as a temporary banner (auto-dismiss after 10 seconds) and then the port remains in the shared ports list.

---

### Phase 5: Coordinator & Event Wiring

#### 5.1 PortForwardCoordinator

**File:** `src/Snacka.Client/Coordinators/PortForwardCoordinator.cs`

Orchestrates between SignalR, TunnelClientService, and VoiceStore (following the VoiceCoordinator pattern):

```csharp
public class PortForwardCoordinator : IDisposable
{
    Task<SharedPortInfo?> SharePortAsync(int port, string? label);
    Task StopSharingPortAsync(string tunnelId);
    Task StopAllSharesAsync();
    Task OpenSharedPortInBrowserAsync(string tunnelId);
}
```

**Lifecycle:**
- Created when joining voice channel
- Subscribes to `PortShared` / `PortShareStopped` SignalR events
- Updates VoiceStore on events
- Fetches existing shared ports when joining a channel (`GetSharedPorts`)
- Disposed when leaving voice channel → stops all local tunnels

#### 5.2 SignalR Event Wiring

**In:** `src/Snacka.Client/ViewModels/SignalRUiEventManager.cs`

Wire up port sharing events:

```csharp
_signalR.PortShared += OnPortShared;
_signalR.PortShareStopped += OnPortShareStopped;

private void OnPortShared(PortSharedEvent evt)
{
    _voiceStore.AddSharedPort(new SharedPortInfo(...));
}

private void OnPortShareStopped(PortShareStoppedEvent evt)
{
    _voiceStore.RemoveSharedPort(evt.TunnelId);
}
```

---

## Security Considerations

### Access Control
- **Voice channel membership check**: Every tunnel access request verifies the user is currently in the same voice channel as the tunnel owner. Checked both at token generation and at cookie validation.
- **Short-lived tokens**: Access tokens expire in 5 minutes. They're only used to establish a session cookie.
- **Session cookies**: Scoped to the tunnel path (`/tunnel/{tunnelId}/`), HTTP-only, 30-minute TTL. Automatically invalidated when the tunnel is destroyed.
- **Tunnel teardown on voice leave**: All tunnels are destroyed when the owner leaves voice, severing all active connections immediately.

### Rate Limiting
- Tunnel creation: max 5 active tunnels per user
- Data connections per tunnel: max 50 concurrent
- Access token generation: max 30/minute per user

### Abuse Prevention
- Only voice channel members can access — no public URLs
- Server can monitor bandwidth per tunnel and terminate if excessive
- Tunnels are ephemeral — no permanent infrastructure exposure

### Privacy
- Traffic passes through the server (encrypted via TLS on both hops)
- Server does not inspect or log tunnel content
- Only metadata is stored in memory: tunnel owner, port, channel, creation time

---

## Implementation Order

**Recommended order to implement, with each step producing a testable increment:**

1. **`TunnelService`** — In-memory tunnel registry, access token generation/validation
2. **Tunnel WebSocket endpoints** — Control + data WebSocket handlers
3. **`TunnelProxyMiddleware`** — HTTP proxy with cookie auth
4. **SignalR hub methods** — SharePort, StopSharingPort, GetSharedPorts, RequestTunnelAccess
5. **Cleanup on voice leave** — Wire into existing OnDisconnectedAsync
6. **Client `TunnelClientService`** — Control WebSocket loop, data WebSocket bridging
7. **Client `PortDetectionService`** — Auto-detect listening ports
8. **Client SignalR event wiring** — PortShared/PortShareStopped events
9. **VoiceStore extensions** — SharedPorts observable state
10. **`PortForwardCoordinator`** — Orchestration layer
11. **Share Port button + picker UI** — Voice panel button and port selection flyout
12. **Shared Ports panel** — Voice channel content area showing active shares
13. **Open in Browser flow** — Token request → browser launch
14. **Participant indicator** — Globe icon in voice participant list

---

## Future Enhancements

- **HTTPS tunnel endpoints** — Allow the tunnel to present as HTTPS to the browser (self-signed cert or Let's Encrypt)
- **Bandwidth monitoring UI** — Show real-time bandwidth usage per tunnel
- **Port range sharing** — Share a range of ports (e.g., 3000-3010) for multi-service setups
- **Tunnel persistence** — Option to keep a tunnel alive outside of voice (with separate access control)
- **Custom labels with auto-detection** — Read `package.json` scripts to auto-label ("vite dev", "next dev", etc.)
- **Collaborative URL bar** — Show what URL each viewer is currently browsing
- **Notification when someone visits** — Toast when another user opens your shared port
