# 137.2 — Lemmy community browse: client-side capability-aware IRI resolution

**Date:** 2026-09-14
**Slice:** 137.2 (PLAN "Up Next" — make the Iris Blazor WASM client browse Lemmy communities by
resolving feed/members IRIs from the community document's capabilities)
**Status:** **DONE.** Client-side capability detection + feed/members IRI resolution implemented,
unit-tested (11 new tests), and wired into `CommunityDetail.razor`. Full client + web test suites
pass (182 + 100). No server-side changes.

## What changed

The Iris Blazor WASM client previously fetched a community's feed from `<iri>/feed` and members
from `<iri>/members` (Mastodon/Pleroma convention). Lemmy does not serve those routes — its posts
live in `<iri>/outbox` and its members in `<iri>/followers`. The fix is **client-side**: the client
reads the community's own document and resolves the feed/members IRIs by what the server supports.

### New capability methods in `IrisDocumentExtensions.cs` (`src/Iris.Client/`)

| Method | Purpose |
|---|---|
| `IsLemmy()` | Returns `true` when the document's `@context` contains `https://join-lemmy.org/context.json` (the authoritative Lemmy signal). Handles both string and array `@context` shapes. |
| `ResolveFeedIri(Iri, string)` | 3-tier priority: (1) `iris:feed` extension (Iris instance), (2) `{actor}/outbox` if `IsLemmy()`, (3) `{actor}/feed` (Mastodon/Pleroma convention). |
| `ResolveMembersIri(Iri)` | 3-tier priority: (1) bare `members` extension (Iris), (2) `{community}/followers` if `IsLemmy()`, (3) `{community}/members`. |
| `IsActivityFeed(Iri, string)` | `true` when the resolved feed is a Lemmy outbox (needs content filtering to drop social activities). |
| `ContainsLemmyContext(JsonElement)` | Private helper for `@context` detection. |
| `AppendPathSegment(Iri, string)` | Private helper for path-append fallbacks. |
| `LemmyContextIri` | Constant: `https://join-lemmy.org/context.json`. |

### `CommunityDetail.razor` wiring (`apps/Iris.Web.Client/Components/Pages/`)

- `FeedIri` property → `CommunityDoc.ResolveFeedIri(iri)`.
- `MembersIri` property → `CommunityDoc.ResolveMembersIri(iri)`.
- `FeedIsActivityFeed` property → `CommunityDoc.IsActivityFeed(iri)`.
- `FeedCollectionIri` → no search: `FeedIri`; search: only for non-activity feeds (Iris `/search?q=`),
  activity feeds fall back to plain feed (Lemmy has no search endpoint).
- `FeedItemFilter` static predicate — keeps `Create` of `Note`/`Article`/`Object` (Lemmy `Page`) and
  any `Announce`; drops social activities (`Follow`/`Like`/etc.). Applied via `PagedCollection`
  `ItemFilter` param only when `FeedIsActivityFeed` is `true`.
- `LoadMemberCountAsync` → `CommunityDoc.ResolveMembersIri(communityIri)`.
- Added `@using ActivityObject = KristofferStrube.ActivityStreams.Object` type alias to resolve the
  `Object`/`object` ambiguity in the Razor compilation context.

### Unit tests (`tests/Iris.Client.Tests/IrisDocumentCapabilityTests.cs`)

11 new tests covering:
- `IsLemmy()` — Lemmy context array, bare context string, Mastodon context (false), no context (false).
- `ResolveFeedIri()` — Iris document (advertised `iris:feed`), Lemmy document (outbox), Mastodon document (`/feed` convention).
- `ResolveMembersIri()` — Iris document (advertised `members`), Lemmy document (`/followers`), Mastodon document (`/members` convention).
- `IsActivityFeed()` — true for Lemmy, false for Mastodon and Iris.

## Why client-side, not server-side

The user's design directive: the client should be ActivityPub-native and adapt to whatever
server/service it is talking to. A server-side adapter would couple the Iris server to Lemmy's
specific route conventions. The client-side capability check is general: it reads the document's
`@context` to detect Lemmy, and falls through to the Iris/Mastodon conventions otherwise. Future
server types (e.g. Pleroma with a different route scheme) can be added by extending the
`ResolveFeedIri`/`ResolveMembersIri` priority chains without server changes.

## What was NOT done (separate work items)

- **137.3 — Stale IRI hygiene:** `iris-dev2.luit.ink/c/test` is dead (TCP connection failure).
  Requires an operator data decision (re-point/remove) or a bounded timeout + error state in the
  community page. Not addressed here.
- **Manual Playwright validation:** The dev environment's advertised base (`https://iris.luit.ink`)
  differs from the browser origin (`http://localhost:8088`), so the `ProxyFallbackHandler` routes
  cross-instance reads to `https://iris.luit.ink/ap/v1/proxy/...` which is cross-origin from the
  browser and blocked by CSP. The proxy itself works correctly (verified via same-origin
  `browser_evaluate` fetch: `POST /ap/v1/proxy/https://lemmy.luit.ink/c/interop` → 200, returns the
  Lemmy `Group` with `join-lemmy.org/context.json`). This is a pre-existing infrastructure
  mismatch (advertised FQDN vs. localhost origin in the dev stack), not a bug in 137.2. The unit
  tests verify the capability logic end-to-end.

## Evidence

- `dotnet build src/Iris.Client/Iris.Client.csproj` → 0 errors, 0 warnings.
- `dotnet build apps/Iris.Web.Client/Iris.Web.Client.csproj` → 0 errors, 0 warnings.
- `dotnet test tests/Iris.Client.Tests --filter IrisDocumentCapabilityTests` → 11/11 passed.
- `dotnet test tests/Iris.Client.Tests` → 182/182 passed (no regressions).
- `dotnet test tests/Iris.Web.Tests` → 100/100 passed (no regressions).
- `dotnet test tests/Iris.Server.Tests --filter IrisDocumentExtensionReaderTests` → 3/3 passed
  (Iris feed/members behavior preserved).
- Same-origin proxy fetch: `POST /ap/v1/proxy/https://lemmy.luit.ink/c/interop` → 200, body is the
  Lemmy `Group` with `@context: ["https://join-lemmy.org/context.json", ...]`.
