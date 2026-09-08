# Iris — Production Deployment (Reverse Proxy)

This directory contains the reverse-proxy configuration for putting Iris.Web behind TLS.
The app container listens on plain HTTP (port 8080 in-container, published as 8088 on the host);
the reverse proxy terminates TLS and forwards to 8088.

## Files

| File | Purpose |
|------|---------|
| `nginx.conf` | Production nginx server block: TLS, WebSocket upgrade, security headers, gzip, rate limiting. |
| `nginx-ratelimit.conf` | `limit_req_zone` directive (include in nginx's `http{}` block). |
| `Caddyfile` | Equivalent Caddy config (simpler — Caddy handles TLS + WebSocket automatically). |

## Requirements

The app already has `app.UseForwardedHeaders()` wired in `WebAppFactory.cs` (before
`UseAuthentication`), so it correctly reads `X-Forwarded-Proto` / `X-Forwarded-For`
from the proxy. No app-side changes are needed.

## nginx

### 1. Install + provision TLS

```bash
# On the host (Debian/Ubuntu example)
sudo apt install nginx certbot
sudo certbot certonly --webroot -w /var/www/certbot -d iris.example.com
```

### 2. Deploy config

```bash
# Rate-limit zone (in the http{} block — /etc/nginx/nginx.conf or a conf.d file):
sudo cp apps/Iris.Web/deploy/nginx-ratelimit.conf /etc/nginx/conf.d/00-ratelimit.conf

# Server block:
sudo cp apps/Iris.Web/deploy/nginx.conf /etc/nginx/conf.d/iris.conf
# Edit: replace iris.example.com with your FQDN, verify cert paths.

sudo nginx -t && sudo systemctl reload nginx
```

### 3. Verify

```bash
# WebSocket upgrade check (should show 101 Switching Protocols):
curl -i -N \
  -H "Connection: Upgrade" \
  -H "Upgrade: websocket" \
  -H "Sec-WebSocket-Version: 13" \
  -H "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==" \
  https://iris.example.com/_blazor

# Health check:
curl -s https://iris.example.com/ap/v1/health | jq
```

In a browser: DevTools → Network → look for the `/_blazor` request. Status **101** = WebSocket
(circuit). Status **200** = SSE fallback (works, but the proxy config is incomplete).

## Caddy (alternative)

```bash
sudo apt install caddy
sudo cp apps/Iris.Web/deploy/Caddyfile /etc/caddy/Caddyfile
# Edit: replace iris.example.com with your FQDN.
sudo systemctl reload caddy
```

Caddy provisions Let's Encrypt TLS automatically and upgrades WebSockets transparently
(no `Upgrade`/`Connection` header directives needed). The Caddyfile adds security headers,
rate limiting, and gzip on top of the defaults.

## Security headers summary

| Header | Value | Why |
|--------|-------|-----|
| `Strict-Transport-Security` | `max-age=31536000; includeSubDomains` | Force HTTPS for 1 year. |
| `X-Content-Type-Options` | `nosniff` | Prevent MIME-type sniffing. |
| `X-Frame-Options` | `DENY` | Clickjacking protection (the app is never embedded in a frame). |
| `Referrer-Policy` | `strict-origin-when-cross-origin` | Minimize referrer leakage. |
| `Content-Security-Policy` | See config | Restrict script/style/image sources. `unsafe-inline` for scripts is required by the Blazor .NET runtime bootstrap. |
| `Permissions-Policy` | `camera=(), microphone=(), geolocation=()` | Disable browser features the app doesn't use. |

## Rate limiting

- **nginx:** `limit_req zone=ap_limit burst=20 nodelay` — 10 req/s per IP, burst of 20.
  Health checks (`/ap/v1/health`) are exempt.
- **Caddy:** `rate_limit` directive — 7 req/s per IP, burst 20.

Adjust the rate if you have legitimate high-traffic patterns (e.g., a popular public timeline
with many concurrent readers). The burst allows a page load (5–10 parallel requests) to pass
without throttling.

## Media upload size

The app's `Iris:MaxRequestBodySize` defaults to 1 MiB (federation activity documents).
The reverse proxy's `client_max_body_size` (nginx) / `request_body max_size` (Caddy) is set
to **10 MiB** to accommodate image uploads through the compose form. If you raise the app's
limit, raise the proxy's limit to match.
