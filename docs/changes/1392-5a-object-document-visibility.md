# 139.2-s5a — Object-document endpoint visibility gate

**Status:** Done.
**Closes:** deferred surface (a) from 139.2-s5: "object-document endpoint — add a recipient check
(404/403 for a non-recipient on non-public content)."

## What was built

The object-document endpoint (`GET /ap/v1/{**path}`) previously served **any** stored object to
**any** requester — an anonymous user who knew a DM's IRI could fetch it. Now, a non-public
(followers-only or direct) content object **authored by a local actor** is only served to a
requester who is a named recipient (appears in `to`/`cc`) or the author (`attributedTo`). All
other requesters get **404** (not 403 — 404 hides the object's existence, matching the Mastodon /
Pleroma convention).

### The check

In `ObjectDocumentHandler` (`ActivityPubServerExtensions.cs`), after resolving the stored object
and the authenticated requester:

1. If the object is a `Tombstone` or a minted activity (not a content `IObject`), skip the check
   (tombstones carry no audience; activities like `Follow`/`Like` are not visibility-gated).
2. Read the object's `attributedTo`. If the first author is a **local** actor (via
   `ILocalActorResolver.IsLocalActorAsync`), apply the existing `VisibilityFilter.IsVisibleTo`
   predicate (public, or named recipient, or author).
3. If the object is not visible to the requester, return `Results.NotFound()`.

### Federation impact: none

Remote objects (authored by non-local actors) are **exempt** from the gate. The remote instance
already has its own copy (federation stores the full document on receipt), and the origin
instance's copy is not the authoritative source for the remote's read path. A remote instance
fetching a non-public object from the origin to store/deliver it still gets 200 — the
`IsLocalActorAsync` check returns false for remote authors, so the gate is skipped.

### Client impact: none

The WASM Blazor client signs requests with the session actor's key. When a logged-in user fetches
their own post or a DM addressed to them, the signature resolves to their actor IRI, which
matches the author or a named recipient → 200. Public posts are visible to everyone regardless.

## Test counts

+9 tests (`tests/Iris.Server.Tests/ObjectDocumentVisibilityIntegrationTests.cs`):

- **Public note:** anonymous → 200, other actor → 200.
- **DM (to: bob):** anonymous → 404, non-recipient (carol) → 404, author (alice) → 200.
- **Followers-only (cc: followers):** anonymous → 404, non-recipient (carol) → 404, author (alice) → 200.
- **Tombstone:** anonymous → 200 (tombstones are always served).

Verification: full solution build clean (0 warnings / 0 errors); full suite green — **2,260 tests,
0 failed** across 11 projects (Iris.Server.Tests now 1,357, +9).

## Decisions

- **404, not 403.** A 404 hides the object's existence — a non-recipient cannot enumerate
  non-public IRIs to confirm they exist. This matches Mastodon (which 404s non-recipient DMs) and
  Pleroma. A 403 would confirm the object exists, leaking information.
- **Gate only local objects.** Remote objects are exempt because the remote instance already has
  its own copy. Gating remote objects would break federation (a remote instance fetching a
  non-public object from the origin to store/deliver it would get 404).
- **Use the existing `VisibilityFilter.IsVisibleTo` predicate.** No new visibility logic — the
  same rule that filters the public feed, global search, and follow feed is applied here. The
  author clause (`attributedTo`) is what lets an actor see their own DMs.
- **Check `attributedTo` (not `actor`).** Content objects (Notes, Articles) use `attributedTo`
  for authorship. The `actor` property is for activities, not objects.
