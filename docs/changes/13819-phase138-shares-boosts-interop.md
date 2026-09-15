# 13819 — Shares/Boosts Interop (Platform-Asymmetry Documentation) (138.19)

## Summary

Verified that Iris correctly handles the platform asymmetry between Lemmy (community-relay-only
`Announce`) and Iris/Mastodon (user-initiated `Announce`/boost). No production code changes were
required — the existing `AnnounceActivityHandler` relay-unwrap logic (138.12) already treats the
community-relay `Announce` as a fan-out envelope (not a user share), attributes the content to the
underlying `Create`'s actor (the original Lemmy member, not the relaying community), and the UI
renders a `LemmyVoteBar` (upvote/downvote/score) instead of the `EngagementBar` (which carries the
Boost button) for Lemmy-sourced content.

## Findings

### 1. Lemmy has no user-initiated boost concept

Lemmy's `Announce` is community-relay-only: the community's actor issues an `Announce` whose `object`
is an embedded `Create` (which embeds the `Page`/`Note`). There is no user-facing "boost" button in
Lemmy's UI — the relay is an automatic server-side fan-out when a member posts to a community.

### 2. Iris's Announce-unwrap logic treats the relay as a fan-out envelope

`AnnounceActivityHandler.HandleAsync` (src/Iris.Server/Inbox/AnnounceActivityHandler.cs:129-139)
detects the relay pattern by checking if the `Announce`'s object deserializes as a `Create` (an
embedded activity, not a bare object IRI). When detected, `HandleEmbeddedCreateAsync`
(lines 222-257) stores the embedded `Page`/`Note` in the object store and records the `Create` in the
community's local members' outboxes via `CommunityContentRecorder`. The `Announce` envelope itself is
separately recorded in the community's outbox (line 143-145) and the announcer→object announce edge is
recorded (line 152-154), but the surfaced content is attributed to the original poster.

A plain `Announce` (bare-IRI object) does NOT trigger the relay-unwrap path — it falls through to the
standard boost path (record in outbox + announce edge + follower fan-out). This is verified by the
existing test `PlainAnnounce_ObjectIsBareIri_DoesNotStoreInObjectStore`.

### 3. The merge path attributes the content to the `Create`'s author

`CommunityContentRecorder.RecordToMembersAsync` (src/Iris.Server/Inbox/CommunityContentRecorder.cs:39-70)
creates a community-tagged copy of the activity that **preserves `Create.Actor`** (the original
author). The community IRI is only appended to the embedded object's `AttributedTo` (which drives feed
inclusion and the "community" association, not authorship). When the UI renders a `Create` feed item,
the displayed author is `Create.Actor` (the original Lemmy member).

### 4. The UI does not offer a "boost" control on Lemmy-sourced content

`ObjectView.razor` (apps/Iris.Web.Client/Components/ObjectView.razor:195-209, 267-285, 611-622)
conditionally renders a `LemmyVoteBar` (upvote/downvote/score only — no Boost button) instead of the
`EngagementBar` (which carries the Boost button) when `IsLemmyPost` is true. `IsLemmyPost`
(ObjectView.razor.cs:374-391) returns true when the content object's IRI matches the Lemmy post
pattern (`.../post/{id}`) via `LemmyPostScore.TryParsePostIri`.

## Test

1 new integration test added to `LemmyCommunityRelayIntegrationTests`:

- **`LemmyCommunityRelay_ContentAttributedToOriginalAuthor_NotRelayingCommunity`** — a
  Lemmy-style `Announce(Create(Page))` delivered to the community's inbox; asserts that the `Create`
  in the member's outbox has `Actor` = the original Lemmy member (not the relaying community).

All 4 tests in the class pass (3 existing + 1 new). Full suite: 1243 passed, 1 known flake.

## Files

- `tests/Iris.Server.Tests/LemmyCommunityRelayIntegrationTests.cs` (modified) — 1 new test
- `docs/plans/phase-138-lemmy-community-integration.md` (updated) — 138.19 marked `[x]`
