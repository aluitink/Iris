# 139.2-s5 — Filter the public feed + global search by audience/visibility

**Status:** Done (read-path gap closed for the two highest-impact surfaces).
**Closes:** the S1 privacy gap flagged in the Phase 139 closeout (139.2 Scenario 5) and first
documented in Phase 136.18.

## What was built

Iris modeled visibility purely with the ActivityStreams `to`/`cc` audience (no first-class
visibility field, no DB column). The read surfaces did **not** filter by that audience, so a
direct message (`to=[bob]`, no `as:Public`) was as visible as a public post: it appeared in the
public timeline and in global search to **everyone** on the instance — a significant privacy
violation (a user who sends a DM expects it to be private).

### The predicate — `VisibilityFilter`

New: `src/Iris.Server/Services/VisibilityFilter.cs` (internal static class). The single rule every
read surface applies:

- **Public** — a post is public when its `to`/`cc` contains the `as:Public` sentinel
  (`https://www.w3.org/ns/activitystreams#Public`), **or** when it names no audience at all.
  The "no audience ⇒ public" rule matches the codebase's existing `IsFollowReply` heuristic
  (which treats `GetAudienceIris().Count > 0` as non-public) and preserves legacy/remote content
  that carries no explicit `to`/`cc`.
- **Non-public (followers-only / direct)** — a post that names an audience but is not public is
  visible **only** to a requester who is one of its named recipients (appears in `to`/`cc`,
  case-insensitive IRI match).

API:
- `IsPublic(IObject?)` — public sentinel present, or no named audience.
- `IsVisibleTo(IObject?, Iri? requester)` — public, or requester is a named recipient (null object
  → visible; nothing contradicts visibility).
- `IsFeedItemVisibleTo(IObjectOrLink, Iri? requester)` — for feed items (typically a `Create`/
  `Announce` activity), extracts the first embedded object (or the item itself when it is a bare
  `IObject`) and applies `IsVisibleTo`; an item with no extractable object is kept, not dropped.

Built on the existing `IriExtensions.GetAudienceIris` / `IsPublicAudience` / `Iri.Public` helpers.

### Read surfaces wired in

**`GET /ap/v1/public/feed`** (`PublicFeedHandler` → `PublicFeedService.GetPublicFeedAsync`):
the handler already resolved the requesting actor via `ResolveAuthenticatedRequesterAsync` (it used
the IRI only for `isLiked`/`isShared` enrichment); it now resolves it **before** the feed call and
threads it into the service, which filters the feed items (via `IsFeedItemVisibleTo`) **before**
sort/truncate, so the `maxItems` cap applies to visible items. An anonymous / unsigned request sees
public content only; a signed request also sees the non-public items addressed to it.

**`GET /ap/v1/search`** (`GlobalSearchHandler` → `GlobalSearchService`): the handler gains an
`ISignatureValidator` and resolves the requesting actor, passed into `SearchAsync` /
`SearchPagedAsync`. The search **always** loads the matching content, filters it by visibility in
memory (the objects are already deserialized, so their `to`/`cc` is readable), applies the type
filter, and computes the `total` from the visible set. Because the store's `CountSearchMatchesAsync`
helpers carry no visibility predicate, the filtered path replaces the previous optimized count path
(the only production caller of `GlobalSearchService` is this endpoint, so the behavior change is
confined to the surface being fixed). Actors are unaffected — a directory entry is about a person,
not a specific post.

Both surfaces resolve identity from a valid **HTTP signature** only (the existing
`ResolveAuthenticatedRequesterAsync` helper). The WASM Blazor client authenticates via a cookie, not
a signature, so its requests resolve to a null (anonymous) requester on these two surfaces — which
is the correct behavior here: the **public** feed and **global** search should surface public
content to a signed-in browser user, and a signed-in user's own DMs / followers-only posts are not
public content, so hiding them on these surfaces is correct. (Cookie-based recipient visibility for
the *follow feed* and *object document* is a deferred surface — see below.)

