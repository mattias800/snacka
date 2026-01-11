# Snacka - Complete Feature Implementation Plan

## Overview
Implement a self-hosted Discord alternative with user accounts, messaging, voice channels, and media streaming. Built with ASP.NET Core 9 backend, Avalonia UI desktop client, and WebRTC for real-time communication.

## Current Status

### Completed ✅
- ✅ Project structure with Shared/Server/Client/WebRTC libraries
- ✅ Avalonia UI 11.1.3 integrated for cross-platform desktop
- ✅ Database schema designed with 7 entities
- ✅ EF Core DbContext fully configured
- ✅ Build pipeline working (zero errors)
- ✅ Git repository on GitHub (private)
- ✅ User authentication (register, login, JWT tokens, refresh tokens)
- ✅ SignalR hub with real-time messaging and presence
- ✅ Direct messages (send, receive, edit, delete, typing indicators)
- ✅ Text channels (create, edit, delete, messages)
- ✅ Voice channels (create, join, leave - UI and signaling only)
- ✅ Complete Discord-like UI (server list, channels, chat, members)
- ✅ Role-based permissions (Owner, Admin, Member)
- ✅ Ownership transfer feature
- ✅ 75+ automated tests

### Implementation Progress
- **Database Models**: 100% ✅
- **Authentication**: 100% ✅
- **Messaging (DM + Channels)**: 100% ✅
- **Voice Signaling**: 100% ✅ (UI + SignalR + WebRTC audio complete)
- **Voice & Media**: 60% (voice audio complete, webcam/screen share pending)
- **UI**: 95% ✅ (main app complete, voice UI complete with settings, video grid pending)

## Phase 1: Core Infrastructure & User Management

### 1.1 Database Setup [COMPLETED]
- ✅ Create EF Core models for users, channels, messages, and relationships
- ✅ Configure DbContext with proper relationships and cascading deletes
- ✅ Set up unique constraints on email/username
- ✅ Configure composite indexes for performance

**Deliverables:**
- 7 database entities with proper foreign keys
- Migration-ready DbContext
- No build errors

### 1.2 User Accounts [COMPLETED] ✅
**Implemented in:** `src/Snacka.Server/Controllers/AuthController.cs`, `src/Snacka.Server/Services/AuthService.cs`

#### Endpoints Implemented:
- ✅ `POST /api/auth/register` - User registration with email/password validation
- ✅ `POST /api/auth/login` - User login with JWT token generation
- ✅ `POST /api/auth/refresh` - Refresh expired tokens
- ✅ `GET /api/auth/profile` - Get current user profile

#### Implementation Details:
- ✅ BCrypt for password hashing
- ✅ JWT token generation with configurable expiration
- ✅ Token refresh mechanism
- ✅ Email/password validation
- ✅ Authentication middleware for protected routes
- ✅ User online status tracking via SignalR

### 1.3 SignalR Hub Setup [COMPLETED] ✅
**Implemented in:** `src/Snacka.Server/Hubs/SnackaHub.cs`

#### Hub Methods Implemented:
- ✅ Connection handling (OnConnectedAsync, OnDisconnectedAsync)
- ✅ User online status broadcasting
- ✅ Connection authentication via JWT
- ✅ User presence tracking
- ✅ Community/channel group management
- ✅ Real-time message delivery
- ✅ Voice channel signaling (join/leave/WebRTC)

---

## Phase 2: Messaging Features

### 2.1 Direct Messages [COMPLETED] ✅
**Implemented in:** `src/Snacka.Server/Controllers/DirectMessagesController.cs`, `src/Snacka.Server/Services/DirectMessageService.cs`

#### Endpoints Implemented:
- ✅ `GET /api/directmessages` - Get list of DM conversations
- ✅ `GET /api/directmessages/{userId}` - Get DM history with specific user
- ✅ `POST /api/directmessages/{userId}` - Send direct message
- ✅ `PUT /api/directmessages/{messageId}` - Edit message
- ✅ `DELETE /api/directmessages/{messageId}` - Delete message
- ✅ `POST /api/directmessages/{userId}/read` - Mark conversation as read

