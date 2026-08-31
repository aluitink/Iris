# 127 — Sample Explorer: route search through the canonical `SearchOf` derivation (§3.2)

**Status:** DONE — the §3.2 audit surfaced one derivation that the UI relied on only implicitly and
that the client built by ad-hoc string: the instance **search endpoint**. This slice makes
`IriExtensions.SearchOf` the single source of truth for where global search lives, in both the client
and the Actors page.

## Background — §3.2 audit

After S9/S10 closed the §3.1 client-method gaps, the remaining audit target was §3.2 (`IriExtensions`).
Most of those helpers are collection-derivation utilities exercised *implicitly* — the client's typed
methods (`GetFollowersAsync`, `GetRelaysAsync`, `GetLikedAsync`, `GetModerationAsync`, the Feed page's
followed feed, `RepliesOf` in `ObjectView`) derive their IRIs internally. The one with a clean,
direct, meaningful home was **`SearchOf`** (0 uses in `.razor`): the Actors page's directory called
`SearchAsync`, which built its IRI by string interpolation —

```csharp
var searchIri = new Iri($"{instanceBase.Value}/search?q=...&limit=...&offset=...");
```

— rather than the canonical `SearchOf` derivation. So the search endpoint had two sources of truth.

## Change

- **`src/Iris.Client/ActivityPubClient.cs`** — `SearchAsync` now derives the endpoint via
  `instanceBase.SearchOf()` and appends the `q`/`limit`/`offset` query:

  ```csharp
  var searchIri = new Iri($"{instanceBase.SearchOf()}?q={encodedQuery}&limit={limit}&offset={offset}");
  ```

  The produced IRI is byte-identical to before (`SearchOf` = `AppendSegment(iri, "search")`, which
  `UriBuilder`-normalizes any trailing slash), so this is behavior-preserving.
- **`samples/SampleBlazorClient/Pages/Actors.razor`** — the page now computes `SearchEndpoint =
  instanceBase.SearchOf()` and shows it under the results (`Searched <code>…/ap/v1/search</code>`),
  so the `SearchOf` derivation is surfaced directly in the UI (it went from 0 `.razor` uses to a live one).

## Tests

- **`tests/Iris.Client.Tests/SearchEndpointDerivationTests.cs`** (new, 2 tests):
  - `SearchAsync_RequestsTheSearchOfDerivedEndpoint` — with a recording `FakeHttpHandler`, `SearchAsync`
    requests exactly `/ap/v1/search?q=alice&limit=50&offset=20` (the `SearchOf` path + the query).
  - `SearchOf_DerivesTheInstanceSearchPath` — `new Iri("…/ap/v1").SearchOf()` = `…/ap/v1/search`.

## Verification

- `dotnet build` 0 warnings; **878/878** green (Iris.Client.Tests 110 → 112).
- Live: `GET /ap/v1/search?q=alice` against the compose stack returns the expected `OrderedCollection`
  (alice + a note) — the `SearchOf`-derived endpoint the refactored client hits, confirming the change is
  behavior-preserving.

## Note

This is the last §3.2 helper with a direct, meaningful UI home that was previously only implicit. The
other collection helpers (`FollowersOf`, `LikedOf`, `BlocksOf`, `RelaysOf`, `FeedOf`, `RepliesOf`,
`OutboxOf`, `InboxOf`) are all already exercised through the client's typed methods and the screens that
drive them (Actors detail cards, Feed, ObjectView replies, Compose outbox, S10 deliver). The §3.2 audit is
now closed.
