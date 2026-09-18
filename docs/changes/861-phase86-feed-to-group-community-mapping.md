# 86.1 — Community-type interop: map the PieFed `Feed` wire type to `Group`

**Phase 86.1** · Community-type interop (Feed → Group mapping)

## What was built

PieFed (a Pleroma fork) emits its community/group actor as `"type":"Feed"` where standard
ActivityPub and standard Pleroma emit `"type":"Group"`. Before this change, a `Feed`-typed
document deserialized to a generic `Object` (the library's `ObjectTypes.Types` registry has no
`"Feed"` entry), so **every** `as Group` / `is Group` cast site failed to recognize it as a
community:

- `FileBackedCommunityStore.TryGetCommunityAsync` (`as Group`) — a stored `Feed` community was
  reported "not found".
- `EfCommunityStore.TryGetCommunityAsync` / `GetAllCommunityIrisAsync` (the `Type == "Group"`
  DB filter) — a `Feed` row was invisible.
- The community `Update` merge (`ActivityPubServerExtensions.HandleCommunityUpdateAsync`,
  `UpdateActivityHandler.HandleActorUpdateAsync`) and the community-creation write path — a
  `Feed` update/create was silently dropped.

Result: PieFed (and any other Pleroma-fork using `Feed`) communities were not recognized as
communities by Iris, even though standard Pleroma `Group` communities worked (verified in
Phase 78.3).

The fix registers `"Feed" → typeof(Group)` in the shared `ObjectTypes.Types` registry so the
polymorphic `ObjectOrLinkConverter` materializes a `Feed` actor as a `Group`. This fixes **all**
cast sites at once (no per-site changes), and answers the open question the 81.2 change doc
raised: *"how to detect `Feed` vs `Group` vs other fork-specific community terms"* — one
centralized registration point.

## Key changes

| File | Change |
|------|--------|
| `src/Iris.Core/ActivityJson.cs` | Added `public const string FeedCommunityType = "Feed"`. In `CreateOptions()`, registered `ObjectTypes.Types["Feed"] = typeof(Group)` (idempotent-guard pattern, mirroring the existing `MuteActivity` registration). This is the single choke-point for all ActivityStreams (de)serialization in Iris. |
| `src/Iris.Server.Data/Stores/EfCommunityStore.cs` | `TryGetCommunityAsync` + `GetAllCommunityIrisAsync` now match `Type == "Group" || Type == ActivityJson.FeedCommunityType`. `PutCommunityAsync` now stores the document's **actual** wire type via a new `CommunityWireType(Group)` helper (`"Feed"` when the document's `Type` is `Feed`, else `"Group"`), so a re-read re-emits the document exactly as authored. |
| `tests/Iris.Core.Tests/PleromaFamilyInteropRoundTripTests.cs` | Updated: the test now **asserts** the `Feed` → `Group` mapping (`Assert.IsAssignableFrom<Group>`) instead of asserting it does *not* happen. The round-trip assertions are unchanged and still hold (see below). |
| `tests/Iris.Server.Tests/Persistance/FeedCommunityMappingTests.cs` | **New** (5 integration tests): `Feed` deserializes to `Group`; `Feed` round-trips preserving the `Feed` wire type; `FileBackedCommunityStore` round-trips a `Feed` document as a `Group`; a `Feed` community survives a simulated restart; a `Feed` community is listed by `GetAllCommunityIrisAsync`. |

## Decision: where to register the mapping (and why the wire type is preserved)

The mapping is registered in `Iris.Core` (`ActivityJson.CreateOptions()`), **not** in
`Iris.Server` (`AddActivityPubServer`), for two reasons:

1. **Semantics.** The fact that `"Feed"` is a community actor type is a wire-format fact about the
   ActivityStreams ecosystem, not a server concern. The client also deserializes remote objects
   (e.g. fetching a remote PieFed community's document) and should classify `Feed` as `Group`
   there too.
2. **Centralization.** `ActivityJson` is the single entry point for all ActivityStreams
   (de)serialization in Iris. Registering there guarantees every consumer (server stores,
   handlers, client fetches, the EF + file-backed stores) sees the mapping, with no risk of a
   consumer that bypasses `AddActivityPubServer` missing it.

**Wire-type preservation (verified empirically):** a surprising and important property of the
library is that a `Group` instance **preserves its original `Type` value** through
deserialize→serialize. A `Feed` document deserializes to a `Group` whose `Type` is still
`["Feed"]`, and re-serialization emits `"type":"Feed"` — *not* `"type":"Group"`. This means:

- A re-emitted PieFed community stays a `Feed` (no wire-type loss, no surprise for other
  instances).
- The existing 81.2 round-trip test's `Assert.Equal("Feed", …type…)` assertion **still holds** —
  only the "is it a `Group`?" assertion needed updating.
- The EF store's `PutCommunityAsync` stores the actual wire type (`Feed` or `Group`) so the DB
  row matches the document, and the read filter matches both.

## Alternatives considered

- **Per-cast-site `is Feed` checks** — rejected: scattered, easy to miss a site, and the
  81.2 doc explicitly asked for a centralized detection point.
- **A subclassed `ObjectOrLinkConverter`** — rejected: the library's converter dispatches on the
  public mutable `ObjectTypes.Types` registry, which Iris already uses for `Mute`. The
  registry approach is simpler and has an in-repo precedent.
- **Registering in `Iris.Server` only** — rejected: the client and the Core round-trip would not
  see the mapping (see "Decision" above).

## Test counts

- **5 new** integration tests (`FeedCommunityMappingTests`) — all passing.
- **1 updated** existing test (`PleromaFamilyInteropRoundTripTests.PleromaFamily_FeedCommunityActor_DeserializesAndRoundTrips`) — now asserts the `Group` mapping; still passing.
- Full suite green: `dotnet build` 0 warn / 0 err; `dotnet test --filter "Category!=Slow"`
  0 failed across all 11 assemblies (1065 Iris.Server.Tests passing, up from 1060).

## Spec references

- ActivityStreams `Group` (an `Actor` that represents a group of members):
  <https://www.w3.org/TR/activitystreams-v2/#group-objects>.
- Pleroma's `Feed` type is a Pleroma-fork extension for communities; standard Pleroma and
  ActivityPub use `Group`. PieFed (piefed.social) is a Pleroma fork that uses `Feed`.

## Follow-up (Phase 86.2)

86.2 is the live/fixture verification slice: confirm against a live PieFed instance (or an
expanded fixture set) that a followed `Feed` community renders as a community in the web client,
and that membership/follow edges route correctly. 86.1 establishes the type mapping; 86.2
verifies the end-to-end behavior.
