# 091 — Phase 12: F-17 — intransitive activity handlers (`Read`/`View`/`Listen`/`Travel`/`Arrive`)

> 2026-08-30 · Phase 12 (Spec Conformance & Missing Features) · Gap closure (F-17)

## What was built

An `IntransitiveActivityHandler` now explicitly accepts the ActivityStreams intransitive-activity
family (`Read`, `View`, `Listen`, `Travel`, `Arrive`) — acknowledgment-of-receipt activities that a
server emits after consuming another actor's object. They are stored by the `InboxProcessor` and
interpreted as a no-op (they change no persistent Iris state). This makes the intent explicit (the
family is named) and guarantees the activities are accepted and stored rather than rejected or 500'd.

## The fix

Three changes in `src/Iris.Server`:

1. **New `IntransitiveActivityHandler`** (`Inbox/IntransitiveActivityHandler.cs`): a non-generic
   `IActivityHandler` registered for the base `Activity` type (the five types share no single concrete
   base — `Read`/`View`/`Listen` derive from `Activity`, `Travel`/`Arrive` from `IntransitiveActivity`).
   `DispatchAsync` pattern-matches the five types (all `Task.CompletedTask` — no state change) and
   **forwards** any non-intransitive activity to the injected `MembershipActivityHandler`.

2. **Registration order + factory** (`ActivityPubServerExtensions.cs`): the handler is registered
   **before** the `MembershipActivityHandler` (both for the base `Activity` type). The
   `InboxProcessor` breaks the base-`Activity` tie by registration order, so the
   `IntransitiveActivityHandler` wins the intransitive family. It is registered via a factory (not a
   direct `AddSingleton<IActivityHandler, IntransitiveActivityHandler>`) because it needs the
   `MembershipActivityHandler` injected; the `MembershipActivityHandler` is also registered as a
   concrete singleton so the factory resolves the same shared instance.

3. **Forwarding (the key correctness detail):** because the `IntransitiveActivityHandler` is registered
   first for the base `Activity` type, it sees *every* activity no more specific handler covers —
   including the `Offer`/`Invite`/`Join`/`Leave` membership primitives. If its `default` case no-op'd
   (as the `MembershipActivityHandler`'s does), the membership family would be swallowed and the
   community's member set would never be updated. Instead, the `default` case forwards to the
   `MembershipActivityHandler`, which interprets the membership primitives (and no-ops a genuinely
   foreign activity in its own default case).

## Tests

- **`IntransitiveActivityHandlerTests`** (new, 11 unit tests): each of the five activity types is
  accepted without throwing or changing state; a `Read` to a local community makes no membership/like
  change; a non-intransitive activity (`Offer`) is forwarded to the `MembershipActivityHandler` (which
  adds the member); a foreign activity (`Follow`) is a graceful no-op; `HandledActivityType` is
  `Activity`; null delivery/activity guards throw.
- **`IntransitiveFederationIntegrationTests`** (new, 3 end-to-end tests): a signed `Read` delivered to
  a local actor is accepted (202), stored, and makes no state change; a signed `Travel` (the
  `IntransitiveActivity` derivative) delivered to a local community is accepted, stored, and makes no
  state change; a `Read` signed by an unresolvable-key actor is rejected (401).

## Files changed

- `src/Iris.Server/Inbox/IntransitiveActivityHandler.cs` — new.
- `src/Iris.Server/ActivityPubServerExtensions.cs` — registration (factory + order) +
  `MembershipActivityHandler` concrete singleton.
- `tests/Iris.Server.Tests/Inbox/IntransitiveActivityHandlerTests.cs` — new.
- `tests/Iris.Server.Tests/IntransitiveFederationIntegrationTests.cs` — new.

## Decisions

- **Forward rather than swallow.** The `IntransitiveActivityHandler` is registered first for the base
  `Activity` type, so it must forward non-intransitive activities to the `MembershipActivityHandler`.
  An alternative (registering it *after* the `MembershipActivityHandler`) would let the membership
  handler win the tie-break and swallow the intransitive family in its default case — defeating the
  purpose of a dedicated handler. Forwarding keeps the intransitive handler as the explicit first
  stop while preserving the membership behavior.

## Test count

938 → 952 (+14), 0 failures.