## Test counts

+16 tests (`tests/Iris.Server.Tests/VisibilityFilterTests.cs`):
- **Unit** (`VisibilityFilterTests`, 8): the predicate directly — public, no-audience, DM,
  followers-only, public+named, null object, feed-item-with-activity, feed-item-bare-object,
  feed-item-without-object.
- **Service-level** (`VisibilityFilterFeedAndSearchTests`, 8): seeds an in-memory store with a
  public post, a followers-only post (→ bob), and a DM (→ carol), then asserts the public feed and
  global search each return the right items for an anonymous request vs. each recipient vs. a
  non-recipient, plus that the search `total` reflects only visible content and that the actor
  directory is unaffected by visibility.

Updated `CrossInstanceVisibilityIntegrationTests.DirectPost_StoredInOutbox_VisibleInPublicFeed_CurrentGap`
(previously pinned the gap) to assert the **fixed** behavior: the DM is still in the author's outbox
(owner-scoped, correct) but is **not** in the public feed or global search for an anonymous
requester.

Verification: full solution build clean (0 warnings / 0 errors, `TreatWarningsAsErrors`); full
`Category!=Slow` suite green — **2,418 tests, 0 failed** across 11 projects (Iris.Server.Tests now
1,339, +16).

## Decisions

- **No new DB column.** Visibility is derived from the jsonb `to`/`cc` in memory (the store already
  deserializes full objects for the search; the feed already deserializes activities). Adding a
  `Visibility` column would require a migration and the `IrisDbContext` design remarks explicitly
  avoid new columns. The in-memory filter is O(visible set) and the local surface is small.
- **Filter in the service, before truncate (feed) / compute total from filtered set (search).**
  This keeps the `maxItems` cap and the pagination `total` honest — a viewer is not returned a full
  page dominated by content they cannot see, and the total does not over-count hidden items.
- **Actors are never filtered.** A directory entry (an actor's name/handle/IRI) is not gated by a
  specific post's audience. (Whether a *deleted* or *muted* actor should be hidden from the
  directory is a separate product decision, out of scope here.)
- **Anonymous = public only; recipient = public + their content.** This is the Mastodon /
  ActivityPub convention and the minimal correct rule. No "followers-only" *follow-graph* check is
  done (i.e., a follower-only post is shown to its named `to` recipients; whether *all* followers
  should see it — a `cc`-to-follower-set model — is a product decision deferred below).

## Deferred surfaces (tracked)

1. **Follow feed** (`GET /ap/v1/u/{handle}/feed`): owner-scoped by construction (the viewer is the
   handle), so the privacy exposure is lower. A full fix would gate the request to the actor's owner
   (or a signed follower) and filter the merged follows' outboxes by audience. Deferred — the public
   feed + search were the confirmed S1 surfaces.
2. **Object document** (`GET /ap/v1/{iri}`): a non-recipient (even anonymous) fetching a specific
   object IRI still gets the full object. A fix would add a recipient check (404/403 for a
   non-recipient on non-public content). This is a larger design decision (it interacts with
   federation — remote instances legitimately need to fetch non-public content to store/deliver it)
   and is deferred.
3. **Federation visibility policy** (Gap #2 from 139.2-s5): whether a remote instance should
   *suppress* non-public content on receipt vs. *store it with a visibility marker* so its own read
   path can filter it. Once the local read path is filtered (this change), a federated-in DM is now
   hidden on the remote instance's public feed / search **only if** it was stored with its `to`/`cc`
   intact (it is — inbound stores the full document). The remaining question is whether to store
   non-public content at all on a remote instance. Deferred as a federation product decision.
4. **`cc`-to-follower-set model:** the current rule shows a followers-only post to its *named*
   recipients only. A richer model (followers-only visible to *all* followers via a computed
   follower set) is a product decision.
