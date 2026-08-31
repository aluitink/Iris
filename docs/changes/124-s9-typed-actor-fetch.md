# 124 — Sample Explorer: ActorDetail uses the typed `GetActorAsync` (closes a §3.1 gap)

**Status:** DONE — `ActorDetail` now fetches the actor via the dedicated **typed** `GetActorAsync`
instead of the generic `GetObjectAsync`, closing the last client-method gap in the second round's §3.1
list that had a clean UI home. 3 new in-process tests; full solution 0 warnings, 873/873 green.

## What

The second-round plan's §3.1 gap list named `GetActorAsync` ("Fetch an actor as a typed `Actor?`") as
"Actor docs always come through `GetObjectAsync`; the typed path is unused." This slice wires the typed
method into the `ActorDetail` page's actor load (`LoadByIriAsync`, the path both the handle-based Load
button and the S2 deep-link `/actor?iri=` auto-load route through).

The change is behavior-preserving on the happy path — `GetActorAsync` is `GetObjectAsync` cast to
`Actor` — but it now **surfaces the typed method's null contract**: when the loaded IRI is not an actor
(a note, an object), the page shows `Not an actor: {iri}` instead of rendering a non-actor as an actor.
This is the difference the typed method exists to provide.

```
ActorDetail.razor — LoadByIriAsync:
  ActorDoc = await client.GetObjectAsync(actorIri);          // before (generic)
  ────────────────────────────────────────────────────────────
  var actor = await client.GetActorAsync(actorIri);          // after (typed)
  if (actor is null) { Error = $"Not an actor: {actorIri.Value}"; return; }
  ActorDoc = actor;
```

`ActorDoc` stays `IObject?` (the `<ObjectView Item="ActorDoc" />` renders it as before); the typed
`Actor` is just the value assigned to it.

## Why not the Feed page's `GetFollowFeedAsync` (also a §3.1 "no UI" method)

Auditing the candidate methods, the **typed** `GetFollowFeedAsync` is the wrong tool for the `Feed` page:
it yields only the feed's items (the server serves page 1 as a collection document whose `next` pointer
is **not** on the flattened items), so it cannot carry the `next`-link the page's "Load more" button
resumes from. The `Feed` page therefore correctly keeps the **paged** `GetCollectionAsync` (which yields
`CollectionPage`s carrying `NextPage` / `IsLastPage`) for both the first page and "Load more" — that is
the S3 design and is preserved. (`GetFollowFeedAsync` is still exercised at the client level by the S3
test `FollowFeed_FollowerSeesFollowedActorsOutbox_NewestFirst`, and the home page already uses the
**typed** `GetCommunityFeedAsync` for its community card.) `GetActorAsync` was the §3.1 method with a
clean, non-paginated UI home — the actor-detail load.

## Tests (3 new, `S9TypedActorFetchTests`)

In-process against a real `Iris.Server` pipeline (mirrors the S3 host):

- `ActorDetail_TypedGetActor_ReturnsActorWithIdentity` — `GetActorAsync(alice)` returns the actor (not
  null) with `Id` = alice's IRI and `PreferredUsername` = `alice`.
- `ActorDetail_TypedGetActor_ReturnsNullForNonActor` — `GetActorAsync(note)` returns `null` (a note is a
  content object, not an actor) — the page's "not an actor" branch.
- `ActorDetail_TypedGetActor_MatchesGenericGetObject` — the typed and generic methods return the same
  actor document (the switch is behavior-preserving for `<ObjectView>`).

## Remaining §3.1 gaps (intentional, documented)

- **`GetFollowFeedAsync`** — see "Why not" above: the typed method can't carry the `next`-link the
  paginated Feed page needs; the page uses the paged method. The typed method is client-tested.
- **`DeliverAsync`** (raw signed activity to an inbox) — an escape hatch the high-level helpers already
  exercise internally; a dedicated raw-delivery screen is a follow-up, not this round.