#### SignalR Events Implemented:
- ✅ Real-time DM delivery
- ✅ Typing indicators
- ✅ Message edit/delete notifications
- ✅ Unread count tracking

### 2.2 Text Channels [COMPLETED] ✅
**Implemented in:** `src/Snacka.Server/Controllers/ChannelsController.cs`, `src/Snacka.Server/Services/CommunityService.cs`

#### Endpoints Implemented:
- ✅ `GET /api/communities/{id}/channels` - List channels
- ✅ `POST /api/communities/{id}/channels` - Create channel
- ✅ `PUT /api/channels/{id}` - Update channel
- ✅ `DELETE /api/channels/{id}` - Delete channel
- ✅ `GET /api/channels/{id}/messages` - Get message history (paginated)
- ✅ `POST /api/channels/{id}/messages` - Post message
- ✅ `PUT /api/channels/{channelId}/messages/{messageId}` - Edit message
- ✅ `DELETE /api/channels/{channelId}/messages/{messageId}` - Delete message

#### SignalR Events Implemented:
- ✅ Real-time message delivery
- ✅ Channel created/updated/deleted notifications
- ✅ Typing indicators
- ✅ Message edit/delete notifications

#### Additional Features:
- ✅ Role-based permissions (Owner, Admin, Member)
- ✅ Ownership transfer
- ✅ Channel types (Text, Voice)

---

## Phase 3: Voice & Media Communication

### 3.1 Voice Channels & WebRTC Signaling [MOSTLY COMPLETE] ✅
**Implemented in:** `src/Snacka.Server/Hubs/SnackaHub.cs`, `src/Snacka.Client/Services/SignalRService.cs`, `src/Snacka.Client/Services/WebRtcService.cs`

#### Completed ✅:
- ✅ Voice channel creation (same as text channels with Type=Voice)
- ✅ Join/Leave voice channel UI and SignalR events
- ✅ Participant tracking in database (VoiceParticipants table)
- ✅ Mute/Deafen state management
- ✅ SignalR WebRTC signaling events (offer, answer, ICE candidates)
- ✅ Participant joined/left notifications
- ✅ Voice state updates (mute, deafen, camera, screen share)
- ✅ **WebRTC audio capture and playback** - SDL2 audio source/sink with SipSorcery
- ✅ Audio codec negotiation (PCMU)
- ✅ Voice activity detection with speaking indicator

#### Remaining ❌:
- ❌ STUN/TURN server configuration (for NAT traversal)

**Current State:** Voice channels fully functional with audio. Users can join channels, speak, and hear each other. Voice activity is indicated in the UI.

### 3.2 Webcam Streaming [NOT STARTED]
**Estimated: 600-800 lines of code**

#### Client-Side Implementation (Avalonia):
- Enumerate available cameras via OS APIs
- Capture video frames from selected camera
- Add video track to WebRTC peer connection
- Render local preview video stream
- Render remote participant video streams in grid layout

#### Server-Side Support:
- Track camera status per participant
- Coordinate video capability negotiation between peers
- Handle camera selection/switching

#### Implementation Details:
- Use OS-native camera APIs (macOS AVFoundation, Windows WinRT, Linux libcamera)
- H.264 or VP8 video codec
- Adaptive bitrate control based on network
- Audio track management (opus codec)
- Mute/unmute functionality
- Display participant names on video tiles
- Handle camera permission requests gracefully

**Dependencies:**
- SipSorcery for video track handling
- OS-specific camera APIs via interop

### 3.3 Screen Sharing [NOT STARTED]
**Estimated: 800-1000 lines of code**

#### Client-Side Implementation (Avalonia):
- Enumerate available displays
- Enumerate available capture devices (including Elgato 4K)
- Capture screen/display frames at high resolution
- Add screen share track to WebRTC connection
- Switch between camera and screen share
- Display screen share to remote participants

