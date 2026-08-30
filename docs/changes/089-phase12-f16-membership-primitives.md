# 089 — Phase 12: F-16 — community membership primitives (`Offer`/`Invite`/`Join`/`Leave`)

> 2026-08-30 · Phase 12 (Spec Conformance & Missing Features) · Gap closure (F-16)

## What was built

A `MembershipActivityHandler` now interprets the ActivityStreams community-membership
primitives for a local community (a `Group`):

- **`Offer`** / **`Invite`** — the activity's `object` (the invited actor) is **added** to the
  recipient community's member set (an invitation is accepted on receipt).
- **`Join`** — the activity's `object` (the joining actor) is **added** to the community's member
  set (the actor's declaration of membership).
- **`Leave`** — the activity's `object` (the leaving actor) is **removed** from the community's
  member set (the actor's declaration of departure).

A **person** recipient is a no-op (a person has no member set — membership is a community
relationship), and a **remote** recipient is not this instance's concern. `AddMember`/`RemoveMember`
are idempotent, so a re-delivered activity (at-least-once delivery, C-07) is safe to re-apply.

This complements F-09 (`Add`/`Remove`): a community that manages membership via the spec's
membership primitives (some servers) now syncs with Iris, not just one that uses `Follow` or
`Add`/`Remove`.

## The fix

### 1. `MembershipActivityHandler` (new, `src/Iris.Server/Inbox/MembershipActivityHandler.cs`)

Derives from the non-generic `IActivityHandler` (a single `ActivityHandlerBase<T>` cannot be
parameterized over four activity types) and is registered for the base `Activity` type. It
pattern-matches the four membership types at dispatch:

```csharp
switch (activity)
{
    case Leave leave:  return RemoveMemberAsync(delivery, leave, ct);
    case Join join:    return AddMemberAsync(delivery, join, ct);
    case Invite invite: return AddMemberAsync(delivery, invite, ct);
    case Offer offer:  return AddMemberAsync(delivery, offer, ct);
    default:           return Task.CompletedTask; // graceful no-op on a foreign activity
}
```

The `case Offer` is placed last because the library's `Invite` **derives from** `Offer` (both are
"invitation" activities), so an earlier `case Offer` would subsume `Invite`. A foreign activity
reaching this catch-all dispatch is a no-op, not a throw (throwing would turn a benign
dispatch-order artifact into a 500 on a validly-delivered activity).

### 2. `InboxProcessor` dispatch by specificity (`src/Iris.Server/Inbox/InboxProcessor.cs`)

The original `FindHandler` walked the activity's type hierarchy and returned the **first**
registered handler whose `HandledActivityType` matched a type in that hierarchy. With two handlers
both registered for `Activity` (the membership catch-all and the old `AddRemoveActivityHandler`), the
winner depended on registration order — and an `Invite` (hierarchy `Invite → Offer → Activity`, with
no dedicated `Invite`/`Offer` handler) landed on whichever `Activity` handler was registered first.

`FindHandler` now resolves the **most specific** matching handler: it measures each handler's
`HandledActivityType` by its distance (in base-type steps) from the activity's runtime type and
picks the closest. An exact type match (distance 0) is most specific; a handler registered for
`Activity` (the largest distance) catches any activity no more specific handler covers. A tie is
broken by registration order (the earlier-registered handler wins). Dispatch is therefore
independent of registration order for distinct activity types.

### 3. `AddRemoveActivityHandler` split into exact-type handlers

Because the membership catch-all and the old `AddRemoveActivityHandler` both claimed `Activity`, they
contended for the same activity under specificity dispatch. The fix splits it into two exact-type
handlers:

- **`AddActivityHandler`** (new) — `ActivityHandlerBase<Add>`, `HandledActivityType = typeof(Add)`.
- **`RemoveActivityHandler`** (new) — `ActivityHandlerBase<Remove>`, `HandledActivityType =
  typeof(Remove)`.

Each is a distance-0 match for its activity, so an `Add`/`Remove` reaches its exact handler and the
membership catch-all interprets `Offer`/`Invite`/`Join`/`Leave` (and any other activity no more
specific handler covers). The old `AddRemoveActivityHandler` is deleted.

## Tests

- **`MembershipActivityHandlerTests`** (new, 14 unit tests): `Offer`/`Invite`/`Join` add a member,
  multiple members, `Leave` removes one, non-member `Leave` is a no-op, idempotency (`Offer` an
  existing member, double `Leave`), person-recipient no-op (`Offer`/`Leave`), unknown-recipient
  no-op, no-object no-op (`Offer`/`Leave`), ctor null guard, dispatch null-delivery guard, and a
  foreign activity is a graceful no-op.
- **`AddActivityHandlerTests`** (new, 8) + **`RemoveActivityHandlerTests`** (new, 7): the split of the
  13 `AddRemoveActivityHandlerTests` (deleted), now exercising the exact-type `HandleAsync` surface
  plus a non-`Add`/non-`Remove` dispatch guard.
- **`MembershipFederationIntegrationTests`** (new, 5 end-to-end tests): a signed `Invite` adds the
  invited actor to the community's member set, a signed `Join` adds the joining actor, a signed
  `Leave` removes a member, an `Invite` to a local person is a no-op, and an `Invite` signed by an
  unresolvable-key actor is rejected (401).
- **`AddRemoveFederationIntegrationTests`** (updated doc comments to reference the new handler names).

## Files changed

- `src/Iris.Server/Inbox/MembershipActivityHandler.cs` — new, the membership-primitive handler.
- `src/Iris.Server/Inbox/AddActivityHandler.cs` — new, exact-type `Add` handler.
- `src/Iris.Server/Inbox/RemoveActivityHandler.cs` — new, exact-type `Remove` handler.
- `src/Iris.Server/Inbox/AddRemoveActivityHandler.cs` — deleted (split into the two handlers above).
- `src/Iris.Server/Inbox/InboxProcessor.cs` — `FindHandler` now dispatches by specificity.
- `src/Iris.Server/ActivityPubServerExtensions.cs` — registers `AddActivityHandler` /
  `RemoveActivityHandler` / `MembershipActivityHandler`.
- `tests/Iris.Server.Tests/Inbox/MembershipActivityHandlerTests.cs` — new.
- `tests/Iris.Server.Tests/Inbox/AddActivityHandlerTests.cs` / `RemoveActivityHandlerTests.cs` — new
  (split of the deleted `AddRemoveActivityHandlerTests.cs`).
- `tests/Iris.Server.Tests/MembershipFederationIntegrationTests.cs` — new.
- `tests/Iris.Server.Tests/AddRemoveFederationIntegrationTests.cs` — doc comments updated.

## Test count

901 → 935 (+34), 0 failures.
