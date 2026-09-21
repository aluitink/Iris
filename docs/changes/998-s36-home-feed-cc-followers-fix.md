# 998: S36 Home Feed Drops Top-Level Posts with cc=[followers]

## Summary

Fixed S36 (S2-severity): the home feed (`/home`) returned empty for a local user — their own outbox posts and followed actors' posts were missing, while the community feed, outbox, and notifications all worked.

## Root Cause

`FeedService.IsFollowReply` (the thread-depth filter, applied at the default `threadDepth=null`) used a two-tier heuristic to decide whether a feed item is a directed reply (and should be dropped from a top-level home timeline):

1. **Primary:** the content object has an `inReplyTo` field → it's a reply.
2. **Fallback (when `inReplyTo` is absent):** `contentObj.GetAudienceIris().Count > 0` → a non-public audience indicates a directed reply.

The bug was in the fallback. `GetAudienceIris()` (in `IriExtensions.cs`) collects entries from **both** the `to` and `cc` fields (minus the public sentinel `https://www.w3.org/ns/activitystreams#Public`). Every top-level post Iris writes carries the standard ActivityPub public-post shape:

```json
{ "to": ["https://www.w3.org/ns/activitystreams#Public"], "cc": ["<actor>/followers"] }
```

Because `cc=[followers]` is a non-public IRI, `GetAudienceIris().Count` was always `1` for every top-level post — so `IsFollowReply` returned `true` and the post was dropped from the home timeline as if it were a directed reply.

**Why in-process repros missed it:** the earlier S36 regression tests seeded posts with no `to`/`cc` at all (audience count 0 → kept). The live wire shape (`to=Public, cc=followers`) is what `OutboxPublishHandler` + `RewriteOutboundAudienceAsync` actually produce, and that's the shape that triggered the bug.

## Fix

`IsFollowReply`'s fallback now inspects **only the `to` field** (not `cc`):

- A `to` of just the public sentinel (or an absent `to`) → top-level post, not a reply → kept.
- A `to` that names a specific actor IRI (non-public) → directed reply → filtered.

This matches the ActivityPub reply convention: a reply addresses its parent's author in `to`; a top-level post's `to` is the public sentinel. A top-level post's `cc=[followers]` is a public carbon-copy, not a directed recipient.

The change is localized to `IsFollowReply` only. `GetAudienceIris()` is still used elsewhere (visibility filtering, audience rendering) and is unchanged.

## Tests

Two new regression tests in `FeedServiceTests.cs`:

- `S36_OwnPostsWithCcFollowers_SurfaceInHomeFeed_AmongActorDocNoise` — 5 own content Creates with the live wire shape (`to=Public, cc=followers`) + 41 actor-doc noise items + a Group Create + a follow Create. All 5 content Creates must surface in the home feed. (Before the fix, only 0 of 5 surfaced.)
- `S36_FollowReplyWithToNamed_IsStillFiltered_TopLevelWithCcFollowers_IsIncluded` — a directed reply (`to=[named-actor]`, no `inReplyTo`) is still filtered; a top-level post (`to=Public, cc=followers`) is still included. Confirms the fix doesn't over-correct.

All 69 FeedService tests pass. Full `Iris.Server.Tests` suite: 1436 pass.

## Files Changed

- `src/Iris.Server/Services/FeedService.cs` — `IsFollowReply` fallback: inspect only `to` (not `cc`).
- `tests/Iris.Server.Tests/Services/FeedServiceTests.cs` — 2 new regression tests.

## Decision

Inspecting only `to` (not `cc`) for the directed-reply heuristic is the correct ActivityPub interpretation. The `cc` field on a public post is a carbon-copy to followers (so they see it in their home timeline), not a directed recipient. The `to` field on a reply names the parent's author (the directed recipient). This distinction is what the ActivityPub spec intends, and it's what makes the home feed work with the standard public-post shape.
