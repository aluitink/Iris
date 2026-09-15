# 138.10 — Post-to-Lemmy-community delivery path (the cross-post leg)

**Phase 138 (Lemmy community integration), slice 138.10.** Implements the delivery path decided in [decision 058](../decisions/058-outbound-post-to-peered-community-shape.md): an Iris-authored post that should land inside a peered non-Iris community's post list is delivered as an **explicit cross-post** — the target community is named in the `Create`'s `to` audience, and the `Create` is delivered to that community's inbox, signed as the authoring local actor.

## What was built

A new outbound delivery leg in `OutboxPublishHandler`'s `Create` branch (src/Iris.Server/ActivityPubServerExtensions.cs):

- After the existing follower fan-out, reply-parent-author delivery, and relay fan-out, the server now delivers the `Create` to every **remote recipient explicitly addressed in the `Create`'s `to` audience** — the cross-post targets.
- New helper `GetCrossPostTargetsAsync` computes those targets: it reads the composed `to` audience (which `RewriteOutboundAudienceAsync` preserves — that rewrite only appends followers to `cc` and the reply parent author to `to`), and returns the subset that is (a) **remote** (not a local actor — a local recipient is on this instance, no cross-instance hop), (b) **not the `as:Public` sentinel**, (c) **not already fanned out** to a follower (no duplicate delivery), and (d) **not blocked** (the trust boundary — `IsBlockedAsync(target, author)`).
- The leg is **best-effort**: a delivery failure for one target does not fail the publish (the follower fan-out and the local record already succeeded), mirroring the existing fan-out conventions.

The delivery itself reuses `IDeliveryService.DeliverToActorAsync` (resolves the recipient's `sharedInbox`/`inbox`, signs as the acting local actor) — no new delivery machinery. The same once-minted activity id is reused for the cross-post leg (decision 055).

## Why the `to` audience is the signal

Per decision 058, the cross-post target is an **explicit, client-supplied** address: the author names the target community's `Group` IRI in the `Create`'s `to` audience. The server does not infer "this community follows a Lemmy community, so push every post there" (a follow is a pull — decision 036 / Phase 89.1). This keeps follow state and posting intent separate and matches the Fediverse cross-post convention (a Lemmy/Mastodon client cross-posts by addressing the target community).

## Key types (no new public API)

- `GetCrossPostTargetsAsync` (private, src/Iris.Server/ActivityPubServerExtensions.cs) — the cross-post target computation.
- Reuses `IDeliveryService.DeliverToActorAsync`, `ILocalActorResolver.IsLocalActorAsync`, `IModerationStore.IsBlockedAsync`, `IriExtensions.ResolveObjectIri` / `IsPublicAudience`, and the existing `AudienceIriComparer`.

## Tests

2 integration tests in `tests/Iris.Server.Tests/CrossPostToRemoteCommunityIntegrationTests.cs` (two-instance: A hosts the cross-post-target community with member bob — the Lemmy stand-in; B hosts alice the author):

1. `CrossPostToRemoteCommunity_DeliversCreateToCommunityInbox_AndLandsThere` — alice (B) cross-posts by addressing A's community in `to`; the `Create` federates B→A (signed, validated), A stores the cross-posted `Note`, and it lands in the community's local member's (bob's) outbox (via `CommunityContentRecorder`) so it surfaces in the community feed.
2. `PlainPostNotAddressedToRemoteCommunity_DoesNotReachIt` — negative control: a plain public post (no cross-post target in `to`) does **not** reach A's community, proving the `to`-addressing (decision 058) is the delivery mechanism, not an incidental fan-out path.

Both pass (Iris.Server.Tests 1215 passed, +2, 16 skipped, 0 failed). No new dependencies.

## Live acceptance (deferred)

The hermetic tests prove the delivery path (Iris→Iris cross-post). The **live Lemmy acceptance** — the post appearing in Lemmy's *own* post listing (`/api/v3/post/list?community_id=...`) attributed to the Iris actor — is the real 138.10 check against the local Lemmy container, and is exercised live alongside the 138.11 fidelity check (whether Lemmy requires a `Page` rather than a `Note`).
