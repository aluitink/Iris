# 138.27 — Full-platform terminology & model audit

**Date:** 2026-09-15
**Slice:** [docs/plans/phase-138-lemmy-community-integration.md](../plans/phase-138-lemmy-community-integration.md) 138.27 (Stage H)
**Type:** Cross-platform consistency review (findings list; no code change)

## Objective

Compare how Iris models the same concept across **Mastodon-style peers**
(`Person` / `Note` / `Announce`-boost / `Like`) and **Lemmy-style peers**
(`Group` / `Page` / `Note`-comment / `Like`+`Dislike`-vote / community-`Announce`-relay),
and verify that **naming, extension terms, and UI language stay platform-agnostic** — no
Lemmy-only or Mastodon-only assumption leaks into shared components. Cross-check against
Pleroma / Misskey / PeerTube interop (Phase 79.3 / 81.x) to confirm the model generalizes
rather than special-casing two platforms.

## Method

Read the shared server-side render paths, the client-side readers, the client UI components,
and the core extension-term constants; for each concept, ask: *is the wire term, the store,
the reader, and the UI label driven by a platform-agnostic capability, or does it hard-code
"Lemmy" / "Mastodon"?*

## Findings

### F1 — Extension-term names are platform-agnostic. **PASS (no defect).**

Every `iris:` term in `IrisExtensionTerms` is named after the *concept*, not the platform:

| Concept | `iris:` term | Named after | Platform-agnostic? |
|---|---|---|---|
| per-requester like state | `isLiked` | the like edge | yes |
| per-requester boost state | `isShared` | the announce edge | yes |
| per-requester dislike state | `isDisliked` | the dislike edge | yes |
| like count | `likedCount` | the like reverse-index | yes |
| boost count | `sharedCount` | the announce reverse-index | yes |
| dislike count | `dislikedCount` | the dislike reverse-index | yes |
| reply count | `repliedCount` | the reply reverse-index | yes |
| net score | `score` | `likedCount − dislikedCount` | yes (see F2) |
| mod-removal actor | `removedBy` | the moderator action | yes |
| community NSFW | `communityNsfw` | the community flag | yes (see F3) |
| thread locked | `locked` | the thread state | yes |
| pinned/featured | `featured` | the pin state | yes |
| language | `language` | the content attribute | yes |
| mods-only posting | `postingRestrictedToMods` | the community flag | yes |

The docs on these terms *mention* Lemmy as the motivating source ("mirrors Lemmy's …"), but
the **names and wire keys are concept-based**, so a Mastodon/Pleroma/Misskey peer that happens
to emit the same semantic flag would be served the same term. No rename needed.

### F2 — `iris:score` is a derived net value, not a platform vote. **PASS (no defect).**

`iris:score = likedCount − dislikedCount` is computed on the server at render time from the two
platform-agnostic reverse-index counters. It is *not* read from any peer's REST API. A peer that
only supports `Like` (no `Dislike`) still gets `score = likedCount − 0 = likedCount` — the term
degrades gracefully. The term name is the generic "net score," not "upvotes minus downvotes."
This is the right generalization: any AP platform that emits `Like`/`Dislike` activities (Lemmy,
and hypothetically any voting-capable AS2 server) lands on the same `score` term.

### F3 — `iris:communityNsfw` mirrors a Lemmy flag but is semantically generic. **PASS (no defect).**

The term name `communityNsfw` is concept-based ("this community's content is NSFW"), not
"LemmySensitive." It is rendered on the community document only when the *source* community
advertises the flag. A Mastodon/Pleroma community that marks itself sensitive via its own
`nsfw` flag would, if wired, map to the same `iris:communityNsfw` term. The client reader
`GetCommunityNsfw` and the `RequiresCw` helper read the term, not the platform. No leak.

### F4 — Client reader names are platform-agnostic. **PASS (no defect).**

`IrisDocumentExtensions` exposes `GetLikedCount`, `GetSharedCount`, `GetRepliedCount`,
`GetIsLiked`, `GetIsShared`, `GetRemovedBy`, `GetCommunityNsfw`, `GetLocked`, `GetFeatured`,
`GetLanguage`, `GetPostingRestrictedToMods`, and `RequiresCw`. None of these take a "platform"
parameter or branch on `IsLemmy`. They read a term from the document; the term is what the server
chose to emit. The only platform-conditional reader is `IsLemmy` (see F5), which is the
*capability-detection* helper, not a shared render path.

### F5 — `IsLemmy` is a capability detector, not a model assumption. **PASS (no defect).**

