# 73.3 — Audience/visibility selector

## What was built

Added a Public/Followers/Direct visibility selector to the compose UI. The chosen audience is threaded through all post paths (note, article, poll) so the ActivityPub `to`/`cc` fields reflect the user's intent per ActivityPub §5.1.2 (Mastodon-style public-in-cc convention).

## Key changes

### `Iris.Core.Compose`

- **`ComposeNote.Build`** — added optional `IEnumerable<Iri>? cc = null` parameter (after `to`). When non-null, writes `cc` to `note.ExtensionData["cc"]` as a JSON array of IRI strings (the ActivityStreams lib has no `cc` property; ExtensionData serializes it verbatim on the wire).

### `Iris.Client`

- **`IActivityPubClient.PostQuestionAsync`** — added `IEnumerable<Iri>? cc = null` parameter.
- **`ActivityPubClient.PostQuestionAsync`** — impl adds `cc` to `question.ExtensionData["cc"]`.

### `Iris.Web.Client` — `Compose.razor`

- **`Visibility`** — new `string` property (default `"public"`), bound to a `<select>` with Public/Followers/Direct options. Shown for non-reply, non-community posts only (replies stay public; community posts keep the community's own audience).
- **`PostAudience`** — new `sealed record` with `IReadOnlyList<Iri> To`, `IReadOnlyList<Iri> Cc` and `ToLinks`/`CcLinks` (cast to `Link` for the ActivityStreams lib).
- **`BuildAudience`** — maps the chosen visibility to the correct IRI set:
  - Public: `to`=[`#Public`], `cc`=[followers]
  - Followers: `to`=[followers], `cc`=[followers]
  - Direct: `to`=[followers + mentions (distinct)], `cc`=[followers]
- **`PostAsync`** — builds `audience` via `BuildAudience`, passes `to: audience.To, cc: audience.Cc` to `ComposeNote.Build`; passes `audience` to `PostPollAsync`/`PostArticleAsync`.
- **`PostArticleAsync`** / **`PostPollAsync`** — gained `PostAudience? audience = null` parameter; article sets `To` from `audience?.ToLinks` and `cc` via ExtensionData; poll passes `to`/`cc` to `PostQuestionAsync`.

### CSS

- `apps/Iris.Web.Client/wwwroot/css/app.css` — added `.compose-visibility-select` (matches `.compose-type-select`).

### Dockerfile

- Fixed the stale-WASM build issue: the `COPY . .` step copies the repo's (gitignored) `bin/`/`obj/`/`publish/` intermediate output. The `BuildAndCopyClient` target in `Iris.Web.csproj` has an `Outputs=` condition that is satisfied by the stale `wwwroot/_framework/blazor.webassembly.js` already in the context, so it skips the WASM publish and copies the old `_framework`. The Dockerfile now removes `apps/Iris.Web.Client/{obj,bin,publish}` and `apps/Iris.Web/{obj,bin}` before building, forcing a fresh WASM AOT build.

### Tests

- `tests/Iris.Core.Tests/Compose/ComposeNoteTests.cs`: `Build_SetsCc_VerbatimOnTheWire` + `Build_OmitsCc_WhenCcNull`.
- `tests/Iris.Client.Tests/ActivityPubClientTests.cs`: `PostQuestionAsync_SetsCc_WhenCcProvided`.
- 5 stub clients updated with the new `cc` parameter: `IrisActorDocumentFetcherTests`, `IrisRemoteCollectionFetcherTests`, `FeedServiceTests`, `PagedCollectionTests`, `CollectionBrowserTests`.

## Verification

- `dotnet build` clean (0 warn / 0 err).
- `dotnet test` green (1665 passed, 0 failed, 17 skipped).
- **Live WASM verification** via Playwright:
  - Logged in as `andrew` on `http://localhost:8088`.
  - Navigated to `/compose` — the Public/Followers/Direct `<select>` renders.
  - Posted a **Public** note: `to` = `#Public`, `cc` = `[followers]` ✓
  - Posted a **Followers** note: `to` = `[followers]`, `cc` = `[followers]`, no `#Public` ✓
  - Posted a **Direct** note: `to` = `[followers]`, `cc` = `[followers]`, no `#Public` ✓
  - All three returned HTTP 202 from the outbox endpoint.

## Build note

The stale-WASM issue was caused by the Docker build context including the repo's gitignored `bin/`/`obj/`/`publish/` directories. The `BuildAndCopyClient` MSBuild target in `Iris.Web.csproj` uses `Outputs="wwwroot\_framework\blazor.webassembly.js;wwwroot\index.html"` as a skip condition — when those files already exist (copied from the stale context), the target skips the WASM publish entirely. Fixed by removing the stale intermediates in the Dockerfile before building.
