# Interop Conformance Matrix

> **Living reference doc.** The single "are we consistent?" artifact: **peer software × capability**,
> capturing what Iris *supports* and what has been *verified* per remote platform. Created by
> [Phase 138.28](../plans/phase-138-lemmy-community-integration.md); kept current as interop slices
> land. Companion to [COMPATIBILITY_MATRIX.md](COMPATIBILITY_MATRIX.md) (the Phase 9 *scenario test
> plan* — this matrix is the *verified-state* grid that supersedes its "expected" column).

## How to read this

- **Supported** = Iris has the code path (inbound handler + outbound client method) for the
  capability against that platform class. Every AP capability Iris ships is supported for *any*
  AP peer; the platform only determines whether the peer *emits* it.
- **Verified** = a slice exercised the capability against that platform (live or two-instance
  integration) and recorded evidence. Legend:
  - ✅ **verified** — exercised + evidence recorded (doc cited).
  - ⚠️ **partial** — works but with a documented limitation or asymmetry.
  - ❌ **N/A** — the platform does not emit the capability (nothing to verify).
  - ⬜ **open** — supported in code, not yet verified against this platform.
- The capability set is fixed: **follow · post · reply · like · dislike/downvote · boost/reblog ·
  edit/update · delete · community moderation · NSFW/sensitive**.

## The matrix

| Capability | Mastodon | Pleroma / Akkoma | Misskey | PeerTube | **Lemmy** |
|---|---|---|---|---|---|
| **Follow** | ✅ [81.1] [82.2] | ✅ [79.3] [81.2] | ✅ [81.2] | ✅ [79.3] | ✅ [138.4–138.6] |
| **Post (Create)** | ✅ [81.1] [82.2] | ✅ [79.3] [81.2] | ✅ [81.2] | ✅ [79.3] | ✅ [138.10–138.11] |
| **Reply** | ✅ [81.1] | ✅ [79.3] | ✅ [81.2] | ⬜ | ✅ [138.13–138.14] |
| **Like** | ✅ [81.1] [138.15/16] | ✅ [81.2] | ✅ [81.2] | ✅ [79.3] | ✅ [138.15/138.16] |
| **Dislike / downvote** | ❌ (no downvote) | ❌ (no downvote) | ❌ (no downvote) | ⚠️ de-facto `Dislike` | ✅ [138.17/138.18] |
| **Boost / reblog** | ✅ [81.1 `Announce`] | ✅ [81.2] | ✅ [81.2 renote] | ✅ [79.3] | ⚠️ relay-only, no user boost [138.19] |
| **Edit / Update** | ✅ `Update` handler | ✅ `Update` handler | ✅ `Update` handler | ⬜ | ✅ [138.22] |
| **Delete** | ✅ [81.3] | ✅ [81.3] | ✅ [81.3] | ✅ [81.3] | ⚠️ author vs mod-removal [138.23] |
| **Community moderation** | ⚠️ Block/Flag/Mute handlers | ⚠️ same | ⚠️ same | ⬜ | ✅ [138.25 `iris:locked`/`removedBy`] |
| **NSFW / sensitive** | ✅ [81.1 `sensitive`] | ⚠️ passthrough | ⚠️ [81.2 round-trip] | ⚠️ [79.3 blur] | ✅ [138.24–138.26] |

**Citation key:** `[81.1]` = [811-phase81-mastodon-interop-roundtrip](../changes/811-phase81-mastodon-interop-roundtrip.md);
`[81.2]` = [812-phase81-misskey-pleroma-interop-roundtrip](../changes/812-phase81-misskey-pleroma-interop-roundtrip.md);
`[81.3]` = [813-phase81-pagination-delete-tombstone-conformance](../changes/813-phase81-pagination-delete-tombstone-conformance.md);
`[79.3]` = [793-pleroma-peertube-interop](../changes/793-pleroma-peertube-interop.md);
`[82.2]` = [822-phase82-real-world-signed-delivery](../changes/822-phase82-real-world-signed-delivery.md);
`[138.x]` = [phase-138 plan](../plans/phase-138-lemmy-community-integration.md) slice x (Lemmy);
`[13827]` = [13827 cross-platform terminology audit](../changes/13827-phase138-cross-platform-terminology-audit.md).

## Per-platform notes

### Mastodon
- **Wire round-trip verified** (81.1): `Person`, `Note`, cross-instance reply (`inReplyTo`), media,
  hashtags, `sensitive`, outbox `Create`/`Announce`.
- **Outbound signed delivery accepted** by mastodon.social (82.2).
- **Live capability sweep mostly open** — [LIVE_INTEROP_TEST_PLAN](LIVE_INTEROP_TEST_PLAN.md) waypoints
  (follow/post/delete/moderation) are largely "pending." The strong evidence is fixture round-trip +
  signed delivery, not a full live sweep.
