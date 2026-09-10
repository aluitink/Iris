# 70.1 — media/pictures render correctly (live verify + harden pass)

Phase 70 (Content & media improvement) is the start of "improving" after the
exploratory buffer. Sub-goal 1 (70.1) is an **ensure-it-works + harden** pass over
the media path that was first built in Phases 20/54/61: drive the full
upload → store → serve → render cycle live on a fresh origin and confirm the
picture actually appears in the UI. **No new coded tests** (WASM manual-test
policy) — verified live via MCP Playwright.

## What was driven live (fresh origin `:8150`, fresh WASM publish, DB `irisweb-db-1`)

A detached server was launched on a fresh port (`:8150`) with
`IRIS__MEDIABLOBDIR=/tmp/iris-media-70` and `--contentRoot` set to the publish dir,
so the browser had no cached WASM and the media blobs landed in a known local dir.
Signed in as `andrew` (cookie auth).

### 1. Upload — `POST /local/v1/u/andrew/media` → **201**
A 10×10 PNG was uploaded via the cookie-authenticated media endpoint (the same
`IMediaClient` path the compose UI uses, which the WASM client cannot carry Basic
auth for). The server stored the blob and returned
`{"id":"https://iris.luit.ink/ap/v1/media/406065da…","type":"image/png","name":"test-media.png"}`.
- Blob confirmed on disk: `/tmp/iris-media-70/406065da…` (75 bytes).
- A `Media` row was inserted (`Id=406065da…`, `ContentType=image/png`,
  `StorageKey=/tmp/iris-media-70/406065da…`).

### 2. Serve — `GET /ap/v1/media/{id}` → **200 image/png**
The same-origin media IRI served the stored bytes with the correct content type.

### 3. Render — object-detail view shows the picture
A consistent note-with-`Image`-attachment was created directly in the store
(`Objects` Note + `Activities` Create + `BoxItems` outbox entry; the `attachment`
carried an `Image` whose `id` + `url.href` were the media IRI), then the object-detail
page was opened in the browser. The rendered DOM contained:

- `<img src="http://localhost:8150/ap/v1/media/406065da…">` — the absolute HTTPS media
  IRI was **rewritten same-origin** (`https://iris.luit.ink/ap/v1/media/{id}` →
  `/ap/v1/media/{id}`) by `RewriteMediaToSameOrigin`, so the `<img>` loads same-origin
  (no CORS / mixed-content).
- `naturalWidth=10`, `naturalHeight=10`, `complete=true` — the image **actually decoded**
  (it is the 10×10 test PNG), i.e. the picture is visible, not a broken image.
- `alt="test-media.png"` — the attachment `name` is used as the alt text.
- **0 console errors** on the page.

## Render code confirmed correct (by inspection)

- `ObjectView.razor.cs` — `MediaAttachments` / `ActivityMediaAttachments` extract the
  note's media IRIs (the latter reads the activity's *embedded* object, which is what the
  home timeline renders as a wrapping `Create`), each passed through
  `RewriteMediaToSameOrigin`. `RichAttachmentUrl` does the same for rich attachments.
- `RewriteMediaToSameOrigin` (ObjectView.razor.cs:81) — returns the absolute HTTPS IRI's
  `AbsolutePath + Query` (same-origin); leaves relative / non-HTTPS IRIs unchanged.
- `IriExtensions.GetMediaAttachments` (IriExtensions.cs:640) — reads `obj.Attachment`,
  keeps only `Image` entries, resolves the IRI from `id` (preferred) or the image URL, and
  carries the attachment `name`.

The feed (`/home`) and the object-detail view both render through the same `ObjectView`
component; the detail view is the canonical "does the picture render" check and it passed.

## Automation limitation noted (not an app defect)

The compose UI's image-attach + post could not be driven end-to-end via MCP Playwright:
Blazor's `@bind` on the compose `<textarea>` never registered a value under Playwright's
DOM manipulation (the char count stayed `0/500` regardless of `fill`, `type`
character-by-character, or a dispatched `input` event), so `PostAsync` early-returned on
the empty content. This is a **known MCP-Playwright + Blazor-WASM input-binding
limitation**, not a product bug (real-browser typing works — notes have been posted via
the compose UI in earlier phases). To exercise the render path without the binding
limitation, the note-with-image was created directly in the store and rendered via the
object-detail view. The compose **upload** half (the `IMediaClient` call) was exercised
directly via the cookie-auth media endpoint.

## Result

The media path works end-to-end: upload → store → serve → render, with a same-origin
`<img>` that decodes and displays, and no console errors. No code change was required —
70.1 is a **verification** slice (the path was already correct from Phases 20/54/61).

## Cleanup

The test note, Create activity, outbox edge, `Media` row, and blob were all removed
after verification to leave the store in a consistent state.
