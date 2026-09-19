# 139.2-s5a/s5b — Object-document visibility gate for federated-in non-public content

**Status:** Done.
**Closes:** 139.2-s5 deferred surface #2 (from `1392-5b-federation-visibility-policy.md`): "a
non-recipient fetching a specific object IRI on B still gets the full object (the object-document
endpoint does not apply the visibility filter for cross-instance notes — a known limitation). This
is a larger design decision (it interacts with federation: remote instances legitimately need to
fetch non-public content to store/deliver it) and remains deferred."

Also records the decision on 139.2-s5 deferred surface #4 (`cc`-to-follower-set model): **DEFER**.

## The decision

**Surface #2 (object-document gate) — implemented.** The S5 visibility filter
(`VisibilityFilter.IsVisibleTo`) now gates the object-document endpoint for **all** non-tombstone
content objects — local AND federated-in. The original S5a carve-out ("remote objects are exempt:
the remote instance already has its own copy") is superseded by the S5b decision: the same privacy
boundary applies whether content originated locally or arrived by federation.

Before this change, the gate in `ObjectDocumentHandler` only applied when the author was a **local**
actor (`ILocalActorResolver.IsLocalActorAsync` was true). A federated-in non-public post (e.g., a DM
from alice on A to bob on B, stored on B) was served to **anyone** who fetched its object IRI on B
— even an anonymous visitor or an unrelated local actor. This was a privacy leak: the read-path
filter that hides the same content from B's public feed and global search did not apply to the
object-document endpoint.

After this change, the gate applies uniformly:

- A non-public post (no `as:Public` sentinel in `to`/`cc`, named recipients only) on the object-
  document endpoint returns **404** for any requester who is not a named recipient or the author.
- The **named local recipient** (e.g., bob on B, signed) gets **200** — the recipient can read
  content addressed to them, even when it arrived by federation.
- The **author** (signed, regardless of local/remote) gets **200**.
- **Public** posts (with `as:Public` in `to`/`cc`, or no named audience) are always served.
- **Tombstones** carry no audience information and are always served.
- A **404** (not 403) hides the object's existence — the standard ActivityPub privacy convention
  (Mastodon, Pleroma).

### Why this is safe (federation correctness)

The S5b decision (store intact, filter on read) ensures that:

- **Delivery is unaffected.** Inbound `CreateActivityHandler` records the embedded object
  unconditionally (no audience suppression on receipt). Federation delivery to the named local
  recipient's inbox is unchanged.
- **The named recipient can still read the content.** When bob (on B) fetches the object IRI
  (signed), the S5 filter keeps it (bob is a named recipient). The object-document endpoint is the
  read path for a signed request by the recipient.
- **Signed federation fetches by a recipient still succeed.** When a remote instance fetches a
  non-public object from the origin instance (e.g., to store a reply or to resolve a link), the
  request is signed as that remote instance's actor. If that actor is a named recipient (or the
  author), the S5 filter keeps the object. If it is not a recipient, the object is hidden — which is
  correct: a non-remote-recipient should not be able to fetch the object document from the origin.
- **The `?iri=` param (139.3-s6 F2) is unaffected.** The explicit IRI lookup (for stored foreign
  objects) still works; the S5 gate applies to the fetched object regardless of how the IRI was
  resolved (path-based or `?iri=`).

### What was changed

**Production source change** — `src/Iris.Server/ActivityPubServerExtensions.cs`:

- Removed the `ILocalActorResolver localActors` parameter from `ObjectDocumentHandler` (no longer
  needed — the gate no longer checks whether the author is local).
- Replaced the S5a gate (which checked `IsLocalActorAsync` before applying `VisibilityFilter`) with
  a uniform gate: `if (obj is IObject visObj && visObj is not Tombstone &&
  !VisibilityFilter.IsVisibleTo(visObj, requesterIri)) return Results.NotFound();`
- Updated the comment to reflect the S5b supersession (the gate now applies to all content, not
  just local).

