# 153 — Community moderation surface: block/flag/mute at the community level

> 2026-09-01 · Slice 19.5.4 (community peers — moderation) · the community-level analogue of the person-level moderation (F-07)

## What was built

The person-level moderation (F-07) already exists and is fully implemented: a person's
`IModerationStore` holds the `block`/`flag`/`mute` edges (keyed by an actor IRI) — block/flag are
federated (a `Block`/`Flag` activity, recorded when either party is local) and mute is a local Basic-auth
decision — and the person feed excludes the content of the actors the person has blocked or muted.

**19.5.4** asks for the same at the **community** level: "Flag/block/mute at the community level where
supported; verify the moderation collections and that moderated actors' content is excluded from the
community feed." A community moderates the actors whose content it surfaces in its **unified feed**
(`ICommunityFeedService`). The key design point, carried over from the person path, is that the
community's moderation edges are **community-scoped** — recorded in the community's *own* moderation
sets, not the person `IModerationStore` — so one community's operator can block/mute/flag an actor
without affecting any other community's feed.

This slice adds that surface, reusing the person path's patterns:

- **Community-scoped moderation edges on `ICommunityStore`.** Nine new methods —
  `AddBlockAsync`/`RemoveBlockAsync`/`GetBlocksAsync`, `AddFlagAsync`/`RemoveFlagAsync`/`GetFlagsAsync`,
  and `AddMuteAsync`/`RemoveMuteAsync`/`GetMutesAsync` — each keyed by `(communityIri, actorIri)` (the
  community is the moderator, the actor is moderated). The `Add`/`Remove` return `bool` (idempotent: a
  re-add / a remove-of-absent is `false`), the `Get` returns the actor IRIs (empty when none). Both the
  in-memory and the file-backed stores implement them; the file-backed store round-trips the three sets
  through a new `blocks`/`flags`/`mutes` section of its document (mirroring `members`/`follows`/
  `followers`).
- **The community feed applies the community's moderation edges.** `CommunityFeedService` now takes an
  optional `ICommunityStore?` (injected via the persistence provider's `Communities` property, the same
  instance the service already reads membership from). `GetFeedAsync` reads the community's `blocks` +
  `mutes` sets and drops those members **before** reading their outboxes, so a blocked member's content is
  excluded (hard) and a muted member's content is excluded while the membership is kept (soft). A
  **flagged** member is *not* excluded — a flag is a moderation report surfaced in the community's
  `flags` collection for the operator to act on, not a content filter (mirroring the person feed, where
  only blocks and mutes filter the timeline). With no community store (the default) no filtering is
  applied (back-compat).
- **The community's moderation collections, served over the wire.**
  `GET /ap/v1/c/{name}/{blocks|flags|mutes}` returns the community's block/flag/mute edges as a paged
  `OrderedCollection`/`OrderedCollectionPage`, mirroring the person moderation collections
  (`GET /u/{handle}/{blocks|flags|mutes}`) for a `Group`. The community document now advertises the three
  links (`blocks`/`flags`/`mutes`) in its `ExtensionData`.
- **A community-scoped mute endpoint.** `POST /ap/v1/c/{name}/mutes/{target}` (Basic auth, the community's
  IRI is the credential seam — the same `IActorCredentialValidator` as the person mute and the community
  follow-decision endpoint) records the community's mute of `{target}`; `?unmute=true` removes it. Both
  are idempotent and return `204`; an unknown community `404`s, an unparseable target `400`s, and an
  unauthenticated request `401`s. A community **block/flag** is *not* exposed as a local `POST`: those
  are the federated `Block`/`Flag` activities, recorded on the community when either party is local — only
  the mute is a pure local decision.

## Key types & files

- `src/Iris.Server/Stores/ICommunityStore.cs` — the nine community-moderation methods (blocks/flags/
  mutes), documented as the community-scoped analogue of the person `IModerationStore`.
- `src/Iris.Server.InMemory/Stores/InMemoryCommunityStore.cs` — three `ConcurrentDictionary`-backed
  moderation sets + the nine methods (via shared `AddToSetAsync`/`RemoveFromSetAsync`/`GetSetAsync`
  helpers, the same pattern as the `members`/`follows`/`followers` sets).
- `src/Iris.Server/Persistance/Stores/FileBackedCommunityStore.cs` — the `blocks`/`flags`/`mutes`
  sections + the nine methods (`WithStateAsync`/`SnapshotAsync` over the set-map), and the
  `ToDocument`/`FromDocument` round-trip extended to the three new sections.
- `src/Iris.Server/Services/CommunityFeedService.cs` — the optional `ICommunityStore?` constructor
  parameter; `GetFeedAsync` reads the community's `blocks`/`mutes` and filters `orderedMembers` before
  reading outboxes (a flag does not filter).
- `src/Iris.Server/ActivityPubServerExtensions.cs` — the `ICommunityFeedService` DI factory now passes
  the persistence provider's `Communities`; a new `CommunityModerationCollectionHandler` at
  `GET /c/{name}/{blocks|flags|mutes}` (`community-moderation-collection-endpoint`); a new
  `CommunityMuteHandler` at `POST /c/{name}/mutes/{**target}` (`community-mute-endpoint`); the community
  document advertises the `blocks`/`flags`/`mutes` links.
