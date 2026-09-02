# Decision 057 — Browser-loadable media: URL-keyed same-origin proxy, AP-native wire

## Context

The only consumer of an actor's `GET /ap/v1/u/{handle}/inbox` is Iris's own WASM client. But the **same
problem generalizes to every external collection stream** the client browses: a **random user's outbox**,
a user's **liked/featured**, **communities**, the **followed feed**, and **synced peers**. Any of these can
contain an object whose attachment (`Image`/`Document`) carries a **cross-origin** `url` (e.g.
`https://remote.example/img.png`). When the browser renders that object it emits
`<img src="https://remote.example/img.png">` and makes a **direct cross-origin fetch**. Most ActivityPub
instances do not send CORS headers for arbitrary media, so the browser **blocks the image**. The content is
there on the wire (AP-native, verbatim) but the browser cannot load it.

This is the **inbound/external attachment URL-rewrite** that Decision 056 (b) explicitly deferred to 20.4,
and that 20.4 (a) (change 161v) only solved for the **local-authoring** path (upload → same-origin serve).
The **external-read** path is this decision.

Two constraints from the operator (2026-09-02) govern the design:

1. **The platform client browses many external streams** (outbox, inbox, liked, featured, communities,
   followed, synced peers) — the fix must work for **all** of them, not just the inbox.
2. **Keep the wire 100% AP-native / verbatim** so that if the spec later defines how a browser reads these
   collections + media, the stored/wired data is already spec-correct and only the *client's render
   boundary* changes. And: **the WASM client is the only consumer of the owner-only inbox, so we may
   customize that endpoint's behavior** without breaking federation — but we prefer a design that keeps the
   *collections* pure and pushes the "make it browser-loadable" decision to the **client boundary** instead.

## The problem with a content hash as the key

A content hash (SHA-256 of the bytes) is attractive — it dedupes (the same image from 50 peers stores once)
and decouples the local bytes from the wire. But it **requires the bytes**, and the client, in the browser,
**does not have the bytes** (it can't fetch them cross-origin without CORS — that's the problem). So:

- For **cold external browsing** (a *random* user's outbox the server has never seen), the server has **no
  local copy** and the client has **no content** from which to compute a hash. A content-hash key has a
  **coverage hole** for exactly the "browse a stranger" case.
- The content-hash fast path (digest-present → `/media/{hash}`) only works when the **originator put a
  digest on the attachment** *and* the server already has the bytes. Not every originator includes a digest.

**Conclusion:** a content hash **cannot be the primary key**. The client always has exactly one thing —
**the originator's attachment `url`** (it is in the verbatim object). So the primary key must be the **URL**;
the content hash is demoted to a **server-internal dedupe index** the client never computes or uses.

## The decision

### The primary key is the originator's attachment URL; the client rewrites it to a same-origin proxy IRI

The **client's render boundary** (a small, pure, reusable helper in the client library — not the sample)
rewrites every **cross-origin** attachment `url` to a same-origin proxy IRI on the client's own server:

```
cross-origin url  →  {baseUrl}/ap/v1/media/proxy?url={percent-encode(originator-url)}
same-origin url   →  unchanged (already local — e.g. locally-authored media from 20.4 (a))
```

The browser then loads `<img src="{baseUrl}/ap/v1/media/proxy?url=…">` — a **same-origin** request to the
client's *own* server, so there is **no cross-origin media fetch and no CORS block**. This works for **every**
stream (outbox, inbox, liked, featured, communities, followed, synced peers, **and cold external objects**)
because it only ever needs the **URL**, which is always present in the verbatim object. No bytes, no digest,
no hash required in the client.

**Why the long-URL form (`?url=…`) rather than a short-token form:** it is **one hop** — the browser loads
the proxy IRI directly and the server resolves it, with **no client round-trip** to mint a token. (A
short-token variant — `POST {url}` → `{shortid}` → `GET /media/{shortid}` — adds a round-trip per cold
attachment and is only worth it if the long URLs ever become a real problem; they won't in practice. Every
real-world image proxy — Cloudflare, Wikipedia, etc. — uses the long-URL form.)

**Why the rewrite lives in the client boundary (not the server's collection serializer):** the server's
collections (outbox/inbox/liked/featured/communities/followed) **serve the canonical object verbatim** — a
remote peer reading any of them sees exactly what the originator sent. The "turn an attachment into a
browser-loadable same-origin IRI" decision is a **presentation concern of the consuming browser client**, and
it is invisible to the federation wire. This is what keeps the wire AP-native and future-proof: if the spec
later defines browser media reading, the canonical objects are already spec-correct and we change (or drop)
the client's render boundary — **nothing in the stored/wired data changes**.

### The server: `GET /ap/v1/media/proxy?url={originator-url}` — fetch-once, store, serve same-origin

A new **public**, **long-cacheable** media-proxy route. On a request:

1. **Decode** the percent-encoded `url` query param (the originator's attachment IRI).
2. **Look up** the media store by that URL (a URL→mediaId index). If present, **serve the cached bytes**
   (no re-fetch).
3. If absent, **fetch the bytes** server-side (an unsigned outbound GET — media hosts do not validate
   HTTP signatures; this reuses the server's existing `IHttpClientFactory`-backed outbound-fetcher pattern,
   the same one used by the actor-document / remote-collection / WebFinger fetchers and the proxy route).
   **Store** the bytes in the media store, **keyed by URL** and additionally indexed by **content hash**
   (SHA-256 of the bytes) for dedupe.
4. **Serve** the bytes with the remote `Content-Type` and a long `Cache-Control` (`max-age=31536000,
   immutable` — a given originator URL serves stable bytes; the store is immutable per URL).

**Failure handling:** if the fetch fails (remote 404, timeout, non-2xx), the route returns **502** (bad
gateway). The client's `<img onerror>` handler then falls back to a **link-out** (an `<a>` to the raw
originator URL) so a human can still open the media directly — the degraded case is "a link, not a broken
image," never a silent failure.

### Eager-warm: on by default — "if the server sees any media, store it"

Per the operator directive (2026-09-02), **eager-warm is on by default**: when the server **stores** an
object that carries attachments (an inbound `Create`, followed-community content, a synced peer's object),
it **pre-fetches each attachment by URL** into the media store so the proxy serves it **immediately** on
first render (no cold-fetch latency on the hot path).

**Scope (by construction):** eager-warm applies to **stored** content — the content the server actually
sees and persists (inbound, followed, community, synced). **Cold external browsing** (a random outbox the
server has never stored) is **lazy by nature**: the server only learns of the attachment URL when the browser
hits the proxy, at which point it fetches-once and caches. Both paths converge on the same media store +
proxy route; the user-visible effect is identical (a warmed view loads instantly; a cold first view triggers
a one-time fetch, then is cached).

**Cost acknowledgment (explicit, not silent):** eager-warm adds outbound fetches to the store path. A note
with 4 large images on a slow/flaky remote host adds latency to inbound processing. Mitigations:
(i) warm fetches are **best-effort and non-blocking** relative to the delivery/store completion (a failed or
slow warm does not fail or stall the store — the proxy will simply lazy-fetch on first render); (ii) the
warm is **idempotent** (a URL already in the store is not re-fetched); (iii) it is **configurable** (a
`MediaOptions.EagerWarm` flag, default **on**) so an operator can turn it off if the outbound cost is
unacceptable on a constrained instance.

### The content hash is a server-internal dedupe index (the client never uses it)

When the server stores bytes (via eager-warm or proxy fetch), it computes **SHA-256(bytes)** and records a
`hash → mediaId` index alongside the `url → mediaId` index. If a **different** originator URL later serves
the **same** bytes (the same image mirrored on two hosts), the server can **dedupe** (one stored copy, two
URL keys pointing at it). The **client is entirely ignorant of the hash** — it always renders off the URL.
The hash is purely a storage-efficiency optimization on the server; it is **not** part of the wire, the
client contract, or the compliance story.

## Consequences

- **New client boundary (pure, reusable):** an attachment-rewrite helper in `Iris.Client` (or `Iris.Core`
  if kept dependency-free) — given an object's attachments + the instance base URL → same-origin media IRIs
  (same-origin → pass through; cross-origin → `/ap/v1/media/proxy?url=…`). Every consuming surface (outbox,
  inbox, liked, featured, communities, followed) gets correct same-origin rendering for free; the UI
  (`ObjectView`) stays dumb — it renders `<img src>` from the already-rewritten attachment list.
- **New server seam:** `IMediaFetcher` (interface + `IHttpClientFactory`-backed implementation) — an
  unsigned outbound fetch of a media URL → bytes. Reuses the existing outbound-fetch pattern.
- **New server route:** `GET /ap/v1/media/proxy?url={originator-url}` (public, long-cacheable, fetch-once,
  store, serve; 502 on fetch failure).
- **Media store extended:** the existing `IMediaStore` gains a **URL-keyed** store/index (in addition to the
  current upload-keyed form) and a **content-hash dedupe index**. Both the in-memory and file-backed
  implementations are extended.
- **Eager-warm hook:** the inbound-object store path (`CreateActivityHandler.StoreEmbeddedObjectAsync` and
  the followed/community/synced equivalents) triggers a best-effort, non-blocking warm of the object's
  attachments by URL (gated on `MediaOptions.EagerWarm`, default on).
- **The wire is unchanged in every collection** — outbox, inbox, liked, featured, communities, followed,
  synced peers all continue to serve the **canonical, verbatim** object. Decision 055 (server is the sole
  id authority) and 056 (c) (no id rewrite; inbound keeps the originator's id) are **unchanged**.

## What is deliberately NOT in this decision

- **No id rewrite, no document mutation.** The stored ActivityStreams object is never modified; only the
  client's *render* rewrites a `url` to a same-origin proxy IRI.
- **No rewrite of any public collection serializer.** The rewrite is in the client boundary, so the server
  never emits a non-canonical object on any wire.
- **No client-side content hash.** The client renders off the URL; the hash is server-internal.
- **No short-token two-hop variant** (the long-URL one-hop form is the default; the token form is a
  documented fallback if long URLs ever become a problem).
- **No new NuGet package.** The fetcher reuses the existing `IHttpClientFactory`-backed outbound pattern;
  the hash is `System.Security.Cryptography` (BCL).

## Alternatives considered

- **Content hash as the primary key** — **rejected.** The client cannot compute it without the bytes, so it
  has a coverage hole for cold external browsing (a random outbox the server has never seen). Demoted to a
  server-internal dedupe index.
- **Server rewrites the attachment URL in the collection serializer (the inbox GET, or all collections)** —
  **rejected** (superseded by the client-boundary approach). It would make the *server* emit a non-canonical
  object, coupling the presentation concern to the wire and to specific endpoints. The client-boundary
  rewrite keeps every collection verbatim and future-proofs against the spec catching up.
- **Eager-warm off by default (pure lazy)** — **rejected** per the operator directive (eager-warm on by
  default). Kept as a config flag (`MediaOptions.EagerWarm`) so it can be disabled on constrained instances.
- **Short-token two-hop proxy** — **rejected as the default.** Adds a client round-trip per cold attachment;
  the one-hop long-URL form is simpler and works cold. Retained as a documented fallback.
- **Client-side CORS workaround (e.g. `no-cors` mode, canvas re-encode)** — **rejected.** `no-cors` yields an
  opaque response (unreadable bytes; cannot re-host); a canvas re-encode is blocked by CORS tainting for the
  same reason. Only a server-side fetch avoids the CORS block, which is exactly what this decision does.

## Compliance posture (the payoff)

- **Wire (all collections): 100% AP-native, verbatim.** A remote peer reading outbox/inbox/liked/featured/
  communities/followed sees exactly what the originator sent.
- **Local media bytes are a cache, keyed by URL (and deduped by content hash).** The wire does not depend on
  them.
- **The client is the only thing that rewrites a URL, to a same-origin IRI its own server understands.** The
  rewrite is a presentation concern in the consuming client — invisible to the federation wire.
- **If the spec catches up** and defines how a browser reads collections + media, the canonical objects are
  already spec-correct; we change (or drop) the client's render boundary (or the proxy route) to follow the
  spec's mechanism. **Nothing in the stored/wired data has to change.**
