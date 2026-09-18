# 139.3 Scenario 11 — Retention / right-to-deletion

**Status:** PASS with one logged nuance. Deleting a local account produces a **consistent post-deletion
state** that matches the documented retention model (Phase 136.19) on every read path that the model
says must hold. The model's tolerated "bounded stale artifacts" (retained activities, box items, and
edges) are confirmed to survive — which is by design — and the one place the model's "not user-visible"
wording is worth a closer look is logged below as a finding.

## Bar

> Delete an account (Phase 53.1); confirm the account's content is handled per the documented retention
> model (Phase 136.19) — no orphaned references, no broken links elsewhere. Consistent post-deletion
> state. **DB check + UI check.**

## What the test covers

`tests/Iris.Server.Data.Tests/AccountDeletionRetentionTests.cs` (1 test, on the shared
`postgres:16-alpine` `PostgresFixture`). The deletion performed here mirrors
`AccountDeletionService.DeleteAsync` (which lives in `Iris.Web` and cannot be referenced from this
project) **exactly**: list the actor's content objects, tombstone each one, remove the actor from the
actor store, and delete the account row.

**Seed:** two local actors (alice — the deletion subject — and bob), a community, alice's two notes
(content objects) + two `Create` activities + two outbox box items, and three edges that reference
alice (bob→alice `Follow`, alice-in-community `CommunityMember`, bob→note1 `Like`), plus alice's
account row.

**Assertions — the parts that MUST hold for a coherent post-deletion state:**

| Check | Result |
|---|---|
| The account row is deleted (`DeleteAsync` → `true`) | ✅ |
| The actor no longer resolves (`TryGetActorAsync` → `false`) | ✅ |
| Both of alice's content objects serve as `Tombstone` (IRIs still resolve as "deleted") | ✅ |
| The account row is gone by id **and** by username (`FindByIdAsync` / `FindByUsernameAsync` → `null`) | ✅ |
| Object **search** excludes the tombstoned content, including a unique needle (`CountSearchMatchesAsync` → 0; `SearchObjectsAsync` → empty) | ✅ |
| Actor **search** does not return the deleted actor (`SearchActorsAsync`, `localOnly: true` → empty) | ✅ |
| The other local actor (bob) is untouched and still resolves | ✅ |

**Assertions — what survives (the model's tolerated "bounded stale artifacts"), as evidence for the
"no broken links elsewhere" bar:**

| Surviving artifact | Confirmed |
|---|---|
| Alice's outbox still returns both `Create` activities (BoxItems + Activities survive — append-only, no pruning) | ✅ |
| The outbox's `Create` activities still embed the **original** notes (type `Note`), **not** `Tombstone` | ✅ |
| The `Follow` edge (bob→alice) survives: `IsFollowingAsync(bob, alice)` → `true`; `GetFollowersAsync(alice)` contains bob | ✅ |
| The `CommunityMember` edge survives: `GetMembersAsync(community)` contains alice | ✅ |
| The `Like` edge (bob→note1, a tombstoned object) survives: `HasLikedAsync` → `true`; `GetLikersAsync(note1)` contains bob | ✅ |

## Findings

**F1 — the consistent half of the retention model holds (PASS).** Every read path the Phase 136.19
model says must stay coherent after an account deletion does: the actor 404s, the content IRIs serve
`Tombstone` documents (not 404s, not live content), object search excludes the tombstoned content,
actor search omits the deleted actor, the account row is gone, and other actors are untouched. This is
the "right-to-deletion" guarantee: the user's content is no longer live, no longer searchable, the
profile is gone, and the account can no longer log in.

**F2 (nuance, logged — no code change this turn) — the model's "not user-visible" wording is
path-specific.** Phase 136.19 states the retained artifacts (activities, like/announce edges) are "a
bounded stale artifact, **not user-visible** — the object-document endpoint skips tombstones." This
test confirms the object-document half of that claim: the outbox's embedded notes are the *original*
notes, but the **object-document endpoint** is the path that serves the `Tombstone` (the embedded copy
in the activity is not what a reader clicks through to).

**Correction (139.3-F2 review): the feed paths DO consult the actor store.** The earlier note that the
feed "reads purely from the surviving rows with no join to Actors" is inaccurate. Both
`FeedService.BuildFeedAsync` and `CommunityFeedService.ReadOutboxAsync` route each follow/member
through `ILocalActorResolver.IsLocalActorAsync`, which consults the actor store (and, when an instance
base is configured, requires the IRI to be hosted locally). A **deleted local** follow or community
member therefore resolves as *not local* → its outbox is read over the wire → the deleted actor 404s →
it contributes nothing to the feed. So a deleted **local** actor's post content does **not** render in
a follower's home feed or a community feed in any host that configures a local-actor resolver
(production does). The feed gap is thus already mitigated; the only residual is the legacy
no-resolver path (every contributor read from the local store), which has no instance base with which
to distinguish a deleted local from a remote actor and so preserves the legacy render.

The genuinely-visible remainder is the **edge-list and like-counter surfaces**, which read the
`Edges` rows directly (no actor-store join):

- the deleted actor can still appear in other actors' **followers/following lists** and in a
  community's **member list**;
- the deleted actor's posts can still count toward **like counters** on surviving objects.

None of this is a *broken link* in the failure sense — every IRI still resolves (the actor 404s
gracefully, the objects serve `Tombstone`, the edges resolve to a known IRI). It is the model's
documented, intentional "retain, don't sweep" behavior surfacing through the edge/counter paths. It is
logged here (rather than silently reconciled) because the "not user-visible" phrasing is accurate for
the object-document and feed paths but not strictly for the edge-list / like-counter paths; a future
slice that wants to hide a deleted actor from those lists would filter edges that reference a removed
actor at read time (a read-path filter, not a DB sweep — consistent with the retention model). That is
a product decision, not a defect, so it is logged as a follow-up.

## Decision recorded

Per the scenario's instruction ("any gap between the documented retention/lifecycle model and actual
behavior is logged as a finding, not silently reconciled by rewriting the model"), this review **does
not change** the deletion behavior or the 136.19 retention model. It verifies the consistent half,
demonstrates the tolerated surviving artifacts, and logs the one wording nuance (F2) for a future
edge-list / like-counter rendering decision. No production source change this turn.

A follow-up review (139.3-F2) corrected the F2 premise: the feed paths **do** consult the actor store
(via `ILocalActorResolver.IsLocalActorAsync`), so a deleted **local** follow/member is already excluded
from the feed in any resolver-configured host. The genuine residual is the edge-list / like-counter
surfaces only. No feed-side code change was warranted.

## Evidence

```
dotnet test tests/Iris.Server.Data.Tests/Iris.Server.Data.Tests.csproj \
  --filter "FullyQualifiedName~AccountDeletionRetentionTests"
Passed!  - Failed: 0, Passed: 1, Skipped: 0, Total: 1
```

Full solution build: 0 warnings / 0 errors. Full `Category!=Slow` suite: 0 failed across all 11 test
projects (Data project now 16 tests).
