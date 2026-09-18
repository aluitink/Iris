# 135.1b(4) — Content federation (Lemmy → Iris) blocked by a signature interop failure

**Date:** 2026-09-13
**Slice:** 135.1 remaining (content posting in both directions) — investigation, not a fix
**Status:** **BLOCKER documented.** The content federation path is not yet working; the precise
failure point is captured below for the next slice (135.2 / 137).

## What was tested

With the live community-follow already working (135.1b(3)) — Iris community `owner-test-5428`
followed Lemmy community `test` (HTTP 204) — a Note was posted on the **Lemmy** side to test the
inbound content path (Lemmy → Iris):

- `POST /api/v3/post` to Lemmy community `test` (id=2) as `lemmyadmin` → **HTTP 200**, post
  `ap_id=https://iris-dev2.luit.ink/post/1` ("Hello from Lemmy to Iris (135.1 interop)").

## Result: the Note did NOT arrive in Iris

- `GET /ap/v1/search?q=Hello%20from%20Lemmy` on Iris → **0 items**.
- Iris DB (`Objects` / `Activities`, matched on `Document->>'id' LIKE '%iris-dev2.luit.ink%'`)
  contains **no** `post/1` Note and no `Create` for it.

## Root cause (from the Lemmy logs): the follow was never registered on Lemmy's side

Because the follow never registered, Lemmy had **no followers** for `test`, so posting the Note
queued only a local activity — it was never sent to Iris. The follow failed at the **HTTP
signature** step:

```
2026-09-13T23:34:05.781214Z  WARN Error encountered while processing the incoming HTTP request:
  lemmy_server::root_span_builder: Unknown: Error when parsing signature from Http Signature
  (POST /inbox, http.host=iris-dev2.luit.ink, http.status_code=400)
```

When Iris's community followed the Lemmy community, Iris sent a signed `Follow` to Lemmy's shared
`/inbox`. Lemmy rejected it with **`Error when parsing signature from Http Signature`** (HTTP 400) —
it failed to **parse** the `Signature` header (not merely to verify it), so the follow was dropped and
never registered. With no registered follower, the later Note had nowhere to federate.

A secondary, related observation in the same log window:

```
lemmy_apub::objects::instance: Failed to dereference site for https://iris.luit.ink/ with
  content <!DOCTYPE html> ... <title>Iris</title>
```

Lemmy dereferenced the **site root** (`https://iris.luit.ink/`) expecting a JSON-LD `DiasporaFederated`
site document and got the **HTML SPA** back. Iris does not (yet) serve a JSON-LD site document at `/`
— relevant to instance-level federation, though the immediate signature-parse failure is the
blocking step for the community follow.

## Diagnostic state for the next slice (135.2 / 137)

The next slice should root-cause and fix the signature interop so a Lemmy-registered follow lets
content flow. Concrete starting points (all verified this turn):

1. **Capture the exact `Signature` header Iris sends** for a `Follow` POST (the Lemmy log says it
   fails at *parse*, so suspect the header format / `headers` component list / quoting). Iris signs
   via `Iris.Client.Pipeline.SigningHandler` (uses `SigningProfile.ServerToServer` for body POSTs —
   covers `digest` + `content-type`), producing the header through `Iris.Core.Signing.SignatureHeader`
   (draft-cavage-http-signatures-03: `keyId`, `algorithm`, `headers`, `signature`, optional
   `created`).
2. **`keyId` is resolvable in principle:** the community's `publicKey.id` is
   `https://iris.luit.ink/ap/v1/c/owner-test-5428#key-1` (RSA, `MII…` PEM), served at
   `GET /ap/v1/c/owner-test-5428`. The failure is at parse, before key resolution.
3. **Also serve a JSON-LD site document at `/`** (or the node-info path) so Lemmy can dereference the
   instance — needed for instance-level federation, and removes the `Failed to dereference site`
   noise.
4. **Verify end to end after the fix:** re-drive the follow, confirm Lemmy logs the follow as
   registered (no 400), then re-post the Note and confirm it lands in Iris (`/ap/v1/search` + the
   `Objects` store) — and the reverse direction (Iris community posts a Note that federates to
   Lemmy).

## What is NOT a code change this turn

No source code was changed; this is a live-state investigation. The working pieces (135.1a
`RemoteCommunityPersister`, 135.1b Lemmy deployment, 135.1b(2) round-trip test, 135.1b(3) live
follow) are committed and green. The new finding is the signature interop blocker for content
federation.
