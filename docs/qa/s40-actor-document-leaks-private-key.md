# S40 — Actor document leaks private key to authenticated users

**Severity:** S1 (Critical)
**Status:** OPEN
**First seen:** Pass 255 (2026-09-21)
**Build:** `401c08b5`

## Summary

The ActivityPub actor document (`GET /ap/v1/u/<handle>`) includes the `privateKey` field in its JSON response. Any authenticated user can read the instance's RSA private key, which completely breaks the federation trust model.

## Repro

1. Log in as any user (e.g., `ii-a1` / `Password1` on `qa-iris-a.luit.ink`).
2. Navigate to `https://qa-iris-a.luit.ink/ap/v1/u/ii-a1` (or use `fetch('/ap/v1/u/ii-a1')` from the browser console).
3. Inspect the JSON response. The `privateKey` field contains the full PEM-encoded RSA private key.

## Evidence

```json
{
  "id": "https://qa-iris-a.luit.ink/ap/v1/u/ii-a1",
  "type": "Person",
  "preferredUsername": "ii-a1",
  "publicKey": {
    "id": "https://qa-iris-a.luit.ink/ap/v1/u/ii-a1#key-1",
    "owner": "https://qa-iris-a.luit.ink/ap/v1/u/ii-a1",
    "publicKeyPem": "-----BEGIN PUBLIC KEY-----\n..."
  },
  "privateKey": "-----BEGIN PRIVATE KEY-----\nMIIEvQIBADANBgkqhkiG9w0BAQEFAASCBKcwggSjAgEAAoIBAQC/oh+bwKOkfRpY\nIw/o+84Vp4vfjx/L0F5SG30ZfKqczhMswOeIdUOfW5HIOjJQwhGWG9zvONus+uS7\niNaRGk7fOS/GgqAunFwSPt6RgePtxS8mrjDyeJprKhU2uYbPAKZJ9iWWRqAK3r7w\nuvQGHhPIXzTxN8Z8O0vmT5KxzSXBYTTOW203a4/gN+36a7n16wevl5/bBgiQGgBT\nrVqecOU+qqtWnb1QRk4qVtcEE72pnxZAq7KfoRbIMw6xP5g4SrVq01QxOHApMQRU\nJSfnT/HgeEPristok5kyrwLez8VExTTh8F60Br/r4DyxU6szu/Xeyutnd0meDm/D\n7A6tQOPDAgMBAAECggEAKKJPcn7MFD5kviidIIl4PvY6fgqCsvx5a46hnaxmHv7B\naR10WuaGkr1fcaYJcj9cbEh3NhCH4CuJIczX5mHv7B\n...",
  "keyAlgorithm": "rsa"
}
```

## Impact

- An attacker who compromises **any** account (or registers a new one, since `openRegistrations` is not enforced) can read the instance's private key.
- With the private key, the attacker can **sign activities as any actor** on the instance, impersonate users, and forge federation messages.
- This completely breaks the ActivityPub trust model.

## Root Cause (suspected)

The actor document serializer includes the `privateKey` field (likely stored for internal signing use) in the HTTP response. The field should be stripped before the document is serialized for external consumption.

## Suggested Fix

Remove the `privateKey` and `keyAlgorithm` fields from the actor document response. The private key should only be accessible internally (for signing) and never exposed via the HTTP API.

## Related

- S35 (remote actor discovery 404) — actor document visibility is a broader concern.
- Pass 252 — `openRegistrations=false` flag is not enforced, making it trivial to obtain an authenticated session.
