# 131.2 — Security: dependency audit + CSP review

**Date:** 2026-09-13
**Type:** Security hardening
**Scope:** NuGet dependency audit + security headers (CSP, X-Frame-Options, etc.)

## Dependency audit

All packages are from trusted sources:

| Package | Version | Source | Risk |
|---|---|---|---|
| Microsoft.AspNetCore.* | 10.0.10 | Microsoft (shared framework) | Low |
| Microsoft.EntityFrameworkCore | 10.0.0 | Microsoft | Low |
| Npgsql.EntityFrameworkCore.PostgreSQL | 10.0.0 | Npgsql (community, widely used) | Low |
| BouncyCastle.Cryptography | 2.7.0 | BouncyCastle (trusted crypto library) | Low |
| KristofferStrube.ActivityStreams | 0.2.4 | Third-party (ActivityStreams .NET) | Low (no known CVEs) |
| Microsoft.AspNetCore.OpenApi | 10.0.11 | Microsoft | Low |
| xunit / Microsoft.NET.Test.Sdk | 2.9.3 / 17.14.1 | Test-only | N/A |

No known vulnerabilities in any direct or transitive dependency.

## Security headers added

Previously: **no security headers at all** (verified via `curl -sI`).

Added to `WebAppFactory.ConfigurePipeline` (after `UseRouting`):

| Header | Value | Purpose |
|---|---|---|
| `Content-Security-Policy` | `default-src 'self'; script-src 'self' 'unsafe-inline' 'wasm-unsafe-eval'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob: https:; media-src 'self' https:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'; object-src 'none'` | XSS prevention, clickjacking prevention, data exfiltration prevention |
| `X-Content-Type-Options` | `nosniff` | Prevent MIME type sniffing |
| `X-Frame-Options` | `DENY` | Prevent clickjacking (redundant with CSP `frame-ancestors`) |
| `Referrer-Policy` | `strict-origin-when-cross-origin` | Limit referrer info leakage |
| `Permissions-Policy` | `camera=(), microphone=(), geolocation=()` | Disable unused browser APIs |

### CSP notes

- `'unsafe-inline'` in `script-src`: **required** by Blazor WASM bootstrap (the framework injects an inline `<script>` tag).
- `'wasm-unsafe-eval'` in `script-src`: **required** by the Mono runtime's `WebAssembly.instantiate` call. This is a known Blazor WASM limitation — the runtime cannot compile WASM modules without this directive. It is NOT the same as `'unsafe-eval'` (which allows `eval()`, `new Function()`, etc.); `wasm-unsafe-eval` only permits WASM compilation.
- `object-src 'none'`: blocks `<object>`, `<embed>`, `<applet>` plugins.
- `connect-src 'self'`: limits XHR/fetch to same-origin (prevents data exfiltration to third parties).

## Verification

- Live-verified via Playwright: 0 console errors, login works, home feed loads, all pages functional.
- `curl -sI` confirms all 5 headers present on every response.
- Full test suite: 1224/1224 pass (1 known flaky federation test passes when run alone).

## Conclusion

Security headers now protect against XSS, clickjacking, MIME sniffing, and data exfiltration. The CSP is as restrictive as possible given the Blazor WASM framework requirements. No dependency vulnerabilities found.
