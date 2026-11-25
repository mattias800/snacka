# Text Channel & Direct Message Improvement Plan

This document outlines planned improvements for the text channel and direct message experience in Snacka.

## Current State

### Text Channels
- Basic send/receive with real-time SignalR updates
- Message editing with "(edited)" indicator
- Message deletion (own messages + admin permissions)
- Markdown support with syntax-highlighted code blocks
- User avatars, timestamps, and presence indicators
- Pagination support exists but UI doesn't utilize it (always loads 50)

### Direct Messages
- Same core messaging features as text channels
- Conversation list with last message preview
- Unread count badges and "NEW MESSAGES" separator
- Read receipts (bulk - marks all as read when conversation opened)
- Online/offline status indicator on avatars

---

## Improvement Roadmap

### Phase 1: Quick Wins (Client-side only) ✅ COMPLETED

These improvements require minimal changes and can be implemented quickly:

#### 1.1 Date Separators ✅
- [x] Group messages by day in the chat view
- [x] Show dividers with "Today", "Yesterday", or full date (e.g., "January 5, 2026")
- [x] Apply to both text channels and DMs

#### 1.2 Selected Conversation Highlight ✅
- [x] Add visual distinction for the currently selected DM conversation
- [x] Background color change on selected state

#### 1.3 Relative Timestamps ✅
- [x] Show "Just now", "2 min ago", "1 hour ago" for recent messages
- [x] Show full timestamp on hover via tooltip
- [x] Fall back to full date for messages older than 24 hours

#### 1.4 Message Formatting Toolbar ✅
- [x] Add toolbar above message input with Bold, Italic, Code buttons
- [x] Insert markdown syntax when clicked
- [x] Help users who don't know markdown shortcuts

---

### Phase 2: High Impact Features ✅ COMPLETED

These features significantly improve the core messaging experience:

#### 2.1 Typing Indicators ✅
- [x] Show "User is typing..." when someone is composing a message
- [x] Throttled SignalR events (send every ~3 seconds while typing)
- [x] Auto-dismiss after ~5 seconds of inactivity
- [x] Support multiple users typing simultaneously in channels

#### 2.2 Unread Indicators for Text Channels ✅
- [x] Track last read message per user per channel (server-side)
- [x] Show unread badge on channel list in sidebar
- [ ] Display "NEW MESSAGES" separator when returning to a channel
- [x] API endpoint to mark channel as read

#### 2.3 Message Replies/Threading ✅
- [x] Add "Reply" button on message hover
- [x] Show quoted preview above the reply message
- [ ] Click preview to jump to original message
- [ ] Visual connection line between reply and original

#### 2.4 Link Previews ✅
- [x] Parse URLs in message content
- [x] Make URLs clickable (opens in browser)
- [x] Style URLs with blue color and underline
- [x] Fetch OpenGraph metadata (title, description, image)
- [x] Display preview card below message
- [x] Server-side proxy to avoid CORS issues
- [x] Support YouTube embeds (via oEmbed API)

#### 2.5 User Mentions (@username) ✅
- [x] Autocomplete dropdown when typing `@`
- [x] Filter by partial username match
- [x] Highlight mentions in rendered message
- [ ] Notification/ping when user is mentioned
- [ ] `@everyone` and `@here` for channels (admin only)

---

### Phase 3: Medium Impact Features

Polish and enhanced functionality:

#### 3.1 Message Reactions ✅
- [x] Add emoji reactions to any message
- [x] Show reaction counts below message
- [x] Tooltip showing who reacted
- [x] Emoji picker UI (common emojis)
- [x] Database model for reactions
- [x] Real-time sync via SignalR
- [ ] Emoji search in picker
- [ ] Custom emoji reactions (requires 3.2 File Attachments)

#### 3.2 File Attachments ✅
- [x] Upload images and files with messages
- [x] Drag-and-drop support
- [x] Image previews inline (lightbox on click)
- [x] Download links for non-image files
- [x] File size limits and validation (25MB max, configurable extensions)
- [x] Server-side storage (local directory with GUID-based filenames)
- [x] Audio file playback with inline player
  - [x] Play/pause, progress bar, seek functionality
  - [x] Current time and total duration display
  - [x] Volume control slider
  - [x] Uses LibVLCSharp (requires VLC on macOS arm64)

#### 3.3 Infinite Scroll / Message History
- [ ] Implement scroll-to-load-more for older messages
- [ ] Use existing pagination API (skip/take)
- [ ] "Jump to present" button when scrolled up
- [ ] Loading indicator while fetching
- [x] Smart auto-scroll (scroll to new messages only when at bottom, preserve position when reading older messages)

