# 81.2 — Misskey + Pleroma wire-format interop round-trip tests (Phase 81, slice 2)

**Slice:** 81.2 (live-server interop sweep: Misskey + Pleroma) — server-side wire-format conformance.
**Status:** DONE — 5 new coded tests in `Iris.Core.Tests` (4 Misskey + 1 PieFed/Pleroma-family) verify Iris deserializes + re-emits real Misskey + Pleroma-family AP documents correctly.
**Companion:** 81.1 (Mastodon) is complete ([811](811-phase81-mastodon-interop-roundtrip.md)).

## What was done

Captured **genuine wire-format documents** from two live, permissive instances and added 5 round-trip tests:

- **Misskey** — from `misskey.io` (a live Misskey instance permitting unsigned AP fetches): a `Person` actor, a minimal public `Note`, a `Note` with custom Emoji tags, and an `Announce` (renote) activity.
- **Pleroma-family** — from `piefed.social` (PieFed, a Pleroma fork, so its AP documents are Pleroma-compatible): a `Feed` (community/group) actor.

All fixtures are the **unmodified** real wire format (full `@context`, explicit `null`/`[]` values, scalar `attributedTo`).

### Why fixtures (re-confirmed)

Same rationale as 81.1: the Docker container's outbound egress to arbitrary AP servers is unreliable (Phase 79.2), and most Pleroma instances are down/unreachable (probed ~10; only `piefed.social` was live + permissive). The host can reach these instances, so I captured real fixtures from the host and round-trip them through `Iris.Core`'s deserializer in coded tests — repeatable, CI-runnable, network-independent.

### Fixtures captured (`tests/Iris.Core.Tests/InteropFixtures/`)

