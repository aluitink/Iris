# 065 — F-09 `Add` / `Remove` (collection-modification primitives) — closes F-09

> 2026-08-29 · Slice 12.20 · Phase 12 (Spec Conformance & Missing Features)

## What was built

Closes gap **F-09** (no `Add` / `Remove` handlers). `Add` and `Remove` are the ActivityStreams primitives
for modifying a collection. Their most common federation use is a server that represents a **community's
membership** as an `Add` of a member to the community's `followers` / `members` collection — in contrast to
the `Follow`-based membership Iris otherwise records. This slice interprets that case: a new
`AddRemoveActivityHandler` adds/removes the activity's `object` to/from the member set of a **local
community** that receives the activity in its inbox.

The handler is the single most-specific registered handler for both `Add` and `Remove`, so the
`InboxProcessor` dispatches both to it (the community catch-all, `CommunityInboxActivityHandler`, still
handles the remaining content activities — `Like`, `Announce` — delivered to a community inbox).

## Key types & files

| Type / file | Role |
|---|---|
| `src/Iris.Server/AddRemoveActivityHandler.cs` (NEW) | Derives from the **non-generic** `IActivityHandler` (a single `ActivityHandlerBase<TActivity>` cannot be parameterized over two activity types). `DispatchAsync` pattern-matches `Add` / `Remove`; `HandledActivityType` is `typeof(Activity)` (so the processor prefers it over the community catch-all for both `Add` and `Remove` — the most specific match). When the **recipient** is a local community, the activity's `object` is added to / removed from the community's member set via `ICommunityStore.AddMemberAsync` / `RemoveMemberAsync`. A person or remote recipient is a no-op; a malformed (no-resolvable-object) activity is a no-op. |
| `src/Iris.Server/ActivityPubServerExtensions.cs` | Registers `AddRemoveActivityHandler` in the `IActivityHandler` pipeline (before the `CommunityInboxActivityHandler` catch-all). |
| `tests/Iris.Server.Tests/AddRemoveActivityHandlerTests.cs` (NEW) | 13 unit tests: add a member, add multiple, idempotent re-add, remove a member, remove a non-member (no-op), person-recipient no-op, unknown-recipient no-op, remove-to-person no-op, malformed no-object `Add`/`Remove` no-ops, and the ctor / dispatch null + unsupported-type guards. |
| `tests/Iris.Server.Tests/AddRemoveFederationIntegrationTests.cs` (NEW) | 4 end-to-end tests (mirroring `MoveFederationIntegrationTests`): a signed `Add` delivered to a local community adds the actor as a member; a signed `Remove` removes an existing member; a signed `Add` to a local **person** is stored but a no-op; and an `Add` signed by an **unresolvable-key** actor is **rejected (401)**. |

## Tests

824 → **841** (+17):

- `tests/Iris.Server.Tests/AddRemoveActivityHandlerTests.cs` — 13 new. Each drives the real
  `AddRemoveActivityHandler` against an in-memory persistence provider. Coverage: `Add` adds the object as a
  community member; multiple `Add`s accumulate; a re-delivered `Add` is idempotent (no duplicate); `Remove`
  removes an existing member; a `Remove` of a non-member is a no-op (the existing member is untouched); an
  `Add`/`Remove` to a **person** is a no-op (a person's followers are owned by the follow lifecycle); an
  `Add` to an **unknown** recipient is a no-op; and an `Add`/`Remove` with **no resolvable object** is a
  no-op (malformed). The null-guard contract (ctor null persistence, dispatch null delivery, and a
  non-`Add`/`Remove` activity reaching the dispatch → `InvalidOperationException`) is asserted via a
  synchronous wrapper (the guards throw before the first `await`).
- `tests/Iris.Server.Tests/AddRemoveFederationIntegrationTests.cs` — 4 new end-to-end (instance A hosts the
  sender `alice`; instance B hosts the local community `iris` and the instance actor `bob`). Coverage: a
  signed `Add` delivered to B's community inbox is **validated** (B fetches `alice`'s actor document from A
  to resolve her key) and **stored**, and B's handler **adds** `alice` to the community's member set; a
  signed `Remove` (after seeding `alice` as a member) **removes** her; a signed `Add` delivered to a local
  **person's** inbox is stored but a **no-op** (no community membership, no follow edge); and an `Add` signed
  by an actor whose IRI is **not served** by A (no key to validate against) is **rejected (401)** — nothing
  is stored and no member is added.

## Decisions

- **Interpret `Add`/`Remove` only for a local-community recipient.** The collection being modified is owned
  by the recipient (the inbox the activity was delivered to). A community is the only collection Iris
  mutates via `Add`/`Remove` — a person's `followers` are maintained by the follow lifecycle
  (`FollowActivityHandler` records the edge; `AcceptActivityHandler` finalizes it; `UndoActivityHandler`
  removes it), so a person recipient is deliberately a no-op here. This keeps the two membership paths
  orthogonal and avoids double-recording.
- **One handler, two activity types, via the non-generic interface.** `ActivityHandlerBase<TActivity>` is
  single-typed, so a handler for both `Add` and `Remove` must derive from the non-generic `IActivityHandler`
  and pattern-match at dispatch. `HandledActivityType` is `typeof(Activity)`; because the
  `InboxProcessor` walks the activity's type hierarchy from most-specific to least, this handler (an exact
  match for `Add`/`Remove`) wins over the `CommunityInboxActivityHandler` (registered for `Activity`, the
  base). A non-`Add`/`Remove` activity reaching this dispatch is a programming error and throws.
- **Idempotent by construction.** `ICommunityStore.AddMemberAsync` / `RemoveMemberAsync` are idempotent, so
  an at-least-once re-delivery (C-07) of an `Add`/`Remove` is safe to re-apply without duplicate edges or
  errors.
- **The `object` is the member.** The item being added/removed is the activity's `object` (an
  `IObjectOrLink`, resolved with the shared `ResolveObjectIri` boundary helper — a `Link`'s `Href` or an
  embedded object's `Id`). The `actor` (the server performing the edit) and `target` / `instrument` (the
  collection) are not used to gate the interpretation — only the recipient must be a local community.

## Result

**F-09 is resolved.** A community's membership is now synchronized from the ActivityStreams `Add` /
`Remove` collection-modification primitives (in addition to the `Follow`-based path): a server that manages
a community's membership via `Add`/`Remove` will now update Iris's member set, and a `Remove` reverts it.
Wave 3 of the Phase 12 fix plan is now fully closed (F-07 moderation, F-06 relay, and F-09 `Add`/`Remove`).