#### 3.4 Message Search
- [ ] Search within current channel or DM
- [ ] Full-text search across all messages
- [ ] Filter by user, date range
- [ ] Jump to message in context
- [ ] Search results highlighting

#### 3.5 Pinned Messages ✅
- [x] Pin important messages to a channel
- [x] "Pinned" button in channel header
- [x] Pinned messages panel/modal
- [x] Unpin option for admins/message author

---

### Phase 4: Nice to Have

Lower priority enhancements:

#### 4.1 Delivery Status Indicators
- [ ] Checkmarks showing message status: sent → delivered → read
- [ ] Particularly useful for DMs
- [ ] Server acknowledgment on receive

#### 4.2 GIF Support ✅
- [x] Integration with Tenor API (server-side proxy to hide API key)
- [x] Picker UI in message composer (popup with search and trending GIFs)
- [x] GIF search and trending endpoints
- [x] Inline GIF display (detects Tenor URLs, renders inline)
- [x] Image caching for performance
- [ ] Sticker packs (future enhancement)

#### 4.3 Voice/Video Calls in DMs
- [ ] Add call button in DM conversation header
- [ ] 1-on-1 voice call using existing WebRTC infrastructure
- [ ] Video call support
- [ ] Call history

#### 4.4 Draft Messages
- [ ] Auto-save unsent message when switching channels
- [ ] Restore draft when returning to channel
- [ ] Visual indicator that draft exists
- [ ] Local storage (not server-side)

#### 4.5 Compact Mode
- [ ] Toggle for denser message display
- [ ] Hide avatars, reduce vertical spacing
- [ ] User preference saved in settings

#### 4.6 Keyboard Navigation
- [ ] Arrow keys to navigate messages
- [ ] `E` to edit selected message
- [ ] `R` to reply to selected message
- [ ] `Delete` to delete selected message
- [ ] `Ctrl+K` / `Cmd+K` for quick channel/DM switcher
- [ ] `Escape` to deselect/cancel

---

## Implementation Notes

### Files to Modify (Text/DM Focus)

**Client-side:**
- `src/Snacka.Client/Views/MainAppView.axaml` - Channel chat UI
- `src/Snacka.Client/Views/DirectMessagesView.axaml` - DM chat UI
- `src/Snacka.Client/ViewModels/MainAppViewModel.cs` - Channel logic
- `src/Snacka.Client/ViewModels/DirectMessagesViewModel.cs` - DM logic
- `src/Snacka.Client/Converters/` - New converters for timestamps, etc.

**Server-side (for features requiring backend):**
- `src/Snacka.Server/Services/MessageService.cs`
- `src/Snacka.Server/Services/DirectMessageService.cs`
- `src/Snacka.Server/Hubs/SnackaHub.cs` - SignalR events
- `src/Snacka.Shared/Models/` - New models for reactions, etc.

### Coordination with Other Agents

Another agent is currently working on server, community, and user account management. To avoid conflicts:

1. **File ownership**: This plan focuses on messaging UI (Views, ViewModels, Converters)
2. **Shared files**: Avoid `Program.cs`, `SnackaDbContext.cs`, `User.cs` unless necessary
3. **Communication**: Check `git status` before editing shared files
4. **Small commits**: Make focused, atomic commits for easier merging

---

## Progress Tracking

### Completed
- [x] Initial plan created
- [x] Phase 1: Quick Wins
  - [x] Date separators (shows "Today", "Yesterday", or full date)
  - [x] Selected conversation highlight (DMs show selected state)
  - [x] Relative timestamps with full timestamp tooltip
  - [x] Message formatting toolbar (Bold, Italic, Code buttons)
- [x] Phase 2: High Impact Features
  - [x] Typing indicators (shows "User is typing..." with throttling)
  - [x] Unread indicators for text channels (badge on channel list)
  - [x] Message replies/threading (reply button, preview in messages)
  - [x] Link previews (OpenGraph metadata, YouTube embeds)
  - [x] User mentions (autocomplete dropdown, partial match filter, highlight in messages)
  - [x] Smart auto-scroll (preserve position when reading old messages)

### In Progress
- [ ] Phase 3: Medium Impact Features
  - [x] Message reactions (emoji picker, counts, tooltips, real-time sync)
  - [x] File attachments (upload, drag-drop, image lightbox, audio player with seek/volume)
  - [x] Pinned messages (pin/unpin, pinned button in header, popup panel)

### Not Started
- [ ] Phase 4: Nice to Have
  - [x] GIF support (Tenor API integration, picker UI, inline display)
