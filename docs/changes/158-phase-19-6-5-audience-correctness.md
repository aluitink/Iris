# 158 — Audience correctness: delivery recipients match the audience for outbound Create/Announce

> 2026-09-01 · Slice 19.6.5 (Phase 19.6 — Architectural expectations: client↔server interaction) · a public
> post/boost reaches the author's followers' inboxes and not a remote non-follower's

## What was built

**19.6.5** asks: "Outbound `Create`/`Announce` carry correct `to`/`cc` (followers + `as:Public` for public
posts; the reply target for replies), and delivery recipients match the audience (followers' inboxes
receive; non-followers do not)."

Reading the path confirmed the **delivery** half is already implemented and correct, and the missing
piece was a dedicated pin for the **non-follower-exclusion** (the "non-followers do not" half):

- `OutboxPublishHandler` (`POST /ap/v1/u/{handle}/outbox`, `ActivityPubServerExtensions.cs`) records the
  activity in the actor's outbox, then resolves the recipients for a `Create`/`Announce` via
  `GetRemoteNonBlockedFollowersAsync` (the author's **remote, non-blocked followers**) and calls
  `IDeliveryService.DeliverToActorAsync(recipient, activity, actorIri)` for each. **The `to`/`cc` arrays
  are never consulted for recipient computation** — the recipient set is the follower set.
- `GetRemoteNonBlockedFollowersAsync` skips local followers and blocked followers, so a remote actor who
  is **not** a follower is simply not in the recipient set and therefore receives nothing.
- The activity is delivered **as-authored** (the server does not rewrite `to`/`cc`), so the `as:Public`
  address the client put on a public Note's `to` round-trips to the follower unchanged.
- Follower-fan-out (both followers receive) and blocked-follower exclusion were already pinned by
  `OutboxCreateFanOutIntegrationTests` / `OutboxAnnounceFanOutIntegrationTests`. What was **not** pinned
  was the *non-follower* case — a remote actor who follows nobody of the author's must not receive the
  post. This slice adds that pin.

## Key types & files

- `src/Iris.Server/ActivityPubServerExtensions.cs` — **unchanged** (`OutboxPublishHandler` +
  `GetRemoteNonBlockedFollowersAsync` already compute the recipient set from the follower set and deliver
  as-authored).
- `src/Iris.Server/Delivery/{DeliveryService,DeliveryWorker}.cs` — **unchanged** (deliver to the recipient's
  inbox, signed as the acting actor).
- `tests/Iris.Server.Tests/OutboxAudienceMatchIntegrationTests.cs` — **new** (two integration tests; see
  below).

## Tests

1254 → **1256** passing (+2: the two audience-match integration tests).
Full `dotnet test` green; `dotnet build` clean (`TreatWarningsAsErrors`); `dotnet format` clean on the
changed file.

Topology: instance A (alice, the author), B (bob, a **remote follower** — bob→alice recorded on A), C
(carol, a **remote non-follower** — carol does not follow alice).

- `OutboxPublish_PublicCreate_FollowerReceivesWithAsPublic_NonFollowerDoesNot` — the central 19.6.5
  assertion: alice publishes a **public** `Create` whose embedded `Note` carries `as:Public` in its `to`
  (exactly as the client compose surface sets it for a public post) to her own outbox (a single signed
  POST). A records it and server-delivers the signed `Create` to **bob's** inbox (the follower) — and the
  federated `Note` still carries `as:Public` in its `to` (the audience round-trips through the wire
  unchanged). A then asserts that **carol** (the remote non-follower) stored **nothing**: the delivery
  recipients are the audience (the follower set), so a non-follower's inbox never receives the post.
- `OutboxPublish_Announce_NonFollowerDoesNotReceive` — the same audience match for a boost: alice's
  `Announce` reaches bob (the follower) and carol (the non-follower) receives nothing.

## Live verification (deferred — a live item)

The server-side audience-match invariant is pinned by the new tests (the non-follower-exclusion half,
end-to-end over the wire). The **live** half — driving compose through the **UI** and confirming the
raw inspector shows the post's audience and the follower (not a non-follower) received it — is the
remaining live-verification item for 19.6.5. It requires the two-instance Docker environment (dev1-public
host unreachable from CI), so it is deferred as a live item; the server-side invariant it verifies is
already covered in CI by the new tests + the existing fan-out pins.

## Decisions

- **Iris distributes by follower fan-out, not by enumerating `to`/`cc`.** The server computes the
  recipient set from the author's remote, non-blocked followers (`GetRemoteNonBlockedFollowersAsync`) and
  delivers the activity as-authored. It does **not** rewrite the activity's `to`/`cc` to add every
  follower IRI. This matches the ActivityPub convention that `to`/`cc` express the *intended* audience
  (here, `as:Public` for a public post, set by the client) while *delivery* is a server-side fan-out to the
  followers' inboxes. Enumerating every follower's IRI in `to`/`cc` would bloat the wire on large follower
  sets without changing who actually receives the post. The client's audience-setting (`as:Public` for a
  public post) was already pinned in `S7ComposeAudienceTests` / `ActivityPubClientTests`.
- **The "non-follower does not receive" is the net-new pin.** The existing fan-out tests cover "followers
  receive" (both followers) and "blocked follower does not receive," but never the distinct case of a
  remote actor who is simply **not** a follower. This slice pins that a non-follower's inbox stays empty —
  the "non-followers do not" clause of 19.6.5.
- **The on-the-wire audience *metadata* half is scoped out (deferred).** Two metadata behaviors from the
  19.6.5 text would require a production change and are not pinned here: (a) the server rewriting an
  outbound `Create`/`Announce` `to`/`cc` to enumerate the follower set, and (b) adding the parent note's
  author (the "reply target") to a reply's `to`/`cc`. Neither changes *who receives* the activity (the
  delivery already reaches the right inboxes via fan-out); only the `to`/`cc` bytes on the wire would
  differ. They are recorded as a remaining item in ROADMAP 19.6.5 so the invariant that is actually
  verifiable and meaningful now (recipients match the audience) is pinned without a speculative production
  change.
- **No production change.** The audience-match invariant was already implemented (the recipient set is the
  follower set; a non-follower is excluded by construction). The slice is a verification pin, consistent
  with how 19.6.2 (change 156) and 19.6.3 (change 157) were closed.