#### Device Support:
- macOS: ScreenCaptureKit (Sonoma 14+) or legacy APIs
- Windows: DXGI Desktop Duplication
- Linux: X11/Wayland screen capture
- Elgato 4K Capture: USB device enumeration, video4linux2 on Linux, AVFoundation on macOS

#### Implementation Details:
- Support multiple simultaneous displays
- Handle display hotplug/disconnect
- High-resolution capture (4K support)
- Configurable frame rate (30fps standard)
- Option to capture specific window
- Pause/stop screen share controls
- Audio from shared application (optional)
- Cursor capture and overlay

**Dependencies:**
- SipSorcery for screen share track
- Platform-specific screen capture libraries
- Device enumeration libraries

---

## Phase 4: UI Implementation

### 4.1 Authentication UI [COMPLETED] ✅
**Implemented in:** `src/Snacka.Client/Views/LoginView.axaml`, `src/Snacka.Client/Views/RegisterView.axaml`, `src/Snacka.Client/Views/ServerConnectionView.axaml`

#### Views Implemented:
1. ✅ **ServerConnectionView** - Connect to server URL
2. ✅ **LoginView** - Email/password login with error handling
3. ✅ **RegisterView** - Username/email/password registration
4. ✅ **LoadingView** - Loading indicator during async operations

#### Features:
- ✅ CLI argument support for auto-login (--server, --email, --password)
- ✅ Error message display
- ✅ Loading indicators
- ✅ View navigation between login/register
- ✅ Token management via AuthService

### 4.2 Main Application Layout [COMPLETED] ✅
**Implemented in:** `src/Snacka.Client/Views/MainAppView.axaml`, `src/Snacka.Client/ViewModels/MainAppViewModel.cs`

#### Main Window Structure (Implemented):
```
┌─────────────────────────────────────────┐
│     Snacka - Username              [_][□][X]  │
├─────────────────────────────────────────┤
│ Servers │ Channels │    Chat Area    │ Users │
│   [S1]  │ # general│ Messages Here   │ User1 │
│   [S2]  │ # random │                 │ User2 │
│   [+]   │ 🔊 Voice │ [input field]   │ User3 │
│   [DM]  │   [+]    │                 │       │
└─────────────────────────────────────────┘
```

