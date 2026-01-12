# Voice Channel Improvement Plan

This document outlines planned improvements for voice channels in Snacka.

## Current State

### Voice Channels
- Join/leave voice channels with real-time participant updates
- WebRTC-based audio/video streaming
- Screen sharing with annotation support
- Mute/deafen controls
- Camera toggle
- Voice activity indicators

---

## Improvement Roadmap

### Phase 1: Quick Wins

#### 1.1 Always-Visible Mute/Deafen Controls
- [x] Move mute/deafen buttons to user panel (next to settings)
- [x] Buttons visible even when not in a voice channel
- [x] Allows pre-muting before joining a channel
- [x] Remember mute/deafen state between sessions (persisted to settings.json)

#### 1.2 Visual Feedback Improvements
- [x] Show muted icon on user avatar in sidebar
- [x] Speaking indicator ring on avatar (green ring when speaking)
- [x] Different icon for self-muted vs server-muted (red for self, orange for server)

---

### Phase 2: Audio Quality

#### 2.1 Audio Device Selection
- [x] Quick audio input device switcher in user panel
- [x] Quick audio output device switcher in user panel
- [x] Audio level meter in user panel
- [x] Push-to-talk option

#### 2.2 Noise Suppression
- [ ] Built-in noise suppression toggle
- [ ] Echo cancellation improvements
- [x] Automatic gain control (AGC with 4x baseline boost, fast attack/slow release)

---

### Phase 3: Advanced Features

#### 3.1 Voice Channel Permissions
- [x] Server mute (admin can mute users - cannot unmute themselves)
- [x] Server deafen (admin can deafen users - cannot undeafen themselves)
- [ ] Channel-specific mute permissions
- [ ] Priority speaker role
- [x] Move users between channels (admin)

#### 3.2 Audio Mixing
- [x] Per-user volume control (0-300% via right-click context menu, persisted)
- [x] Separate volume for screen share audio (fullscreen view has volume slider)
- [ ] Ducking (lower other audio when someone speaks)

#### 3.3 Recording
- [ ] Record voice channel (with consent)
- [ ] Save recordings locally
- [ ] Playback in app

---

### Phase 4: Nice to Have

#### 4.1 Soundboard
- [ ] Quick sound effects/clips
- [ ] Custom sound uploads
- [ ] Hotkey triggers

#### 4.2 Voice Effects
- [ ] Voice changer/modulation
- [ ] Background noise addition (coffee shop, rain)

#### 4.3 Connectivity
- [ ] Connection quality indicator
- [ ] Automatic bitrate adjustment
- [ ] Region-based server selection

---

## Progress Tracking

### Completed
- [x] Phase 1.1: Always-visible mute/deafen controls
- [x] Remember mute/deafen state between sessions
- [x] Show muted icon on user avatar
- [x] Speaking indicator ring on avatar
- [x] Phase 1.2: Different icon for self-muted vs server-muted
- [x] Phase 2.1: Audio device selection (input/output switcher, level meter, push-to-talk)
- [x] Per-user volume control (Phase 3.2)
- [x] Phase 3.1: Voice Channel Permissions (server mute/deafen, move users)

### In Progress
- [ ] Phase 2.2: Noise Suppression (AGC done, noise suppression and echo cancellation pending)

### Not Started
- [ ] Phase 3.1: Remaining permissions (channel-specific mute, priority speaker)
- [ ] Phase 3.2: Remaining audio mixing (ducking)
- [ ] Phase 3.3: Recording
- [ ] Phase 4: Nice to Have
