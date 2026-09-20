# Shared inbox: `POST /ap/v1/shared-inbox` route (was advertised but unhandled)

**Date:** 2026-09-20
**Scope:** Inbound federation — the instance-wide shared inbox.

## Problem

Every actor document advertises `endpoints.sharedInbox`
(`https://iris.luit.ink/ap/v1/shared-inbox`, set via `Iris:SharedInboxIri`), but the server
implemented **no POST handler** for that route. A remote sender that prefers the shared inbox —
notably **Mastodon**, which coalesces delivery targets by `preferred_inbox_url` and delivers every
activity to the shared inbox rather than a per-actor inbox — POSTed its activities to
`/ap/v1/shared-inbox`, which fell through to the catch-all. The catch-all returned **200 and
silently dropped the body** (no `BoxItem`, no `Inbox` log, no handler run).

**Symptom:** a local Iris follower of a shared-inbox-preferring sender never received that
sender's posts — including after an unfollow/re-follow cycle (the "still seeing messages from an
unfollowed user" / "no longer receive posts" observation in the `followtest1`→`mstest` live
interop, [change doc 993](993-local-actor-resolver-fix.md)). A direct signed POST to the per-actor
inbox (`POST /ap/v1/u/{handle}/inbox`) returned 202 and processed correctly; the same payload to
the shared inbox returned 200 and was dropped.

## Fix

A new route `POST /ap/v1/shared-inbox` (`SharedInboxHandler`,
`src/Iris.Server/ActivityPubServerExtensions.cs`) now handles the advertised shared inbox. It
verifies the signature (or Bearer token), buffers + parses the body (the recipient is **not** in
the URL — it must be read from the activity), resolves the intended local recipient(s), and routes
the delivery through the same pipeline as a per-actor inbox POST (`HandleInboxPostAsync`),
reusing the per-peer rate limit, the activity handlers, and the 202 contract.

Recipient resolution:

- **Content activities (Create, Announce):** the intended recipients are the **local followers of
  the author** (the activity's `actor`). The handler fans the delivery out to each local follower
  (suppressed for a follower who has blocked the author, F-07). This mirrors the per-actor
  `CreateActivityHandler` local-follower handling in reverse: a shared-inbox-preferring sender
  delivers to the one shared URL, and Iris expands it to the local actors who follow the author.
- **Object-addressed activities (Follow, Accept, Reject, Undo, …):** routed to the activity's
  `object` (e.g. a Follow to a local actor).
- **No local recipient** (the author has no local followers, or the object is not local): the
  delivery is accepted (202) and dropped — a 4xx would make the sender retry a delivery this
  instance can never process.

The activity is stored idempotently by IRI (`InboxProcessor`'s add-if-absent guard), so a
re-delivery or a multi-recipient fan-out is loop-safe.

## Why fan-out (not a single recipient)

A Create/Announce from a remote sender has a **remote author** (not a local recipient in the URL),
so there is no single local actor to route to. The correct local recipients are the local actors
who follow that author — exactly the set a per-actor inbox would have fanned out to. Fan-out is
the shared-inbox equivalent of that per-actor fan-out, and it is what makes a
shared-inbox-preferring sender's posts reach local followers at all.

## Tests

`tests/Iris.Server.Tests/Security/SharedInboxIntegrationTests.cs` (4 tests, two live in-process
instances, A hosts `alice`, B hosts `bob` + `carol`):

1. **Follow → shared inbox** is routed to its object (`bob`) and records the follow edge (same
   outcome as a per-actor inbox delivery).
2. **Announce → shared inbox** (alice re-posts; bob follows alice) is fanned out to alice's local
   follower (bob) and stored — the "shared-inbox-preferring sender posts, local follower receives
   it" path that was silently dropped.
3. **Create (author not local) → shared inbox** is accepted (202) and dropped.
4. **Follow to a remote actor → shared inbox** is accepted (202) and dropped (no edge recorded).

Full fast suite green: 1891 passed, 0 failed (4 new).

## Notes / out of scope

- **Lemmy Undo (unfollow) 400:** a separate, pre-existing blocker — Lemmy's shared-inbox Undo
  path returns 400 (dead-lettered) even though the Follow is accepted. Likely needs the original
  Follow's `id` or a different Lemmy inbox shape; tracked separately (not part of this route fix).
- The per-actor inbox (`POST /ap/v1/u/{handle}/inbox`) is unchanged and remains the route for
  senders that address a specific actor.
