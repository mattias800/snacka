<p align="center">
  <img src="https://raw.githubusercontent.com/mattias800/snacka/master/src/Snacka.Client/Assets/snacka-icon.png" alt="Snacka Logo" width="128" height="128">
</p>

# Snacka

*Nu snackar vi!*

[![Build Client](https://github.com/mattias800/snacka/actions/workflows/build-client.yml/badge.svg)](https://github.com/mattias800/snacka/actions/workflows/build-client.yml)
[![Build Server](https://github.com/mattias800/snacka/actions/workflows/build-server.yml/badge.svg)](https://github.com/mattias800/snacka/actions/workflows/build-server.yml)

A self-hosted communication platform where you own your data.

> **Note:** Snacka is a work in progress and not yet ready for production use.

![Snacka Screenshot](https://raw.githubusercontent.com/mattias800/snacka/master/docs/screenshots/main-app.png)

## What is Snacka?

Snacka is a free, open-source communication platform you can host yourself. Full control over your data and privacy - no third-party services required.

## Features

### Text Communication
- **Direct Messages** - Private conversations with typing indicators
- **Text Channels** - Organized discussions with message editing and deletion
- **GIF Picker** - Search and share GIFs (Tenor or Klipy)
- **File Sharing** - Share images, audio files, and documents

### Voice & Video
- **Voice Channels** - Crystal-clear voice chat with WebRTC
- **Webcam Streaming** - Share your camera in voice channels
- **Screen Sharing** - Present your screen to others
- **Drawing on Shares** - Annotate screen shares in real-time
- **TURN Server Support** - Works behind firewalls and VPNs

### Remote Co-op Gaming
- **Controller Streaming** - Share your controller input with the host
- **Virtual Controllers** - Host receives input as a virtual gamepad
- **Rumble Feedback** - Feel game vibrations on your controller

### Server Management
- **Communities** - Create and manage multiple communities
- **Channels** - Organize with text and voice channels
- **Roles & Permissions** - Owner, Admin, and Member roles
- **Invite System** - Share invite links to bring friends to your server

### Cross-Platform
- **Windows** - Native installer with automatic updates
- **macOS** - Apple Silicon (M1/M2/M3/M4)
- **Linux** - AppImage for easy installation

## Download

Download the latest version for your platform:

| Platform | Download | Notes |
|----------|----------|-------|
| **Windows** | [Installer](https://github.com/mattias800/snacka/releases/latest) | Includes auto-updates |
| **macOS** | [DMG](https://github.com/mattias800/snacka/releases/latest) | Apple Silicon only |
| **Linux** | [AppImage](https://github.com/mattias800/snacka/releases/latest) | Run `chmod +x` before launching |

See the [Releases](https://github.com/mattias800/snacka/releases) page for all versions.

## Connecting to a Server

1. Download the client for your platform
2. Get an invite link from a server admin
3. Paste the invite link in the client
4. Create an account and start chatting

## Self-Hosting

Want to run your own server? You'll need:
- A server or VPS with Docker installed
- A domain name (recommended)

See the [Deployment Guide](docs/DEPLOY.md) for setup instructions.

## Getting Help

- **Issues**: [GitHub Issues](https://github.com/mattias800/snacka/issues)
- **Discussions**: [GitHub Discussions](https://github.com/mattias800/snacka/discussions)

## Contributing

Contributions are welcome! See [DEVELOPMENT.md](DEVELOPMENT.md) for development setup and guidelines.

## License

MIT