`IrisDocumentExtensions.IsLemmy(document)` returns true when the document is a `Group` with an
`outbox` and *no* `iris:`-namespaced extension key. This is a **capability signal** ("this
community's posts come from its outbox; its members come from its followers") that happens to be
named after the most common server that exhibits it. It is used in exactly two capability-
resolution paths:

- `ResolveFeedIri` → `{actor}/outbox` when `IsLemmy`, else `iris:feed` / `{actor}/feed`.
- `ResolveMembersIri` → `{community}/followers` when `IsLemmy`, else `iris:feed` members.

The name is a slight misnomer (it really means "non-Iris Group with outbox/followers shape"), but
the *logic* is capability-based and would correctly handle any other AP server with the same
Group-outbox-follower shape (e.g. a Friendica group, a Pleroma community). **Follow-up (cosmetic,
S3):** consider renaming to `IsNonIrisGroupCommunity` (or `HasOutboxFollowersShape`) to make the
capability intent explicit and avoid the "Lemmy-only" reading. *This is a naming-only nit; the
behavior is already platform-agnostic.*

### F6 — `LemmyPostScore` / `LemmyVoteBar` / `LemmyVoteState` are the **only** platform-branded
client types. **FINDING (one follow-up defect).**

- `LemmyPostScore` (client) parses a *Lemmy REST* `post_view` document (`/api/v3/post?id=`).
  It is **inherently platform-specific** — no other AP server exposes a `/api/v3/post` endpoint.
  The client calls it only when `LemmyPostScore.TryParsePostIri` matches the `/post/{id}` IRI
  pattern. This is correct: it is a *Lemmy REST supplement*, not an ActivityPub model concept.
- `LemmyVoteBar` (UI) renders upvote/downvote/score using `LemmyPostScore` and the
  `Like`/`Dislike` AP activities. It is shown **only** when `IsLemmyPost` is true
  (`ObjectView.razor:197/267/611`); otherwise the platform-agnostic `EngagementBar` is shown.
- `IsLemmyPost` (`ObjectView.razor.cs:374`) detects a Lemmy post by IRI shape (`/post/{id}`),
  not by an AP term.

**The leak:** the *UI* branches on "is this a Lemmy IRI?" to choose between a vote bar and an
engagement bar. If a **Mastodon/Pleroma/Misskey peer** ever supported `Dislike` (downvote)
activities, Iris would *not* render a vote bar for it — it would fall through to the
`EngagementBar` (like/boost only), hiding the downvote affordance. The model is platform-agnostic
(`Like`/`Dislike` are stored and served regardless of source); only the **UI presentation gate**
is Lemmy-branded.

**Follow-up defect (S2, filed for a later UX slice):** generalize the vote-bar gate from
"IRI matches Lemmy `/post/{id}`" to "the object advertises a non-zero `dislikedCount` or the
peer advertises a `dislike` capability." That way any downvote-capable peer (Lemmy today,
others tomorrow) gets the vote bar, and the `LemmyVoteBar` component can be renamed to
`VoteBar` (the name is cosmetic; the component is already driven by AP `Like`/`Dislike`
activities, not Lemmy REST). The `LemmyPostScore` REST fetch stays Lemmy-specific (it is the
*score source*, not the model), but the *render gate* should be capability-driven.

### F7 — `EngagementBar` is platform-agnostic. **PASS (no defect).**

`EngagementBar` reads `likedCount` / `sharedCount` / `repliedCount` and renders like/boost/reply
counters. It carries no platform name and no platform branch. It is the default bar for all
non-Lemmy content. When F6's follow-up lands, `EngagementBar` and the (renamed) `VoteBar` can be
unified: show the downvote/score column when `dislikedCount > 0`, otherwise hide it.

### F8 — `TransformCreateForCrossPost` is a *server-side wire adaptation*, not a model assumption.
**PASS (no defect).**

When an Iris-authored `Note` is cross-posted to a non-Iris community (Lemmy), the server
transforms the embedded `Note` → `Page` (Lemmy's `Note` struct requires `inReplyTo`). This is the
correct, targeted wire adaptation: the *local* model stays `Note`; only the *outbound* document
to that specific peer is reshaped. The transform is gated on the recipient (cross-post target),
not on a global "if Lemmy" flag, so an Iris→Iris or Iris→Mastodon post is never transformed. No
leak into the shared model.

### F9 — `CommunityContentRecorder` handles `Note`/`Page`/`Article` symmetrically. **PASS (no
defect).**

`TagActivityForCommunity` tags `Note`, `Page`, and `Article` objects with the community IRI in
`attributedTo` so the feed filter includes them. The three types are handled by parallel
`TagNote`/`TagPage`/`TagArticle` methods with identical logic. No platform branch — the recorder
does not ask "is this Lemmy?" It tags whatever content object type arrived. Correct
generalization.