#### Components Implemented ✅:
1. ✅ **Community List** - Server icons, create/join, DM access
2. ✅ **Channel List** - Text (#) and Voice (🔊) channels, create buttons (permission-based)
3. ✅ **Chat Panel** - Messages with author/timestamp, edit/delete, input field
4. ✅ **Member List** - Online status, roles displayed, context menu (DM, promote, demote, transfer ownership)
5. ✅ **Direct Messages View** - Conversation list, chat interface

#### MVVM Implementation ✅:
- ✅ MainAppViewModel with ReactiveUI
- ✅ Converters for roles, timestamps, unread counts
- ✅ Command bindings throughout

### 4.3 Voice UI [MOSTLY COMPLETE] ✅
**Implemented in:** `src/Snacka.Client/Views/MainAppView.axaml` (voice channel section), `src/Snacka.Client/Views/AudioSettingsView.axaml`

#### Completed ✅:
- ✅ Voice channel list with participant count
- ✅ Join/Leave voice channel buttons
- ✅ Voice control bar (mute, deafen, disconnect)
- ✅ Current voice channel indicator
- ✅ Participant list in voice channel
- ✅ Voice activity indicator (username highlights when speaking)
- ✅ **Audio device selection UI** - Input/output device dropdowns in Settings
- ✅ **Input gain control** (0-300%) - Amplify or reduce microphone input
- ✅ **Noise gate** - Mute audio below threshold to reduce background noise
- ✅ **Mic test with level indicator** - Visual feedback with gate threshold marker
- ✅ **Loopback test** - Hear yourself to verify audio quality

#### Remaining ❌:
- ❌ Video grid for webcam/screen share
- ❌ Volume controls per participant

---

## Phase 5: Advanced Features & Polish

### 5.1 Additional Backend Features [NOT STARTED]
**Estimated: 500-700 lines of code**

- User server invites system
- Role-based permissions (Admin, Moderator, Member)
- Message search across server
- Audit logging for server actions
- Rate limiting on API endpoints
- Batch loading optimization (DataLoader pattern)
- Caching strategy for frequently accessed data

### 5.2 Client Enhancements [NOT STARTED]
**Estimated: 400-600 lines of code**

- Auto-reconnection on network loss
- Offline message queuing
- Message persistence cache
- Performance optimization (virtualization, lazy loading)
- Error handling and user notifications
- App state persistence (open channels, window size)
- Keyboard shortcuts

### 5.3 Testing [NOT STARTED]
**Estimated: 500-800 lines of code**

- Unit tests for all services (target: >80% coverage)
- Integration tests for SignalR hub methods
- WebRTC connection tests
- UI component tests with ReactiveUI
- End-to-end testing (if time permits)

**Testing Framework:** MSTest ✅ (already configured)

---

## Technical Implementation Details

### Backend Stack
- **ASP.NET Core 9** - Web framework ✅
- **Entity Framework Core 9** - ORM with DbContext ✅
- **SignalR** - Real-time communication
- **SipSorcery** - WebRTC implementation
- **BCrypt.Net-Next 4.0.3** - Password hashing ✅
- **JWT (System.IdentityModel.Tokens.Jwt)** - Authentication
- **Swagger/OpenAPI** - API documentation ✅

### Client Stack
- **Avalonia UI 11.1.3** - Cross-platform UI ✅
- **ReactiveUI** - MVVM pattern ✅
- **WebRTC Client** - Peer connection management
- **HTTP Client** - REST API communication
- **SignalR Client** - Real-time updates

### Database Schema (7 Tables)

```
Users
├─ Id (PK)
├─ Username (Unique)
├─ Email (Unique)
├─ PasswordHash
├─ Avatar
├─ Status
├─ IsOnline
├─ CreatedAt
└─ UpdatedAt

SnackaServers
├─ Id (PK)
├─ Name
├─ Description
├─ OwnerId (FK → Users)
├─ Icon
├─ CreatedAt
└─ UpdatedAt

Channels
├─ Id (PK)
├─ Name
├─ Topic
├─ ServerId (FK → SnackaServers)
├─ Type (Text/Voice)
├─ Position
├─ CreatedAt
└─ UpdatedAt

Messages
├─ Id (PK)
├─ Content
├─ AuthorId (FK → Users)
├─ ChannelId (FK → Channels)
├─ CreatedAt
└─ UpdatedAt

DirectMessages
├─ Id (PK)
├─ Content
├─ SenderId (FK → Users)
├─ RecipientId (FK → Users)
├─ CreatedAt
└─ IsRead

UserServers (Junction)
├─ Id (PK)
├─ UserId (FK → Users)
├─ ServerId (FK → SnackaServers)
├─ Role (Owner/Admin/Moderator/Member)
└─ JoinedAt

VoiceParticipants
├─ Id (PK)
├─ UserId (FK → Users)
├─ ChannelId (FK → Channels)
├─ IsMuted
├─ IsDeafened
├─ IsScreenSharing
├─ IsCameraOn
└─ JoinedAt
```

---

## Implementation Order & Dependencies

1. **Phase 1.2** - User Authentication (prerequisite for everything)
   - Register/Login endpoints
   - JWT token generation
   - Password hashing

2. **Phase 1.3** - SignalR Hub Setup (prerequisite for messaging)
   - Hub configuration
   - Connection management
   - Online status tracking

3. **Phase 2.1** - Direct Messages (simplest messaging feature)
   - DM endpoints
   - SignalR DM events
   - DM history

4. **Phase 2.2** - Text Channels (builds on DM infrastructure)
   - Channel management
   - Channel messages
   - Member management

5. **Phase 4.1** - Authentication UI (needed before app is usable)
   - Login window
   - Register window
   - Session management

6. **Phase 4.2** - Main App Layout (basic chat UI)
   - Server list
   - Channel list
   - Chat display and input

7. **Phase 3.1** - Voice Channels & WebRTC (complex but isolated)
   - WebRTC signaling
   - SDP/ICE candidate handling
   - Participant tracking

8. **Phase 4.3** - Voice UI (depends on Phase 3.1)
   - Video grid
   - Control buttons
   - Participant management

9. **Phase 3.2** - Webcam Streaming (depends on Phase 3.1)
   - Camera enumeration
   - Video track integration
   - Local/remote video rendering

10. **Phase 3.3** - Screen Sharing (depends on Phase 3.1 & 3.2)
    - Screen capture
    - Device enumeration (Elgato support)
    - Screen share controls

11. **Phase 5** - Testing & Polish (throughout, but formalized here)
    - Unit tests for all services
    - Integration tests
    - Performance optimization

---

## Implementation Summary

| Phase | Component | Status | Notes |
|-------|-----------|--------|-------|
| 1.1 | Database | ✅ Complete | All models and migrations |
| 1.2 | Auth Backend | ✅ Complete | Register, Login, JWT |
| 1.3 | SignalR Hub | ✅ Complete | Connection management |
| 2.1 | Direct Messages | ✅ Complete | Endpoints + SignalR |
| 2.2 | Text Channels | ✅ Complete | Messages, permissions, roles |
| 4.1 | Auth UI | ✅ Complete | Login/Register windows |
| 4.2 | Main Layout | ✅ Complete | Discord-like interface |
| 3.1 | Voice/WebRTC | ✅ Complete | Audio fully working |
| 4.3 | Voice UI | ✅ Complete | Device selection, gain, noise gate |
| 3.2 | Webcam | ⏳ Pending | Camera APIs per OS |
| 3.3 | Screen Share | ⏳ Pending | Platform-specific capture |
| 5 | Testing/Polish | ⏳ Ongoing | 75+ tests, targeting >80% coverage |

---

## Success Criteria

- ✅ All 6 core features implemented and working
- ✅ Real-time messaging delivery via SignalR
- ✅ WebRTC voice calls establishing successfully
- ✅ Webcam video streams displaying in UI
- ✅ Screen sharing functional with device detection
- ✅ Cross-platform client (Windows, macOS, Linux)
- ✅ Self-hosted server deployment ready
- ✅ All tests passing with >80% coverage
- ✅ No build errors or warnings
- ✅ Database migrations included for fresh deployments

---

## Key Architecture Decisions

### Why Avalonia over MAUI?
- MAUI has poor macOS/Linux support
- Avalonia proven in production (JetBrains Rider)
- XAML-based, familiar to WPF developers
- Cross-platform performance excellent

### Why SipSorcery for WebRTC?
- Pure C# implementation
- Comprehensive WebRTC support
- Active maintenance and community
- No native dependencies required

### Why SignalR for Real-Time?
- Built into ASP.NET Core
- Automatic fallback to polling if WebSocket fails
- Integrated with Dependency Injection
- Excellent documentation and examples

### Database: SQL Server vs PostgreSQL
- Both fully supported via EF Core
- Migrations work identically
- Choose based on deployment preference
- SQL Server recommended for Windows hosts
- PostgreSQL recommended for Linux/cloud hosts

---

## Notes for Future Developers

### Before Starting Each Phase:
1. Read the phase description fully
2. Understand all dependencies
3. Plan the API contract before coding
4. Write tests as you code, not after
5. Commit frequently (after each logical unit)

### Critical Gotchas:
1. **WebRTC is complex** - Plan extra time for Phase 3.1
2. **Screen capture is OS-specific** - Need platform-specific implementations
3. **Cross-platform testing is essential** - Test on Windows, macOS, Linux
4. **SignalR groups are powerful** - Use them for channel message broadcasting
5. **JWT expiration handling** - Implement token refresh properly
6. **Elgato enumeration** - Requires USB device enumeration library
7. **Performance matters for video** - Virtualization and lazy loading are critical

### Recommended Libraries to Add Later:
- `AutoMapper` for entity/DTO mapping
- `FluentValidation` for complex validation
- `Serilog` for structured logging
- `Polly` for resilience and retry policies
- `MediatR` if CQRS pattern desired (optional)

---

## Repository Structure

```
snacka/
├── src/
│   ├── Snacka.Server/
│   │   ├── Controllers/          (REST endpoints)
│   │   ├── Hubs/                 (SignalR hubs)
│   │   ├── Services/             (business logic)
│   │   ├── Data/                 (DbContext ✅)
│   │   ├── DTOs/                 (data transfer objects)
│   │   ├── Middleware/           (auth, error handling)
│   │   ├── Migrations/           (EF Core migrations)
│   │   └── Program.cs
│   ├── Snacka.Client/
│   │   ├── Views/                (XAML windows ✅)
│   │   ├── ViewModels/           (MVVM VMs)
│   │   ├── Services/             (HTTP, SignalR clients)
│   │   ├── Models/               (UI models)
│   │   ├── Converters/           (value converters)
│   │   ├── App.axaml ✅
│   │   └── Program.cs ✅
│   ├── Snacka.Shared/
│   │   └── Models/               (entities ✅)
│   ├── Snacka.WebRTC/
│   │   ├── Handlers/             (WebRTC peer handling)
│   │   └── Services/             (media capture, encoding)
│   ├── SnackaCapture/            (screen/window capture)
│   └── SnackaMetalRenderer/      (Metal-based rendering, macOS)
├── tests/
│   ├── Snacka.Server.Tests/
│   └── Snacka.WebRTC.Tests/
├── tools/                        (build and dev tools)
├── docs/
│   └── DEPLOY.md                 (deployment guide)
├── .github/
│   └── workflows/                (CI/CD)
├── Dockerfile ✅
├── docker-compose.yml ✅
├── dev-start.sh                  (quick start script)
├── PLAN.md ✅
├── AGENTS.md ✅
├── README.md ✅
└── Snacka.sln ✅
```

---

## Additional Resources

### Learning Materials:
- SignalR Documentation: https://learn.microsoft.com/en-us/aspnet/core/signalr/
- WebRTC Overview: https://webrtc.org/
- SipSorcery GitHub: https://github.com/sipsorcery-org/sipsorcery
- Avalonia Documentation: https://docs.avaloniaui.net/
- Entity Framework Core: https://learn.microsoft.com/en-us/ef/core/

### Reference Projects:
- Discord Clone implementations on GitHub
- SipSorcery examples directory
- Avalonia sample applications
- SignalR chat application sample

---

## Next Steps

### Immediate Priority
1. ✅ ~~**Complete WebRTC Audio**~~ - DONE: Voice channels fully functional with SDL2 audio
2. **Add STUN/TURN Configuration** - For NAT traversal in voice calls across networks

### Secondary Priority
3. **Webcam Streaming** - Add video tracks to WebRTC peer connections
4. **Screen Sharing** - Platform-specific screen capture
5. **Volume controls per participant** - Individual volume sliders for each user in voice channel

### Polish
6. ✅ ~~**Audio Device Selection UI**~~ - DONE: Input/output device selection in Settings
7. ✅ ~~**Input Gain Control**~~ - DONE: 0-300% gain slider
8. ✅ ~~**Noise Gate**~~ - DONE: Threshold-based muting for background noise
9. **Improve Test Coverage** - Currently 75 tests, aim for >80% coverage
10. **Performance Optimization** - Message virtualization, lazy loading

---

**Last Updated:** 2026-01-11
**Status:** Core Features Complete, Voice Audio Fully Functional
**Maintainer:** Development Team