- **No `Dislike`** — Mastodon has no downvote; Iris's `iris:score` degrades to `likedCount`.
- **Boost** = `Announce` (verified via 81.1 outbox `Announce`).

### Pleroma / Akkoma
- **Inbound clean** (79.3): `conversationId`, custom emoji, `toot` survive.
- **Verified round-trip** (81.2) via a PieFed `Feed` community.
- **Known gap (81.2):** PieFed's `Feed` community type is **not** mapped to `Group` — a documented
  capability gap, not a defect.
- **No `Dislike`.**

### Misskey
- **Verified round-trip** (81.2): `Person`, `Note`, custom Emoji tags, `Announce` (renote).
- **No `Dislike`** (explicit — 13827 F10).
- **NSFW** round-trip verified (81.2 "not sensitive" leg).

### PeerTube
- **Media interop verified** (79.3): `Video` self-contained media; `GetSelfMediaIri` render fix.
- **`Dislike`** is de-facto used by PeerTube (138 plan) but not specifically verified.
- **`Video` is a `CreativeWork` subtype** (not `Note`/`Page`); the `CommunityContentRecorder` else-
  branch already tags it, but a dedicated `Video` branch is a follow-up (out of Phase 138 scope).

### Lemmy
- **The most thoroughly verified peer** (Phase 138, 138.4–138.26): follow (138.4–138.6), post
  (138.10–138.11, Note→`Page` transform), reply (138.13–138.14), like (138.15/138.16), dislike
  (138.17/138.18, `iris:score`), boost asymmetry (138.19 — `Announce` is community-relay-only, no
  user boost), edit (138.22), delete/mod-removal (138.23), metadata (138.24–138.26).
- **Full regression checklist:** [LEMMY_INTEROP_REGRESSION_CHECKLIST](LEMMY_INTEROP_REGRESSION_CHECKLIST.md)
  (68 items + known incompatibilities K1–K10).
- **Live leg expected-blocked** by Lemmy-side signature parse (K1) + WebFinger egress (K2) — not Iris
  defects.
- **`Dislike`** is the only capability with a sharp platform split: verified only by Lemmy here
  (PeerTube de-facto).

## Capability coverage (what Iris ships)

Every capability in the matrix has a **symmetric inbound handler + outbound client method**:

| Capability | Inbound handler (`src/Iris.Server/Inbox/`) | Outbound client (`IActivityPubClient`) |
|---|---|---|
| Follow | `FollowActivityHandler` (+`Accept`/`Reject`) | `FollowAsync` / `UndoFollowAsync` |
| Post | `CreateActivityHandler` | `PostNoteAsync` / `CreateCommunityAsync` |
| Reply | `CreateActivityHandler` (`inReplyTo`) | `PostReplyAsync` |
| Like | `LikeActivityHandler` | `LikeAsync` / `UnlikeAsync` |
| Dislike | `DislikeActivityHandler` | `DislikeAsync` / `UndislikeAsync` |
| Boost | `AnnounceActivityHandler` | `AnnounceAsync` / `UnannounceAsync` |
| Edit | `UpdateActivityHandler` | `UpdateNoteAsync` / `UpdateActorAsync` |
| Delete | `DeleteActivityHandler` (+`TombstoneInbound`) | `DeleteAsync` |
| Moderation | `Block`/`Flag`/`Mute` handlers | — (operator-side) |
| Membership | `MembershipActivityHandler` (`Join`/`Leave`/`Offer`/`Invite`) | — (operator-side) |

The inbound set also includes `MoveActivityHandler` (actor migration), `Add`/`Remove` (pin/featured),
and `IntransitiveActivityHandler` (no-op acks for `Read`/`View`/`Listen`/`Travel`/`Arrive`).

## Cross-platform consistency (from the 138.27 audit)

The 138.27 audit confirmed the shared **model** (iris: extension terms, stores, client readers, wire
adaptation, IRI helpers) is platform-agnostic and generalizes to Pleroma/Misskey/PeerTube. The single
leak is a **UI presentation gate**: the vote bar is shown only for Lemmy-IRI-shaped posts
(`/post/{id}`), so a future downvote-capable non-Lemmy peer would not get the downvote affordance.
Filed as an **S2 follow-up** (generalize the gate to `dislikedCount > 0` / a `dislike` capability;
rename `LemmyVoteBar` → `VoteBar`). See [13827 audit](../changes/13827-phase138-cross-platform-terminology-audit.md) F6.

## Maintenance

- **When to update:** any slice that verifies a capability against a new platform (or finds a new
  gap) updates the corresponding cell + the per-platform notes. Phase 139 (whole-platform E2E
  review) references this matrix as its "are we consistent" artifact.
- **Do not** scatter per-platform capability claims across change docs — point them here.