### F10 — Pleroma / Misskey / PeerTube cross-check. **PASS (model generalizes).**

- **Pleroma:** emits `Person`/`Note`/`Announce`/`Like` (same as Mastodon). Iris's Mastodon-style
  model (`Person`/`Note`/`Announce`-boost/`Like`) handles Pleroma without a branch. The
  `iris:` terms (`isLiked`, `isShared`, `likedCount`, `sharedCount`) are all concept-based and
  would serve a Pleroma peer identically.
- **Misskey:** emits `Person`/`Note`/`Announce`/`Like`; Misskey has no `Dislike` (no downvote).
  Iris's model handles it as a like-only peer (`dislikedCount = 0`, `score = likedCount`). The
  F6 vote-bar gate would *not* show a vote bar for Misskey (no downvotes) — which is **correct**
  (there is nothing to vote). No defect.
- **PeerTube:** emits `Person`/`Video` (a `CreativeWork` subtype, not `Note`/`Page`). Iris's
  `ObjectView` renders any `IObject` content; the `Note`/`Page`/`Article` tagging in
  `CommunityContentRecorder` would need a `Video` branch to tag PeerTube video re-posts, but
  PeerTube interop (Phase 81.x) is out of scope for this phase and the current code path
  (bare `Object` tagging) already covers it via the `else` branch. No defect for Phase 138.

### F11 — `Iri` helper extensions are platform-agnostic. **PASS (no defect).**

`InboxOf`, `OutboxOf`, `FollowingOf`, `FeedOf` (and `FollowedBy`, `SharedInboxOf`) are plain
IRI-segment appenders. They carry no platform logic. The *choice* of which helper to call is the
capability resolution (`ResolveFeedIri` / `ResolveMembersIri`), which is F5's concern (PASS).

### F12 — No platform-branded term in `ActivityPubExtensionNames` (bare terms). **PASS (no
defect).**

The bare ecosystem terms (`manuallyApprovesFollowers`, `manuallyApprovesMembers`, `publicKey`,
`privateKey`, `keyAlgorithm`) are all spec/de-facto-convention terms, not Iris-invented, and not
platform-branded. Correct to keep them bare (not under `iris:`) for cross-ecosystem interop.

## Summary

| # | Concept / component | Verdict | Follow-up? |
|---|---|---|---|
| F1 | `iris:` extension-term names | PASS | — |
| F2 | `iris:score` derived net value | PASS | — |
| F3 | `iris:communityNsfw` | PASS | — |
| F4 | Client reader names | PASS | — |
| F5 | `IsLemmy` capability detector | PASS (cosmetic) | S3 rename (cosmetic) |
| F6 | `LemmyPostScore` / `LemmyVoteBar` / `IsLemmyPost` | **FINDING** | **S2: generalize vote-bar gate to capability-driven; rename `LemmyVoteBar` → `VoteBar`** |
| F7 | `EngagementBar` | PASS | — |
| F8 | `TransformCreateForCrossPost` wire adaptation | PASS | — |
| F9 | `CommunityContentRecorder` symmetric tagging | PASS | — |
| F10 | Pleroma/Misskey/PeerTube cross-check | PASS | — |
| F11 | `Iri` helper extensions | PASS | — |
| F12 | Bare `ActivityPubExtensionNames` terms | PASS | — |

**Net:** the shared *model* (extension terms, stores, readers, wire adaptation) is
platform-agnostic and generalizes to Pleroma/Misskey. The single leak is a **UI presentation
gate** (F6): the vote bar is shown only for Lemmy-IRI-shaped posts, so a future
downvote-capable non-Lemmy peer would not get the downvote affordance. Filed as an S2 follow-up
for a later UX slice. One cosmetic S3 rename (F5: `IsLemmy` → a capability-shaped name) is noted
but not blocking.

## Follow-up defects filed

- **S2 (UX, later slice):** Generalize the vote-bar render gate in `ObjectView` from "IRI matches
  Lemmy `/post/{id}`" to "object advertises `dislikedCount > 0` or peer advertises a `dislike`
  capability." Rename `LemmyVoteBar` → `VoteBar`. Keep `LemmyPostScore` as the Lemmy-REST score
  source (inherently platform-specific) but make the *render decision* capability-driven.
- **S3 (cosmetic, later slice):** Rename `IrisDocumentExtensions.IsLemmy` to
  `IsNonIrisGroupCommunity` (or `HasOutboxFollowersShape`) to reflect its capability-based logic.
