# 155 — Community lifecycle on recreation: full state survives `down`/`up`

> 2026-09-01 · Slice 19.5.6 (community peers — lifecycle) · a community created in a prior turn survives a recreation with all collections intact

## What was built

**19.5.6** asks: "A community created in a prior turn (with members, follows, content) survives
`down`/`up` (volume-backed) with all collections intact."

This is the community analogue of **19.3.7 (Recreation stability)**, which pinned the *delivery-queue*
half of recreation (a re-created instance replays its already-delivered `Create` from its on-disk
journal; the replay is a harmless no-op, not a re-delivery storm). **19.5.6** is the *community-state*
half: the community's own persisted state — its document, its memberships, its follow/follower edges,
its moderation edges, and the member content its feed is derived from — must survive the recreation.

The implementation already existed: the file-backed `FileBackedCommunityStore` round-trips **all** of
the community's state through a single JSON file (the `communities` document map plus the `members`,
`follows`, `followers`, `blocks`, `flags`, and `mutes` set sections — the last three added by change
153 for 19.5.4), and the file-backed `FileBackedActivityStore` round-trips the member outboxes. What
was missing was a **pin**: no test exercised the *combined* community state across a restart. The
existing `CommunityStore_DocumentsAndMembers_SurviveRestart` covered only the document + members +
followers, and `ActivityStore_Outbox_SurvivesRestart` covered an actor outbox in isolation.

This slice adds the pin. It seeds a **full** community state — members, an outbound follow, an inbound
follower, all three community-scoped moderation edges (a block, a flag, a mute), and two members'
outbox activities (the content the unified feed is derived from) — in one `FileBackedPersistenceProvider`
over a temp directory, disposes it (the `down`), builds a **second** provider over the same directory
(the `up`), and verifies **every** collection is unchanged.

## Key types & files

- `src/Iris.Server/Persistance/Stores/FileBackedCommunityStore.cs` — **unchanged** (the
  `communities`/`members`/`follows`/`followers`/`blocks`/`flags`/`mutes` sections + the
  `CommunityToDocument`/`CommunityFromDocument` round-trip already cover the full state).
- `src/Iris.Server/Persistance/Stores/FileBackedActivityStore.cs` — **unchanged** (the member outbox
  round-trip already exists, pinned by the pre-existing `ActivityStore_Outbox_SurvivesRestart`).
- `src/Iris.Server/Persistance/FileBackedPersistenceProvider.cs` — **unchanged** (bundles the
  per-store files under one directory; a fresh provider over the same directory is the `down`/`up`
  simulation).
- `tests/Iris.Server.Tests/Persistance/FileBackedPersistenceTests.cs` — **new** test
  `Community_FullState_MembersFollowsFollowersModerationAndContent_SurvivesRestart` (see below).

## Tests

1249 → **1250** passing (+1: the community full-state survival integration test).
Full `dotnet test` green; `dotnet build` clean (`TreatWarningsAsErrors`); `dotnet format` clean on the
changed file.

- `Community_FullState_MembersFollowsFollowersModerationAndContent_SurvivesRestart` — the central
  19.5.6 assertion: a community is seeded with two members, an outbound follow (to a remote actor), an
  inbound follower, a community-scoped block + flag (of one member) + mute (of another), and two
  members' outbox `Create`s (+ their notes). The provider is disposed (`down`) and a fresh provider is
  built over the same directory (`up`). The re-created instance then re-reads: the community document
  (still a `Group`), both members, the follow edge, the follower edge, the block/flag/mute edges, and
  both members' outbox activities (each a single `Create`, retrievable by IRI) — every collection
  unchanged.

## Live verification (deferred — a live item)

The full-state survival is pinned by the new test (every collection re-read after the recreation). The
**live** half — a real Docker `down` (no `-v`, so the named volume survives) + `up` of a seeded
community, then resolving its collections over the public FQDNs — is the remaining live-verification
item for 19.5.6. It exercises the same file-backed round-trip the test covers, plus the volume mount
and the wire read path (already proven for the actor state in 19.3.7's two-instance Docker env).

## Decisions

- **The `down`/`up` is simulated by a fresh `FileBackedPersistenceProvider` over the same directory.**
  This is exactly what the production `down` (no `-v`) + `up` does at the persistence layer: the
  per-store JSON files are replayed on construction. It is the same simulation the pre-existing
  `*SurviveRestart` tests in this file use, and it keeps the test CI-runnable (no Docker, no public
  FQDNs required). The live Docker drive (volume mount + wire read) is the deferred live item.
- **The pin is the *combined* state, not the per-store tests.** 19.5.6 is about a *community* surviving
  a recreation, which spans two stores: the community store (document + members + follows + followers +
  moderation) **and** the activity store (the member outboxes the feed is derived from). A single test
  that seeds both stores in one provider and re-reads both after the recreation is the faithful
  expression of "a community with members, follows, and content survives `down`/`up` with all
  collections intact" — it cannot be decomposed into the two existing isolated tests without losing the
  "one community, one restart" shape of the requirement.
- **The community's content is the members' outbox activities, not a separate "community content"
  store.** The unified feed is the union of the community's *local members'* outbox activities
  (`ICommunityFeedService`); there is no separate store of "community content." So "the community's
  content survives a recreation" is pinned by seeding the members' outboxes in the activity store and
  re-reading them after the recreation — the feed is derived from them and is therefore unchanged.
