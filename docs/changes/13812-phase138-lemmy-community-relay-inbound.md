# 138.12 — Lemmy community relay inbound (Announce(Create(Page)))

**Date:** 2026-09-15
**Phase:** 138 (Lemmy community federation)
**Slice:** 138.12 — Lemmy community post surfaces in Iris

## Summary

Implemented the inbound path for Lemmy's community relay pattern. When a Lemmy community relays a
member's post, it sends an `Announce` activity whose object is the member's `Create` activity, which
in turn embeds a `Page` (or `Note`). Previously, the `AnnounceActivityHandler` only handled plain
`Announce` activities (where the object is a bare object IRI, i.e., a boost/re-share). It did not
unwrap embedded `Create` activities, so Lemmy community posts never surfaced in Iris community feeds.

## Root cause

Lemmy's community relay pattern wraps a member's `Create` inside an `Announce`:

```json
{
  "type": "Announce",
  "id": "https://lemmy.luit.ink/activities/announce/create/51a65ff0",
  "actor": "https://lemmy.luit.ink/c/interop",
  "object": {
    "type": "Create",
    "id": "https://lemmy.luit.ink/c/interop/123",
    "actor": "https://lemmy.luit.ink/u/bob",
    "object": {
      "type": "Page",
      "id": "https://lemmy.luit.ink/post/1",
      "name": ["Hello"],
      "content": ["<p>World</p>"]
    }
  }
}
```

The `AnnounceActivityHandler` extracted the `object` as an IRI (via `ResolveObjectIri()`), which
returned the `Create`'s IRI, but never unwrapped the embedded `Create` to store the `Page` in the
object store or record it in the community's members' outboxes.

## Changes

### `src/Iris.Server/Inbox/AnnounceActivityHandler.cs`

1. **Unwrap embedded `Create`**: When the `Announce`'s object is an embedded `Create` activity (not a
   bare object IRI), the handler now unwraps it and calls `HandleEmbeddedCreateAsync`, which:
   - Stores the embedded object (`Page`/`Note`) in the object store (with a tombstone guard).
   - Records the `Create` in the community's local members' outboxes via
     `CommunityContentRecorder.RecordToMembersAsync`.

2. **Community recipient check**: The handler now checks if the recipient is a local community (in
   addition to a local person) before processing. Previously, it only checked `IsLocalActorAsync`
   (the actor store), which does not include communities (they live in the community store). This
   mirrors the `CreateActivityHandler`'s dual-store check.

### `tests/Iris.Server.Tests/LemmyCommunityRelayIntegrationTests.cs` (new)

3 unit tests:
- `LemmyCommunityRelay_AnnounceWithEmbeddedCreate_StoresPageAndRecordsInMemberOutbox`: A Lemmy-style
  `Announce(Create(Page))` stores the `Page` in the object store and records the `Create` in the
  community's local member's outbox.
- `PlainAnnounce_ObjectIsBareIri_DoesNotStoreInObjectStore`: A plain `Announce` (boost) does NOT
  store the object in the object store.
- `LemmyCommunityRelay_AnnounceIsAlsoRecordedInCommunityOutbox`: The `Announce` itself is still
  recorded in the community's outbox (the relay envelope).

## Verification

- `dotnet build` — 0 warnings, 0 errors.
- `dotnet test tests/Iris.Server.Tests` — 1226 passed, 16 skipped, 0 failed (including the 3 new
  tests).

## Notes

- This implements the server-side handling of Lemmy's relay pattern. The live end-to-end test
  (posting from Lemmy and verifying it appears in the Iris community feed) requires a running Lemmy
  stack and is exercised manually (per the Phase 45+ web test policy).
- The `Announce`'s `to`/`cc` audience is not relevant for the inbound path (the recipient is
  determined by the inbox the activity was delivered to, not by the audience fields).
