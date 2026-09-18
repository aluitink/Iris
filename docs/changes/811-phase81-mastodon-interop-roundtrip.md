# 81.1 — Mastodon wire-format interop round-trip tests (Phase 81, slice 1)

**Slice:** 81.1 (live-server interop sweep: Mastodon) — server-side wire-format conformance.
**Status:** DONE — 4 new coded tests in `Iris.Core.Tests` verify Iris deserializes + extracts real Mastodon AP documents correctly.
**Companion:** Phase 80 is complete ([800](800-phase80-bug-hunt-tracker.md)); Phase 81 (federation hardening) is now ACTIVE.

## What was done

Captured **genuine Mastodon ActivityPub documents** from `mastodon.online` (a live Mastodon instance that permits **unsigned** AP fetches — unlike `mastodon.social`, which requires HTTP Signatures) and added 4 round-trip tests that run them through Iris's deserializer + core-side rendering extractors.

### Why fixtures, not live testing

Phase 79.2 established that live cross-instance testing from the Docker container is blocked by network isolation (the container has no outbound HTTP tooling, and its NAT egress to arbitrary AP servers is unreliable). The host can reach Mastodon, but the **container** (where the app's outbound fetches run) is the real constraint. Following the established interop-verification pattern (79.2/79.3), I **captured real wire-format fixtures** (from the host, which can reach `mastodon.online`) and **round-trip them through `Iris.Core`'s deserializer** in coded tests — repeatable, CI-runnable, and free of network flakiness.

### Fixtures captured (`tests/Iris.Core.Tests/InteropFixtures/`)

| File | What it is | Key interop detail |
|---|---|---|
| `mastodon-actor-person.json` | `Person` (Gargron) | single-valued `inbox`/`outbox`/`followers`; `webfinger`/`featured` extensions |
| `mastodon-note-108379641124250772.json` | public `Note` | media `Document` attachment, `#Caturday` hashtag, `sensitive:false` |
| `mastodon-note-109235168212367317.json` | followers-only `Note` reply | cross-instance `inReplyTo` (kirakiratter.com), 1 `Mention` |
| `mastodon-note-109251632999281181.json` | public `Note` reply | 2 `Mention`s, cross-instance `inReplyTo` (mastodon.social) |
| `mastodon-outbox.json` + `mastodon-outbox-page1.json` | `OrderedCollection` + page | outbox serves `Create`/`Announce` **activities** (not bare objects) |

All fixtures are the **unmodified** real wire format: full Mastodon `@context` (an array of strings + `@id`/`@type` term mappings), **explicit `null`** values for absent fields, and a **scalar** `attributedTo`.

### Tests (`MastodonInteropRoundTripTests`, 4 facts)

Each: `ActivityJson.Deserialize<IObjectOrLink>(json)` → `Assert.IsType<T>` → core-side `IriExtensions` extractors → `ActivityJson.Serialize` round-trip (asserting Mastodon extension fields survive in `ExtensionData`):

1. **`Mastodon_PersonActor_DeserializesAndRoundTrips`** — `IsType<Person>`; `PreferredUsername`/`Name`/`Summary` populate; single-valued `Inbox`/`Outbox`/`Followers` resolve to `ILink`; `webfinger`/`featured` extensions survive.
2. **`Mastodon_PublicNoteWithMediaAndHashtag_DeserializesAndExtracts`** — `IsType<Note>`; raw `to` keeps the AS Public sentinel; `GetAudienceIris()` filters it (correct contract); `IsSensitive()` false; `GetRichAttachments()` → 1 media URL; `GetHashtagTags()` → `#Caturday` + href; `IsPreRenderedHtmlContent()` true; `atomUri`/`inReplyToAtomUri`/`contentMap`/`conversation` survive.
3. **`Mastodon_FollowersOnlyCrossInstanceReply_DeserializesAndExtracts`** — `IsType<Note>`; `to`=followers (NOT public); `InReplyTo.FirstOrDefault()` resolves the kirakiratter.com parent; `GetMentionIris()` → 1 cross-instance mention.
4. **`Mastodon_PublicNoteWithMultipleMentions_DeserializesAndExtracts`** — `IsType<Note>`; public `to`; `InReplyTo` resolves the mastodon.social parent; `GetMentionIris()` → 2 mentions (shoq + gnomon).

## Interop confirmations (no code changes needed — all behaved correctly)

- **AS Public sentinel:** the deserializer **keeps** `https://www.w3.org/ns/activitystreams#Public` in `to`; `GetAudienceIris()` **deliberately filters it** (public is a visibility flag, not a deliverable audience) — correct, intended design.
- **Explicit `null`s:** Mastodon sends `"inReplyTo": null`, `"summary": null`, `"sensitive": false` explicitly — all deserialize cleanly (no NRE, `InReplyTo` becomes an empty/absent list, `IsSensitive()` false).
- **Scalar `attributedTo`:** accepted (the library normalizes to its `IEnumerable` property).
- **`inReplyTo` is a list** in the library (`IEnumerable<IObjectOrLink>`); the first (only) entry is the parent — tests use `.FirstOrDefault()`.
- **Hashtags surface verbatim** with the `#` prefix (`#Caturday`), matching the wire format; the UI strips/renders as it chooses.
- **Media as `Document`:** Mastodon wraps images as a `Document` (not `Image`) with `mediaType`; `GetRichAttachments()` resolves the media URL correctly.
- **Outbox serves activities:** the outbox page contains `Create`/`Announce` activities (the `Note` is nested in `Create.object`) — Iris's outbox processing already unwraps this (verified in Phases 19–26); noted here for the 81.3 pagination slice.

**No production code changes were required** — Iris's deserializer + extractors handled all real Mastodon wire-format features correctly. This is a **verification** slice (locking in conformance with real-world data), not a fix slice.

## Decision (recorded per the autonomous-loop open-questions policy)

**Fixture loading:** added `<None Include="InteropFixtures\**\*.json" CopyToOutputDirectory="PreserveNewest" />` to `Iris.Core.Tests.csproj` and read fixtures at runtime from `AppContext.BaseDirectory`. Rationale: tests the **real, unmodified** wire format (full `@context`, explicit nulls, scalar `attributedTo`) rather than a trimmed inline literal; keeps the test file small + readable; makes the captured fixtures actually useful. This is a small, justified csproj change (a new `<None>` item, no new packages). The prior Phase 79 tests use inline literals — that pattern is preserved for those tests; this new test is the first to consume on-disk fixtures, which is a strict improvement for fidelity.

## Verification

- **Build:** `dotnet build -c Release` → **0 warnings, 0 errors**.
- **Tests:** `dotnet test -c Release --filter "Category!=Slow"` → **1624 passed, 0 failed, 1 skipped** (up from 1620 — the 4 new tests; `Iris.Core.Tests` 356 → 360). No regressions in any other project.

## Files changed

- `tests/Iris.Core.Tests/MastodonInteropRoundTripTests.cs` — new (4 tests + helpers).
- `tests/Iris.Core.Tests/InteropFixtures/*.json` — new (6 real Mastodon fixtures).
- `tests/Iris.Core.Tests/Iris.Core.Tests.csproj` — copy `InteropFixtures/**/*.json` to output.
- No production code changes.

## Test-debt log

- **Web tests:** none touched (this is a core/server-side wire-format test, which is explicitly in-scope for new coded tests per the WASM manual-test policy).
- **Core tests:** 4 new, all passing; no existing tests modified or deleted.
