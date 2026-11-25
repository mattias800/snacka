# Deployment Guide

This guide covers how to deploy Snacka server for production use.

For development setup, see [DEVELOPMENT.md](../DEVELOPMENT.md).

## Quick Start with Docker

The easiest way to deploy Snacka is using Docker Compose.

### 1. Clone and Configure

```bash
git clone https://github.com/mattias800/snacka.git
cd snacka

# Copy the example environment file
cp .env.example .env
```

### 2. Edit Configuration

Edit `.env` and set the required values:

```bash
# REQUIRED: Change these!
JWT_SECRET_KEY=your-secure-random-string-at-least-32-characters
POSTGRES_PASSWORD=your-secure-database-password
ALLOWED_ORIGIN=https://your-domain.com

# Server info
SERVER_NAME=My Snacka Server
SERVER_DESCRIPTION=A place to hang out

# Optional: GIF picker (get free API key at https://developers.google.com/tenor)
TENOR_API_KEY=your-tenor-api-key
```

### 3. Start the Server

```bash
docker compose up -d
```

### 4. Verify

```bash
# Check status
docker compose ps

# View logs
docker compose logs -f

# Test health endpoint
curl http://localhost:5117/api/health
```

The server is now running on port 5117.

## Reverse Proxy Setup

For production, run behind a reverse proxy (nginx, Caddy, etc.) for SSL termination.

> **Trusted proxies:** Snacka only trusts `X-Forwarded-For` from loopback by default, so clients can't fake their IP to dodge rate limits. If your proxy reaches Snacka from another address (for example Docker, where requests arrive from the bridge network), add it:
> ```
> ReverseProxy__TrustedNetworks__0=172.16.0.0/12
> ```
> Only do this when port 5117 is **not** reachable directly from the internet. Otherwise anyone could send fake headers through the trusted network.

### NGINX Proxy Manager

Works out of the box - create a proxy host pointing to `localhost:5117` and enable SSL.

### Manual NGINX

```nginx
server {
    listen 443 ssl http2;
    server_name snacka.example.com;

    ssl_certificate /path/to/cert.pem;
    ssl_certificate_key /path/to/key.pem;

    location / {
        proxy_pass http://localhost:5117;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_read_timeout 86400;  # WebSocket timeout
    }
}
```

### Caddy

```
snacka.example.com {
    reverse_proxy localhost:5117
}
```

## TURN Server (Recommended)

A TURN server enables voice/video for users behind restrictive firewalls or VPNs. The docker-compose.yml includes coturn.

### Enable TURN

Add to your `.env`:

```bash
TURN_ENABLED=true
TURN_HOST=your-domain.com
TURN_SECRET=your-secure-random-string-for-turn-auth
TURN_EXTERNAL_IP=your.server.public.ip  # Required if behind NAT
```

### Firewall Ports

Open these ports for TURN:

| Port | Protocol | Purpose |
|------|----------|---------|
| 3478 | TCP/UDP | STUN/TURN |
| 5349 | TCP/UDP | TURNS (TLS) |
| 49152-49252 | UDP | Media relay |

### TLS for TURN (Optional)

For TURNS (TURN over TLS), uncomment the certificate lines in `turnserver.conf` and mount your certificates:

```yaml
# In docker-compose.yml under coturn volumes:
- ./certs:/etc/coturn/certs:ro
```

## Environment Variables

| Variable | Required | Description |
|----------|----------|-------------|
| `JWT_SECRET_KEY` | **Yes** | JWT signing key (min 32 chars) |
| `POSTGRES_PASSWORD` | **Yes** | PostgreSQL password |
| `ALLOWED_ORIGIN` | **Yes** | Your domain (e.g., `https://snacka.example.com`) |
| `SERVER_NAME` | No | Server display name |
| `SERVER_DESCRIPTION` | No | Server description |
| `ALLOW_REGISTRATION` | No | Allow signups (default: `true`) |
| `GIF_PROVIDER` | No | `Tenor` or `Klipy` (default: `Tenor`) |
| `TENOR_API_KEY` | No | Tenor GIF API key |
| `KLIPY_API_KEY` | No | Klipy GIF API key |
| `TURN_ENABLED` | No | Enable TURN server (default: `false`) |
| `TURN_HOST` | No | TURN server hostname |
| `TURN_SECRET` | No | TURN auth secret |
| `TURN_EXTERNAL_IP` | No | Server's public IP (if behind NAT) |

## Database

The docker-compose.yml includes PostgreSQL. Data is persisted in the `postgres-data` volume.

## Updating

```bash
# Pull latest image
docker compose pull

# Restart with new image
docker compose up -d
```

## Client Downloads

Users connect using the Snacka desktop client:

| Platform | Download |
|----------|----------|
| Windows | [Installer](https://github.com/mattias800/snacka/releases/latest) |
| macOS | [DMG](https://github.com/mattias800/snacka/releases/latest) |
| Linux | [AppImage](https://github.com/mattias800/snacka/releases/latest) |

Share your server URL with users - they paste it in the client to connect.

## Troubleshooting

### Server won't start
- Check logs: `docker compose logs snacka-server`
- Verify `.env` file exists and has required values
- Ensure port 5117 is not in use

### Can't connect from client
- Verify `ALLOWED_ORIGIN` matches your domain exactly
- Check firewall allows port 5117
- Ensure reverse proxy forwards WebSocket connections

### Voice/video not working
- Enable TURN server (see above)
- Open firewall ports for TURN (3478, 5349, 49152-49252)
- Set `TURN_EXTERNAL_IP` if server is behind NAT

### Database errors
- Check PostgreSQL is healthy: `docker compose ps`
- View database logs: `docker compose logs postgres`

## Ports Summary

| Port | Protocol | Purpose | Required |
|------|----------|---------|----------|
| 5117 | TCP | HTTP API & WebSocket | Yes |
| 3478 | TCP/UDP | STUN/TURN | If TURN enabled |
| 5349 | TCP/UDP | TURNS (TLS) | If TURN enabled |
| 49152-49252 | UDP | Media relay | If TURN enabled |