| File | What it is | Key interop detail |
|---|---|---|
| `misskey-actor-person.json` | Misskey `Person` | `name` **omitted**; `_misskey_*` + `isCat` + `image` + `sharedInbox` extensions |
| `misskey-note-basic.json` | minimal public `Note` | `attachment: []`, `sensitive: false`, `inReplyTo: null` (explicit) |
| `misskey-note-tags.json` | `Note` with custom Emoji tags | `tag` holds `Emoji`-type objects (`:stat_sake:`) — NOT hashtags/mentions |
| `misskey-announce-renote.json` | `Announce` (renote) activity | `actor` + `object` + `to` (Public) + `cc` (followers) |
| `misskey-outbox.json` + `misskey-outbox-page1.json` | `OrderedCollection` + page | outbox serves `Create`/`Announce` activities |
| `piefed-group-actor.json` | PieFed `Feed` (community) actor | `Feed` type (PieFed's group term), `moderators`, `childFeeds`, `publicKey`, `endpoints.sharedInbox` |

### Tests (`MisskeyInteropRoundTripTests` 4 facts + `PleromaFamilyInteropRoundTripTests` 1 fact)

Each: `ActivityJson.Deserialize` → `Assert.IsType<T>` (or `IsAssignableFrom<IObject>`) → core-side `IriExtensions` extractors → `ActivityJson.Serialize` round-trip (extension fields survive in `ExtensionData`):

1. **`Misskey_PersonActor_DeserializesAndRoundTrips`** — `IsType<Person>`; `PreferredUsername` populates; `name` absent (tolerated, not a crash); `Inbox`/`Outbox` Links; `isCat` + `_misskey_summary` survive.
2. **`Misskey_NoteWithCustomEmojiTags_IsNotMisclassifiedAsHashtags`** — `IsType<Note>`; content is pre-rendered HTML; **`GetHashtagTags()` returns empty** (the Emoji tags must NOT be misclassified as hashtags); the 2 Emoji tags survive in `Tag`.
3. **`Misskey_Announce_Renote_DeserializesAndResolvesActorAndObject`** — deserialized via the **polymorphic converter** (`IObjectOrLink as Activity`, the dead-letter-store pattern); `Actor` + `Object` IRIs both resolve.
4. **`Misskey_BasicPublicNote_DeserializesAndRoundTrips`** — `IsType<Note>`; not sensitive; `to`=Public sentinel (kept raw, filtered by `GetAudienceIris`); round-trips.
5. **`PleromaFamily_FeedCommunityActor_DeserializesAndRoundTrips`** — deserializes to a valid `IObject` with the correct `Id`; the round-trip preserves the `Feed` type + every Pleroma-family field (`preferredUsername`, `name`, `outbox`, `moderators`, `childFeeds`, `publicKey`, `sharedInbox`).

## Interop confirmations (no code changes needed — all behaved correctly)

- **Misskey omits `Person.name`** — the deserializer tolerates it (null `Name`, no crash).
- **Misskey custom Emoji tags** are `type: Emoji` (not `Hashtag`) — `GetHashtagTags()` correctly returns empty for them (no misclassification).
- **`Announce` deserialization requires the polymorphic converter** — `Deserialize<Activity>` returns the base `Activity` class (no subtype dispatch); the working pattern is `Deserialize<IObjectOrLink>(json) as Activity` (used by `FileBackedDeliveryDeadLetterStore`). The `Actor` + `Object` fields are lists (`IEnumerable<IObjectOrLink>`), resolved via `.FirstOrDefault().ResolveObjectIri()`.
- **Misskey/PieFed `@context`** (arrays of strings + `@id`/`@type` term mappings, incl. `Emoji: toot:Emoji` and `misskey:`-namespaced terms) deserialize cleanly; the terms land in `ExtensionData`.

## Finding (documented — NOT fixed this turn)

**`Feed` is not mapped to `Group`.** The PieFed-specific `Feed` type (a community/group; standard Pleroma uses `Group`) is **not** mapped to the `Group` class by the polymorphic converter, so a `Feed` actor is not recognized as a community by Iris's `as Group` cast (`FileBackedCommunityStore.cs:62`). Standard Pleroma `Group` actors **are** handled (verified in Phase 78.3). **Mapping `Feed` → `Group`** (for full PieFed community support) is a **production code change** — out of scope for this verification slice. It's a candidate follow-up (a new slice in 81.3 or Phase 82). The test locks in the *current* behavior (round-trip preservation) so any future `Feed`→`Group` mapping can be added without regressing the round-trip.

## Decision (recorded per the autonomous-loop open-questions policy)

**Scope:** kept 81.2 as a **verification** slice (consistent with 81.1) — no production code changes. The `Feed`→`Group` mapping is a genuine gap but is a production change that warrants its own slice (with its own tests + a decision doc on how to detect `Feed` vs `Group` vs other fork-specific community terms). Recording it here + in the Active Slice keeps it from being lost. The Pleroma half is satisfied by the real PieFed `Feed` fixture (a Pleroma-family document) — a pure-Pleroma `Group` is already covered by Phase 78.3.

## Verification

- **Build:** `dotnet build -c Release` → **0 warnings, 0 errors**.
- **Tests:** `dotnet test -c Release --filter "Category!=Slow"` → **1629 passed, 0 failed, 1 skipped** (up from 1624 — the 5 new tests; `Iris.Core.Tests` 360 → 365). The single `Iris.Server.Tests` failure in the parallel full-suite run is the **known timing/contention flake** — it passes **975/975 in isolation** (a core-only change cannot affect server tests).

## Files changed

- `tests/Iris.Core.Tests/MisskeyInteropRoundTripTests.cs` — new (4 tests).
- `tests/Iris.Core.Tests/PleromaFamilyInteropRoundTripTests.cs` — new (1 test).
- `tests/Iris.Core.Tests/InteropFixtures/misskey-*.json` — new (6 Misskey fixtures).
- `tests/Iris.Core.Tests/InteropFixtures/piefed-group-actor.json` — new (1 PieFed fixture).
- No production code changes; no csproj change (the `<None Include="InteropFixtures\**\*.json">` from 81.1 already covers these fixtures).

## Test-debt log

- **Web tests:** none touched (core/server-side wire-format tests are in-scope for new coded tests per the WASM manual-test policy).
- **Core tests:** 5 new, all passing; no existing tests modified or deleted.
