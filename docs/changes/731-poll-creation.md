# 73.1 — Poll creation (outbound)

- **Status:** implemented (build + client round-trip test green; no new coded UI tests per WASM manual-test policy)
- **Phase:** 73 (Compose feature completion), slice 1
- **Resolves:** polls render (Phase 58.2, F-26) but can't be created — the compose UI has no poll option and `IActivityPubClient` had no `PostQuestionAsync`.

## Problem

`Compose.razor`'s type selector only offered Note / Article / Reply / Community. A user with a poll to ask had no way to author one. The read path (`IriExtensions.GetPollData`) already supported two wire shapes (Mastodon `poll` extension + AS2.0 `Question` with `options`/`endTime`/`closed`/`multiple`), but nothing on the client emitted either.

## Design decision: emit the Mastodon `poll` extension shape

`PostQuestionAsync` builds a generic ActivityStreams `Object` of type `Question` whose `ExtensionData` carries a **top-level `poll` object** (the Mastodon extension shape):

```json
{
  "type": ["Question"],
  "content": ["Who wins?"],
  "attributedTo": [ ... ],
  "to": [ ... ],
  "poll": {
    "options": [{"title": "Option A", "votesCount": 0}, {"title": "Option B", "votesCount": 0}],
    "expired": false,
    "multiple": true,
    "totalVotes": 0,
    "endsAt": "2026-11-15T12:00:00Z"
  }
}
```

**Why the nested `poll` object and not the AS2.0 flat `endTime`/`closed`/`multiple` keys:** the ActivityStreams library's deserializer **drops individual scalar keys from `ExtensionData` on `IObject`/`Note`** (verified empirically — `endTime` survives the wire but is absent from `ExtensionData` after `ActivityJson.Deserialize<IObject>`). A nested `JsonElement` property (the `poll` object) **survives intact** through the same deserializer, which is exactly what `IriExtensionsTests.GetPollData_RoundTripsThroughJsonSerialization` already proves for the Mastodon shape. The AS2.0 flat path would round-trip the wire correctly but the client's own `GetPollData` re-read would lose `endsAt`.

The `poll` object is built as a **raw JSON string** and parsed into a single `JsonElement` (NOT by serializing ActivityStreams objects directly — `SerializeToElement` on a runtime ActivityStreams type injects a `@context` into each option, polluting the wire). This mirrors how `ParsePollFromExtension` reads the fields back.

## Changes

### Client

- `src/Iris.Client/IActivityPubClient.cs` — `PostQuestionAsync` declared (after `PostNoteAsync(Note)`).
- `src/Iris.Client/ActivityPubClient.cs` — `PostQuestionAsync` implemented (after `PostReplyAsync`). Validates ≥2 non-empty options; builds the `Question` object with the `poll` extension in `ExtensionData`; optional audience `to`, mention `tag`s, hashtag `tag`s (same convention as `PostReplyAsync`); publishes a `Create` to `actorId.OutboxOf()`.

### Compose UI

- `apps/Iris.Web.Client/Components/Pages/Compose.razor`:
  - "Poll" option added to the type `<select>`.
  - Poll editor block: question input, 2–4 option rows (add/remove), duration `<select>` in minutes (5m/30m/1h/6h/1d/3d/7d), multiple-choice checkbox, error `<p>`.
  - State fields `PollQuestion` / `PollOptions` / `PollDurationMinutes` / `PollMultiple` / `PollError` + `AddPollOption` / `RemovePollOption` / `GetValidPollOptions`.
  - `ContentType == "Poll"` branch in `PostAsync` → new `PostPollAsync` helper (calls `PostQuestionAsync` with `endsAt = now + duration`); success block clears poll state.
- `apps/Iris.Web.Client/wwwroot/css/app.css` — `.compose-poll*` styles after `.compose-type-select`.

### Test stubs

Five test stubs implementing `IActivityPubClient` updated with a `PostQuestionAsync` stub (return `202` or `throw NotSupportedException`):

- `tests/Iris.Server.Tests/Caching/IrisRemoteCollectionFetcherTests.cs` (`StubCollectionClient`)
- `tests/Iris.Server.Tests/Security/IrisActorDocumentFetcherTests.cs` (`StubActivityPubClient`)
- `tests/Iris.Server.Tests/Services/FeedServiceTests.cs` (`StubClient`)
- `tests/SampleBlazorClient.Tests/PagedCollectionTests.cs` (`BaseFakeClient`)
- `tests/SampleBlazorClient.Tests/CollectionBrowserTests.cs` (stub)

### New tests

- `tests/Iris.Client.Tests/ActivityPubClientTests.PostQuestionAsync_BuildsAs2Question_ThatRoundTripsThroughGetPollData` — posts a 2-option poll via `PostQuestionAsync`, captures the wire, deserializes the embedded object with `ActivityJson.Deserialize<IObject>`, and asserts `GetPollData` returns a 2-option poll with the right `endsAt` + `multiple` flag (votes zero on a fresh poll). Also asserts the wire carries `endsAt`.
- `tests/Iris.Client.Tests/ActivityPubClientTests.PostQuestionAsync_FewerThanTwoOptions_Throws` — a 1-option poll is rejected client-side with `ArgumentException`.
- `FirstValue` helper (kept) — normalizes single-string vs single-element-array serialization of multi-valued AS slots.

## Verification

- **Build:** `dotnet build` 0 warn / 0 err.
- **Full suite** (`dotnet test Iris.slnx -c Release`): **975 passed, 17 skipped, 0 failed**.
- **Round-trip test green:** `PostQuestionAsync_BuildsAs2Question_ThatRoundTripsThroughGetPollData` + `PostQuestionAsync_FewerThanTwoOptions_Throws` both pass (2/2).
- **UI not Playwright-verified this turn** — the poll editor markup + state + `PostPollAsync` wiring are verified by build + code inspection; no live poll data to drive a Playwright round-trip this turn. (Optional follow-up: rebuild the WASM container on a fresh port and click through the poll editor.)

## Residual

- The AS2.0 flat `endTime`/`closed`/`multiple` path in `ParsePollFromAs2` remains in the read code for inbound Pleroma objects, but is **not** the shape Iris emits. If a future slice needs the AS2.0 shape outbound (e.g. to interop with a Pleroma instance that doesn't understand the Mastodon `poll` extension), the library's `ExtensionData` scalar-drop would need to be worked around (e.g. by emitting the poll as a `Note` with the flat keys and accepting that the client's own `GetPollData` re-read loses `endsAt` until the library is patched).
- 73.6 (poll voting) is the slice that adds the interactive vote path + the only new server endpoint in Phase 73.
