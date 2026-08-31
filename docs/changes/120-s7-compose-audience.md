# 120 — S7: Compose audience (the note's `to` parameter)

**Status:** DONE — full solution green (`dotnet build` 0 warnings; `dotnet test` 870/870).

## Objective

`PostNoteAsync`'s (and `PostReplyAsync`'s) optional `to` (audience) parameter was never populated by the
Compose page. S7 exposes an **audience** input (comma-separated actor IRIs, or `Public`) and passes it
through, so the note's `to` reflects who it is addressed to. (Media/attachment upload is a larger lift —
noted as a follow-up, not this round.)

## Changes

### `samples/SampleBlazorClient/Pages/Compose.razor`

- Added an **Audience** input (above the "Reply to…" details). `Public` (case-insensitive) maps to the
  conventional `as:Public` address (`https://www.w3.org/ns/activitystreams#Public`); otherwise each
  comma-separated entry is an actor IRI. Empty = no explicit `to` (the default, unaddressed audience).
- `ParseAudience()` returns the parsed `IReadOnlyList<Iri>?` (null when empty).
- `PostAsync` passes the audience to both paths: `PostNoteAsync(actor, content, to)` and
  `PostReplyAsync(actor, parent, content, mentions, to)`.

## Test coverage

- `S7ComposeAudienceTests` (4 new) — in-process, logged on as `alice`, target `bob`; each posts via the
  same client call the page issues and asserts the stored object's `to`:
  - **PostNote_PublicAudience_CarriesAsPublic** — `to: [as:Public]` → the stored note's `to` includes the
    `as:Public` address.
  - **PostNote_ActorAudience_CarriesActorIris** — `to: [bob]` → the stored note's `to` includes bob's IRI.
  - **PostNote_NoAudience_CarriesNoTo** — no `to` → the stored note carries no `to`.
  - **PostReply_Audience_CarriesTo** — a reply with `to: [bob]` → the stored reply's `to` includes bob's IRI.
- Full solution green: **870/870** (SampleBlazorClient.Tests now 75: 71 → +4).

## Notes

- The `to` links are stored on the embedded `Note.To` (the server stores the `Create`'s embedded object
  as-is), so the tests read the audience straight from the object store (`ListObjectsAsync`).
- Logged-in *browser* verification is blocked by the environment's orphaned root-owned 8081 server (CORS
  locked to origin 8090) — an environment constraint, not a code defect. The in-process tests exercise the
  identical client API + server pipeline the page uses.
