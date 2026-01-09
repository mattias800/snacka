# Channel List Improvement Plan

This document outlines planned improvements for the text and voice channel list in Miscord.

## Current State

### Text Channels
- [x] Create text channels (admin)
- [x] Select/switch channels
- [x] Rename channels (admin)
- [x] Unread message count badges
- [x] Keyboard shortcuts for rename (Enter/Escape)

### Voice Channels
- [x] Create voice channels (admin)
- [x] Join/leave voice channels
- [x] Voice participant list with avatars
- [x] Per-user volume control (0-200%)
- [x] Server mute/deafen (admin)
- [x] Speaking state indicators
- [x] Mute/deafen/camera status icons

---

## Improvement Roadmap

### Phase 1: High Priority (User Experience)

#### 1.1 Channel Deletion
- [ ] Right-click context menu with "Delete Channel" option
- [ ] Confirmation dialog to prevent accidents
- [ ] Different confirmation for channels with messages vs empty channels
- [ ] Server-side deletion endpoint

#### 1.2 Drag & Drop Channel Reordering
- [ ] Allow admins to drag channels to reorder them
- [ ] Visual drop indicator during drag
- [ ] Persist order to server via Position field
- [ ] Smooth animation during reorder

#### 1.3 Channel Categories/Folders
- [ ] Collapsible category headers
- [ ] Create/rename/delete categories
- [ ] Drag channels between categories
- [ ] Category-level permissions

#### 1.4 Search/Filter Channels
- [ ] Search box at top of channel list
- [ ] Filter by name as you type
- [ ] Highlight matching text
- [ ] Keyboard shortcut (Ctrl+K) for quick switcher

---

### Phase 2: Visual Enhancements

#### 2.1 Channel Topic Preview
- [ ] Show tooltip with channel topic on hover
- [ ] Show first line of topic below channel name (truncated)
- [ ] Edit topic inline or in modal

#### 2.2 Mention/Notification Badges
- [ ] Current red badge for unread count
- [ ] Different indicator for @mentions (e.g., @ symbol)
- [ ] Highlight for @everyone/@here pings
- [ ] Clear visual hierarchy between types

#### 2.3 User Avatars in Voice Channels
- [ ] Load actual user avatars instead of placeholder ellipses
- [ ] Show online/offline ring around avatar
- [ ] Speaking animation on avatar (pulsing ring)
- [ ] Fallback to initials if no avatar

#### 2.4 Channel Icons
- [ ] Different icons for channel types (announcements, rules, general)
- [ ] Allow admins to set custom emoji as channel icon
- [ ] Icon picker in channel settings

---

### Phase 3: Channel Management

#### 3.1 Right-click Context Menu for Text Channels
- [ ] Copy channel link
- [ ] Mark as read
- [ ] Mute notifications
- [ ] Edit channel (admin)
- [ ] Delete channel (admin)
- [ ] Channel settings (admin)

#### 3.2 Mute/Notification Settings Per Channel
- [ ] Right-click to mute notifications
- [ ] Options: All messages, @mentions only, Nothing
- [ ] Muted icon indicator on channel
- [ ] Persist settings per user

#### 3.3 Pin/Favorite Channels
- [ ] Star/pin frequently used channels
- [ ] Pinned channels appear at top of list
- [ ] Persist per-user preferences
- [ ] Quick access via keyboard

#### 3.4 Private/Read-only Channel Indicators
- [ ] Lock icon for private channels
- [ ] Read-only icon for announcement channels
- [ ] Tooltip showing who has access
- [ ] Permission management UI

---

### Phase 4: Voice Channel Enhancements

#### 4.1 Voice Channel User Limit
- [ ] Set max users per voice channel
- [ ] Show current/max users (e.g., "3/10")
- [ ] Indicate when channel is full
- [ ] Lock icon when at capacity

#### 4.2 Screen Share Indicator
- [ ] Show screen share icon if someone is sharing
- [ ] Quick preview thumbnail on hover
- [ ] Click to view screen share

#### 4.3 Voice Channel Status
- [ ] Show bitrate/quality indicator
- [ ] Region indicator
- [ ] Connection quality per user

---

### Phase 5: Quick Wins

#### 5.1 Keyboard Navigation
- [ ] Arrow keys to navigate channels
- [ ] Enter to select channel
- [ ] Escape to deselect
- [ ] Tab to switch between text/voice sections

#### 5.2 Double-click to Edit
- [ ] Double-click channel name to start rename (admin)
- [ ] More intuitive than finding edit button
- [ ] Escape to cancel, Enter to save

#### 5.3 Hover Actions
- [ ] Show action buttons on hover (settings, invite, etc.)
- [ ] Hide when not hovering to reduce clutter
- [ ] Smooth fade in/out animation

#### 5.4 Last Activity Timestamp
- [ ] Show when channel was last active
- [ ] Relative time format ("2m ago", "1h ago")
- [ ] Helps identify inactive channels
- [ ] Optional setting to show/hide

---

## Progress Tracking

### Completed
- [x] Text channel creation (admin)
- [x] Voice channel creation (admin)
- [x] Channel selection/switching
- [x] Channel rename (admin)
- [x] Unread message indicators
- [x] Voice participant tracking
- [x] Per-user volume control
- [x] Server mute/deafen (admin)
- [x] Speaking state indicators
- [x] Mute/deafen/camera icons

### In Progress
(None currently)

### Not Started
- [ ] Phase 1: High Priority features
- [ ] Phase 2: Visual Enhancements
- [ ] Phase 3: Channel Management
- [ ] Phase 4: Voice Channel Enhancements
- [ ] Phase 5: Quick Wins

---

## Technical Notes

### Files to Modify

**Controls:**
- `src/Miscord.Client/Controls/ChannelListView.axaml` - Main channel list UI
- `src/Miscord.Client/Controls/ChannelListView.axaml.cs` - Code-behind

**ViewModels:**
- `src/Miscord.Client/ViewModels/MainAppViewModel.cs` - Commands and state
- `src/Miscord.Client/ViewModels/VoiceChannelViewModel.cs` - Voice channel data

**Services:**
- `src/Miscord.Client/Services/ApiModels.cs` - ChannelResponse model
- `src/Miscord.Client/Services/ApiClient.cs` - API calls

**Server:**
- `src/Miscord.Server/Controllers/ChannelsController.cs` - Channel endpoints
- `src/Miscord.Server/Services/ChannelService.cs` - Channel business logic

### Data Model Considerations

The `ChannelResponse` already includes:
- `Position` field for ordering (not yet used in UI)
- `Topic` field for description (not yet displayed)
- `UnreadCount` for badges (implemented)

New fields may be needed for:
- Category/folder grouping
- User limits for voice channels
- Notification settings
- Private channel flags