- `tests/Iris.Server.Tests/CommunityModerationIntegrationTests.cs` — **new** integration test class
  (11 tests): the community document advertises the three collections; the empty collections' shape;
  unknown community `404` (read + write); an authenticated mute records the community edge (`204`); the
  mute appears in the community's `mutes` collection; a mute excludes the member's content from the
  community feed **without severing the membership** (soft) and an un-mute restores it; a block excludes
  content (hard) and an un-block restores it; a flag is recorded + read back but does **not** exclude
  content; an unauthenticated mute `401`s; an un-mute of a non-existent mute is a no-op (`204`); and the
  community moderation is scoped to the community (the person `IModerationStore` stays empty).
- `tests/Iris.Server.Tests/Services/CommunityFeedServiceModerationTests.cs` — **new** unit test class
  (6 tests): a blocked member is excluded, a muted member is excluded, a flagged member is *not*
  excluded, an un-block restores content, a service without a community store applies no filtering, and
  the moderation is scoped to the community being read.

## Tests

1231 → **1248** passing (+17: 11 community-moderation integration tests + 6 community-feed-moderation
unit tests).
Full `dotnet test` green; `dotnet build` clean (`TreatWarningsAsErrors`); `dotnet format` clean.

- `Mute_ExcludesMemberContentFromCommunityFeed_WithoutSeveringMembership` — the central 19.5.4 assertion:
  before the mute the member's post is in the community feed; after `POST /c/{name}/mutes/{target}`
  (`204`) the mute edge is in the community's sets, the membership is **intact**, and the member's
  content is excluded from the feed; after `?unmute=true` the edge is gone and the content returns.
- `Block_ExcludesMemberContentFromCommunityFeed` / `Flag_IsRecordedAndReadBack_ButDoesNotExcludeContent`
  — the block is a hard exclusion (the other member is unaffected); the flag is a report: it is recorded
  and served in the `flags` collection, but the flagged member's content stays in the feed.
- `CommunityModeration_IsScopedToTheCommunity_NotThePersonStore` — a community-scoped mute/block lives in
  the community's own sets, not the person `IModerationStore` (keyed by actor IRI); the person store
  stays empty.
- `Mute_Unauthenticated_IsRejected` / `ModerationCollections_UnknownCommunity_Return404` — the Basic-auth
  seam (`401`) and the unknown-community guard (`404` for both the read and the write).
- `WithoutCommunityStore_NoModerationFiltering` — the back-compat path: a service constructed without a
  community store merges every member regardless of recorded edges.

## Live verification (deferred — a UI/live item)

The endpoint + store + feed behavior is pinned by the 17 tests above (status codes, the stored edges, the
collection contents, and the feed-exclusion semantics). The **UI** half (a community "Moderation" screen
in the sample client wiring the `POST /c/{name}/mutes/{target}` + the block/flag collection reads) is the
remaining live/UI item for 19.5.4. The federated block/flag half (a signed `Block`/`Flag` activity
addressed to a community, recorded on the community when either party is local) reuses the existing
`BlockActivityHandler`/`FlagActivityHandler` delivery path (proven end-to-end for the person level in the
two-instance Docker env) and is exercised there; the community-scoped recording is the new piece pinned
here.

## Decisions

- **The community's moderation edges are community-scoped, in `ICommunityStore`, not the person
  `IModerationStore`.** A community moderates the actors it surfaces in its feed; that is a
  `community → actor` relationship distinct from a person's `actor → actor` moderation. Keeping the
  community's edges in the community's own sets (keyed by `communityIri`) means a block/mute/flag by one
  community does not leak into another community's feed or into the person store. The `IModerationStore`
  (keyed by a single actor IRI) cannot express "which community issued this", so a separate
  community-keyed surface is required rather than reusing it.
- **The community feed excludes blocked + muted members, but not flagged ones.** A block hides a
  member's content (hard); a mute hides it while keeping the membership (soft — the operator can un-mute
  without un-inviting); a flag is a *report* to be reviewed, not a content filter. This mirrors the
  person feed exactly (only blocks and mutes filter the person timeline), so the two surfaces are
  consistent in their moderation semantics.
- **Only the mute is a local Basic-auth POST; block/flag are federated activities.** A mute is an
  Iris-specific local decision (no ActivityStreams type, no recipient to notify) — exactly the shape of
  the person mute endpoint. A block/flag *is* an ActivityStreams type (`Block`/`Flag`) that federates;
  when either party is local it is recorded on the local actor/community's moderation sets by the
  existing inbox handlers, so there is no separate local write surface to add. This keeps the
  ActivityStreams-only invariant (19.6.1) intact: the only non-AS local write is the mute, which has no
  AS form.
- **The community store is passed to the feed service via the persistence provider, not as a standalone
  DI service.** `IFollowFeedService`/`FeedService` already read the person `IModerationStore` through
  `persistence.Moderation`; the community feed service mirrors that by reading `persistence.Communities`.
  This avoids registering `ICommunityStore` as a top-level service (it is a property of the persistence
  provider) and keeps the service's constructor optional (back-compat for hosts that don't want
  community-moderation filtering).