**Test changes** — `tests/Iris.Server.Tests/CrossInstanceVisibilityIntegrationTests.cs`:

- Added a `carol` actor to instance B in the fixture (`SeedForFixture` + `BuildOptions`) to test the
  non-recipient local actor case.
- Added 3 new cross-instance integration tests pinning the federated-in object-document visibility
  behavior:
  - `FederatedDm_ObjectDocument_404_ForAnonymous` — a federated-in DM (to=[bob], from alice on A,
    stored on B) returns **404** for an anonymous request to B's object-document endpoint (via
    `?iri=` for the foreign IRI).
  - `FederatedDm_ObjectDocument_200_ForNamedRecipient` — the same DM returns **200** when fetched
    by bob (signed, named recipient) via B's object-document endpoint (via `?iri=`).
  - `FederatedDm_ObjectDocument_404_ForNonRecipientLocalActor` — the same DM returns **404** when
    fetched by carol (signed, local actor on B, **not** a named recipient) via B's object-document
    endpoint (via `?iri=`).

### Test counts

+3 new tests (the 3 federated-in object-document tests above).

Verification: full solution build clean (0 warnings / 0 errors, `TreatWarningsAsErrors`); full
`Iris.Server.Tests` suite green — **1,364 tests, 0 failed** (was 1,361, +3 new). The S5a local
tests (`ObjectDocumentVisibilityIntegrationTests`, 9 tests) still pass (the uniform gate handles the
local cases correctly: public posts pass, non-public posts are gated by recipient/author). The only
failures in the full suite are the 5 pre-existing `Iris.LiveInterop.Tests` Lemmy peering failures
(unrelated to Iris code — a separate track).

**Surface #4 (cc-to-follower-set model) — DEFER.** The current per-follower `cc` model is correct
and sufficient for privacy. The `FollowersCollection` optimization (using a computed follower set
instead of named `cc` recipients for followers-only posts) is a **scalability** optimization, not a
correctness or privacy fix. It is a substantial federation feature (receiving-side collection
expansion, membership verification, caching) and should be its own phase. The current model:

- Is correct: a followers-only post names its recipients via `cc` (the author's followers at the
  time of posting). The S5 filter keeps the post for those named recipients and hides it from
  non-recipients. This is the same behavior as the `to`-based DM case.
- Is consistent with the S5b decision: the read-path filter is the single privacy boundary, and it
  applies identically whether the content originated locally or arrived by federation.
- Is what the existing S5 tests pin: the local S5 surface tests and the S5b federation tests all
  exercise the per-follower `cc` model.

Deferring this keeps the current slice focused (object-document gate only) and avoids bundling a
large federation feature into a privacy fix. The `FollowersCollection` optimization can be taken up
as a standalone phase when scalability demands it.

## Decisions

- **Uniform S5 gate on the object-document endpoint.** The gate applies to all non-tombstone
  content objects, regardless of whether the author is local or remote. The S5b decision supersedes
  the original S5a "remote objects are exempt" carve-out.
- **404, not 403.** Hides the object's existence — the standard ActivityPub privacy convention.
- **Tombstones always served.** They carry no audience information.
- **Public posts always served.** `as:Public` sentinel in `to`/`cc` (or no named audience) means
  the post is public.
- **cc-to-follower-set model: DEFER.** The current per-follower `cc` model is correct; the
  `FollowersCollection` optimization is a scalability feature, not a privacy fix, and should be its
  own phase.

## Deferred / out of scope

- **`cc`-to-follower-set model (139.2-s5 deferred surface #4):** DEFER (see above). The current
  per-follower `cc` model is correct and sufficient; the `FollowersCollection` optimization is a
  scalability feature for a future phase.
- **No other deferred surfaces from 139.2-s5 remain.** Surfaces #1 (local object-document gate —
  done in S5a), #2 (federated-in object-document gate — done in this change), #3 (federation
  visibility policy — done in S5b), and #5 (follow-feed owner gate — done in 139.2-s5c) are all
  resolved.
