# 118 — S5: Home page renders the community feed items (not just the count)

**Status:** DONE — full solution green (`dotnet build` 0 warnings; `dotnet test` 864/864).

## Objective

The landing page (`Home.razor`) called `GetCommunityFeedAsync` but **discarded the items**, showing only
`FeedCount` ("Fetched N item(s)"). The plan (S5) calls for rendering the actual recent items (via the
deep-linked `<ObjectView>`, per S2) so the landing page shows real content instead of a bare number.

## Changes

### `samples/SampleBlazorClient/Pages/Home.razor`

- Replaced the `FeedCount` state with a `FeedItems` list (`List<IObjectOrLink>?`, null = not yet loaded;
  empty = the community is empty / the feed could not be read).
- The "Community feed" card now renders each item via `<ObjectView Item="item" />` (the same deep-linked
  component S2 wired up), inside a `<ul class="object-list">`. An empty feed shows a muted "No feed items
  (is the seeded `iris` community empty?)" note.
- `LoadFeedAsync` now collects the items (capped by a new `FeedPageSize = 5` via
  `CollectionQuery(Limit: 5)` so the landing page stays light; the full feed is on the `/feed` page) and
  stores them. A read failure yields an empty list (the card shows the empty-state note rather than
  hiding).
- `LogOut` resets `FeedItems = null` (the card is hidden again until the next logon reloads it).
- Added `@using Iris.Client.Collections` + `@using KristofferStrube.ActivityStreams` for
  `CollectionQuery` / `IObjectOrLink`.

## Test coverage

- `S5CommunityFeedTests` (2 new) — host the seeded in-process `SampleServer` (the `iris` community, whose
  feed merges alice + bob's outboxes) and, logged on as `alice`:
  - **CommunityFeed_YieldsRealItemsWithResolvableIris** — the community feed yields ≥2 items, each an
    `IObjectOrLink` carrying a resolvable IRI (the content `<ObjectView>` renders; previously only the
    count was shown).
  - **CommunityFeed_LimitCapsEnumeration** — a small `CollectionQuery.Limit` (2) caps the enumeration to at
    most that many items while still returning items (the landing page's cap).
- Full solution green: **864/864** (SampleBlazorClient.Tests now 69: 65 → +4 across S4/S5).

## Notes

- The community feed is served at `/ap/v1/c/{name}/feed` (the union of the community's members' outbox
  activities, newest first), read through the client's `CollectionPageCache` like any other collection.
- The existing `S6ScreenTests.Community_FeedMembersAndSearch` already exercised `GetCommunityFeedAsync`
  (returns ≥2 items); the S5 tests are the focused landing-page-cap + item-resolvability assertions.
- Logged-in *browser* verification is blocked by the environment's orphaned root-owned 8081 server (CORS
  locked to origin 8090) — an environment constraint, not a code defect. The in-process tests exercise the
  identical client API + server pipeline the card uses.
