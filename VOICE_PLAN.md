# Voice Channel Improvement Plan

This document outlines planned improvements for voice channels in Miscord.

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
- [ ] Different icon for self-muted vs server-muted (requires server-side admin mute feature - see 3.1)

---

### Phase 2: Audio Quality

#### 2.1 Audio Device Selection
- [ ] Quick audio input device switcher in user panel
- [ ] Quick audio output device switcher in user panel
- [ ] Audio level meter in user panel
- [ ] Push-to-talk option

#### 2.2 Noise Suppression
- [ ] Built-in noise suppression toggle
- [ ] Echo cancellation improvements
- [ ] Automatic gain control

---

### Phase 3: Advanced Features

#### 3.1 Voice Channel Permissions
- [ ] Server mute (admin can mute users)
- [ ] Server deafen (admin can deafen users)
- [ ] Channel-specific mute permissions
- [ ] Priority speaker role
- [ ] Move users between channels (admin)

#### 3.2 Audio Mixing
- [ ] Per-user volume control
- [ ] Separate volume for screen share audio
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

### In Progress
- [ ] Phase 2: Audio Quality

### Not Started
- [ ] Phase 3: Advanced Features (includes server mute for admin)
- [ ] Phase 4: Nice to Have
