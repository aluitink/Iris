# 135.1b(2) — Prove a real Lemmy community `Group` round-trips end to end

**Date:** 2026-09-13
**Slice:** 135.1 (continuation — a test-only addition that hardens the 135.1a/135.1b interop verification)
**Commits:** `91248a7` — `test(server): prove a real Lemmy community Group round-trips end to end (135.1b)`

## Why

135.1a proved the remote community `Group` persistence logic with a **minimal** test fixture
(`{ id, type, preferredUsername, name }`). 135.1b deployed a real Lemmy instance whose community
serves a **genuine** Lemmy `Group` document with the full Lemmy-specific shape. The gap: no test
exercised the **real** Lemmy document shape through the fetch → persist → serve path. The live
follow-interop (driving the real follow) is blocked on external infra (host nginx 400s the POST
federation paths; the running Iris container's actor Basic-auth creds don't match the PLAN notes),
so this test provides the strongest in-process proof that Iris correctly handles a real Lemmy
community — the crux of the 135.1 goal.

## Change (test only — no production code changed)

`IrisActorDocumentFetcherTests.GetActor_RealLemmyCommunity_PersistsAndServesIt` embeds the
**verbatim** document served by the Phase 135.1 Lemmy deployment (`https://iris-dev2.luit.ink/c/test`),
and drives it through the full path:

1. `ActivityJson.Deserialize<IObjectOrLink>(…)` — the polymorphic converter materializes the real
   Lemmy `Group` (asserted `is Group`, with the correct `id`/`preferredUsername`).
2. `IrisActorDocumentFetcher.GetActorAsync` (with a `RemoteCommunityPersister`) — the real `Group` is
   **persisted** to the durable `ICommunityStore` **and returned** (as an `Actor`) for key resolution.
3. The cached-actor endpoint (`GET /ap/v1/actor?iri=…`) — serves the persisted Lemmy community as-is.

The embedded JSON carries the genuine Lemmy shape a minimal fixture would not:
- an `@context` **array** including `https://join-lemmy.org/context.json`,
- a nested `source` (`content` + `mediaType`),
- `sensitive`, `postingRestrictedToMods` (Lemmy booleans),
- `endpoints.sharedInbox`,
- `featured` (a collection IRI),
- an empty `language` array,
- `published`,
- `attributedTo` pointing at the community's `/moderators` collection.

The test asserts the Lemmy-specific fields **round-trip** through the store: `source.content`,
`endpoints.sharedInbox`, `featured`, and `postingRestrictedToMods`.

## Key finding (why the Lemmy fields survive)

The `Group` type in **KristofferStrube.ActivityStreams 0.2.4** preserves unknown/extension JSON
fields on a deserialize → serialize round-trip (verified empirically: all of `source`, `endpoints`,
`sensitive`, `postingRestrictedToMods`, `featured`, `language`, `published` are re-emitted). So the
real Lemmy community is stored and served **as-is** — its Lemmy-specific fields are not dropped.
This is what makes a remote Lemmy community usable as known content (and, later, for posting/follow
interop) without Iris needing to model each Lemmy extension.

## Verification

- `dotnet build` clean (0 warnings / 0 errors).
- The new test passes; `IrisActorDocumentFetcherTests` is 6/6.
- Full suite green except the **pre-existing** `FollowEdgeConvergenceIntegrationTests` timing flake
  (passes in isolation; unrelated to this change — it is a Delivery convergence test that times out
  under full-suite load).

## Files

- `tests/Iris.Server.Tests/Security/IrisActorDocumentFetcherTests.cs` (new test + `using` for
  `System.Text.Json` / `Iris.Testing`)
