# Phase 11 — Implementation gaps and usability fixes

> Extracted from the historical changelog. The changelog now keeps a short pointer to the detailed write-path notes.

## Overview

Phase 11 focused on the gaps exposed by user-journey testing: the read path was mostly working, but the write path and discovery path were still clumsy or incomplete. The work fell into three buckets:

1. discovery and bundle exposure
2. client convenience APIs (`FollowAsync`, `PostNoteAsync`)
3. server-side recording and federation of authored posts and follow decisions

## Slice 11.1 — Page-1 collection interop gap

- The server served page 1 as an `OrderedCollection` without a typed `next` property.
- The client stopped at page 1 because it treated page 1 as a terminal page.
- Fix: emit `next` via `ExtensionData` on page 1 and teach the client to read it from either a bare IRI string or a link-object form.

## Slice 11.2 — User-journey walkthroughs

- Walked all major capabilities end-to-end as a real user would.
- Captured usability friction in the new `PHASE_11_USER_JOURNEYS.md` register.
- Highlights included:
  - missing client-side follow API
  - missing client-side note-post API
  - discovery service not exposed on the bundle
  - hand-built `Create`/`Follow` activities being required to know SMTP-like routing details

## Slice 11.3 — Expose discovery on the bundle

- `IrisClientBundle` now exposes `Discovery` and `ResolveActorAsync(account, ct)`.
- `IrisClientBuilder.Build()` creates a default `WebFingerDiscoveryService` when no override is supplied.
- This closes the dead-end where users could not go from `@user@host` to an actor IRI through the public bundle surface.

## Slice 11.4 — Client follow API

- Added `IActivityPubClient.FollowAsync(Iri actorId, Iri targetId, ct)`.
- The `Follow` is built with deterministic IDs and delivered to the target actor’s inbox.
- The client also fixed a bug where an empty `Host` header could cause a malformed `host` signature component in signed requests.

## Slice 11.5 — Client post API

- Added `IActivityPubClient.PostNoteAsync(Iri actorId, string content, IEnumerable<Iri>? to = null, ct)`.
- The client builds a deterministic `Create` + embedded `Note` and sends it to the author’s own inbox.
- This was the first significant author-facing write API, and it intentionally routes through the author’s instance rather than directly to followers.

## Slice 11.6 — Surface local posts in the author’s outbox

- Added a dedicated `CreateActivityHandler`.
- When a `Create` is delivered to a local person, the server records it in the person’s outbox so the author sees it locally.
- When the recipient is a local community, the same create is recorded in local members’ outboxes for the community feed.
- This closes the server-side “post stored but not visible” hole.

## Slice 11.7 — Federate local posts to remote followers

- After recording the post locally, the server enumerates the author’s remote followers and delivers the same authored `Create` to their inboxes.
- This is server-side federation, not client fan-out; the client never enumerates the follower set.

## Slice 11.8 — RSA default key + public PEM

- Switched the default generated actor key to RSA-2048.
- Updated the public actor document to serve a PEM-based public key where necessary for real-world interop.
- Added `PKCS#1` RSA public-key import support so external servers publishing RSA public keys can be resolved.

## Slice 11.9 — Undo follow

- Added `UndoActivityHandler` to handle unfollow semantics.
- An `Undo` of a `Follow` removes the local follow edge in the follower’s context.
- This closed the gap where following existed but un-following did not.

## Slice 11.10 — Honor `manuallyApprovesFollowers`

- Added explicit support for the `manuallyApprovesFollowers` flag in actor metadata.
- When set for a local person, follow state is recorded but the automatic `Accept` is suppressed; a manual `Accept`/`Reject` remains the operator’s action.
- The public actor doc now advertises the property when it is enabled.

## Key outcome

The write path changed from “hand-built ActivityPub with hidden server-side assumptions” to a clear client/server split:

- the client builds a valid signed author-owned activity
- the server owns follower-state decisions and federation fan-out
- users can resolve actors, follow them, and post notes through the public APIs
