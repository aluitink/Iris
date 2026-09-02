# 161o — 19.1.4 browser signed-POST 401: carry the signed date in `X-Signature-Date`

## Summary

Phase 19.1.4 (**signature scenarios**), remediation for a finding from the live ad-hoc interop: the
*browser* client's direct signed POST to its own outbox returned **401**, while the server-side proxy
fallback (which re-signs over a self-consistent base) succeeded and masked the defect. Root cause: a
Blazor WebAssembly host signs outgoing requests through a real `HttpClientHandler` (the browser's
`fetch`), and the standard `Date` request header is a **forbidden header** — the browser strips/overrides
it on the wire. The client signed over a `Date` value it chose, but the server verified the `date`
signature component over the (different) value the browser actually put on the wire, so the reconstructed
signature base mismatched and RSA verification failed → 401.

The fix carries the signed `date` value in a custom, **non-forbidden** `X-Signature-Date` header (the
browser sends it faithfully). The server verifier reads the `date` component from that header when
present, falling back to the wire `Date`; the `Date` header is still sent for replay protection on
non-browser paths. Both the signer and the verifier resolve the `date` component through the same shared
helper, so the two can never drift.

## What changed

- **`src/Iris.Core/Signing/Signatures.cs`**
  - `SignatureDateHeaderName` (`"X-Signature-Date"`) — the custom header a client sets to the exact value
    it signed over for the `date` component. Non-forbidden, so a browser sends it verbatim.
  - `ResolveDateComponent(IReadOnlyDictionary<string,string>)` — the single source of truth for the value
    a `date` signature component is checked against: the `X-Signature-Date` value when present (and
    non-empty), else the `Date` value, else an empty string. Used by both the signer and the verifier.
- **`src/Iris.Client/Pipeline/SigningHandler.cs`**
  - After signing, the handler sets the `X-Signature-Date` header to `metadata.Date` (the value it signed
    over), alongside the existing `Date` header (kept for replay protection on non-browser transports).
- **`src/Iris.Server/Security/HttpSignatureValidator.cs`**
  - `ToMetadata` now also collects the `X-Signature-Date` header and sets the metadata's `Date` field to
    `Signatures.ResolveDateComponent(headers)` — i.e. the value the client actually signed over — so
    `BuildSignatureBase` reconstructs the same base the client signed.

## Tests

- **`tests/Iris.Core.Tests/Signing/SignaturesTests.cs`** (4 new `ResolveDateComponent` cases):
  prefers `X-Signature-Date` over `Date`; falls back to `Date` when `X-Signature-Date` is absent; returns
  empty when neither is present; and ignores an *empty* `X-Signature-Date` (treated as absent) in favor of
  the wire `Date`.
- **`tests/Iris.Client.Tests/Pipeline/SigningHandlerTests.cs`** (2 new cases): the `SigningHandler` sets
  `X-Signature-Date` equal to the signed `Date`; and — the key scenario — a signature made by the handler
  **still verifies when the wire `Date` is overridden to a different value** (simulating the browser
  override) as long as `X-Signature-Date` carries the signed value.

## Verification

- `dotnet build` — clean, 0 warnings / 0 errors (TreatWarningsAsErrors on).
- `dotnet test` — **1,288 tests, 0 failed** (was 1,278; +6 new: 4 `ResolveDateComponent` core cases + 2
  `SigningHandler` cases). The full server-security suite (59 tests, including the two-instance
  federation round-trips that now flow through `X-Signature-Date` end-to-end) passes — no regressions.
- **Live (ad-hoc interop, Playwright):** logged on as `alice@iris-dev1.luit.ink` (the FQDN dial base, so
  advertised == dials and the write is a direct signed POST, not the proxy). Composed a note → the direct
  `POST https://iris-dev1.luit.ink/ap/v1/u/alice/outbox` returned **202** (not 401). The captured request
  carried `X-Signature-Date: <signed value>`, the standard `Date` header was **absent** (the browser had
  stripped it — the exact condition that previously caused the 401), and **no `/ap/v1/proxy/…` fallback
  request** was made. The note was persisted in alice's outbox.

## Deployment note

The cross-origin WebFinger log-on from the public UI (`https://iris.luit.ink`) to the AP server
(`https://iris-dev1.luit.ink`) was CORS-blocked because `https://iris.luit.ink` was not in the server's
`Iris__CorsOrigins`. Added it to `IRIS_CORS_ORIGINS` in `.env` (gitignored deployment config; the
container was restarted to pick it up). This is a deployment-config gap, not a code change.
