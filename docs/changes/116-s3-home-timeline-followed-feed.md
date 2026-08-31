# 116 — S3: Home timeline (followed feed) + pagination

> 2026-08-31 · Phase 8 (Sample, second round) · Slice S3 (the followed feed, a core user journey with no UI)

## What was built

The **home timeline** — the union of the logged-on actor's followed actors' outboxes, newest-first,
de-duplicated — had no UI (gap list item: `GetFollowFeedAsync`, SAMPLE_EXPLORER_PLAN §3.2). A new
**Feed** page renders it, and its "Load more" button surfaces paged-collection enumeration
(`GetCollectionAsync` + `next`-link walking), which no other sample page exercised.

## The changes

### 1. `Pages/Feed.razor` — the followed-feed page (`@page "/feed"`)

- When logged on, enumerates the actor's `{actor}/feed` collection via the paged
  `IActivityPubClient.GetCollectionAsync(Iri, CollectionQuery, CancellationToken)`, starting from
  `Session.ResolvedActorIri` (falling back to `Session.ActorIri`).
- Renders each item through `<ObjectView>` (deep-linked per S2, so each feed entry's author / parent /
  mentions are clickable).
- **"Load more"** resumes the enumeration from the page's `NextPage` IRI (one page per click), walking the
  paged collection's `next` links — the `next`-link walking the plan called out for S3.
- Empty state: "No followed items yet. Follow an actor … to populate your timeline."
- Not-logged-on state: a prompt to log on.

**File:** `samples/SampleBlazorClient/Pages/Feed.razor`.

### 2. `Layouts/MainLayout.razor` — nav link

Added a `Feed` link to the nav bar (between `Instance` and `Actors`).

**File:** `samples/SampleBlazorClient/Layouts/MainLayout.razor`.

### 3. `tests/SampleBlazorClient.Tests/S3FollowFeedTests.cs` — in-process tests (2)

Hosts a real `Iris.Server` ActivityPub pipeline (in-memory) for the follow feed (mirroring
`S7ScreenTests.StartHost`), seeds `alice` (the follower) + `bob` (the followed), and exercises the feed the
same way the page does:

- **`FollowFeed_FollowerSeesFollowedActorsOutbox_NewestFirst`** — logs on as `alice`, records a follow edge
  to `bob` (signed `FollowAsync`, `202`), seeds `bob`'s outbox with 3 notes (via `PostNoteAsync` → outbox
  activities the feed reads), then asserts `GetFollowFeedAsync(alice)` yields the followed actor's outbox
  items (every item is one of `bob`'s activities).
- **`FollowFeed_PagedCollection_CarriesNextLink`** — asserts the feed is served as a *paged* collection:
  requesting `?limit=2` over 3 items yields a 2-item page carrying a `next` link pointing at `page=2`
  (the "Load more" continuation).

**File:** `tests/SampleBlazorClient.Tests/S3FollowFeedTests.cs`.

## Verification

- **Full solution builds with 0 warnings** (`TreatWarningsAsErrors`).
- **All test suites green:** Iris.Client.Tests 110 · Iris.Server.Tests 656 · Iris.Client.Extensions.Tests 29 ·
  SampleBlazorClient.Tests **65** (was 63, +2 S3) = 860 total.
- **Browser-verified:** the `/feed` page renders (the "Home timeline (followed feed)" heading, the not-logged-on
  prompt, and the new `Feed` nav link). The logged-in browser flow could not be exercised end-to-end in this
  environment: the only reachable local ActivityPub server was an orphaned root-owned process on port 8081 whose
  CORS is locked to origin 8090 (also orphaned), so the WASM origin was refused. That is an environment
  constraint, not a code defect — the in-process tests exercise the identical `GetFollowFeedAsync` /
  `GetCollectionAsync` API the page uses.

## Notes

- `CollectionQuery.Limit` is a *max total items to enumerate*, not a page size. The server page-sizes via the
  `?limit=` query param (default 20). The page's "Load more" walks the server's `next` links; the test forces a
  multi-page feed by requesting `?limit=2` directly against the feed IRI.
- The follow feed reads the **activity store** (the outbox's recorded activities), not the object store — so the
  test seeds notes via `PostNoteAsync` (which records a `Create` per note in the outbox), not by inserting bare
  objects.
