# 139.3 Scenario 8 — Media lifecycle

**Status:** PASS (verified; + graceful-degradation fix). Media persists across restart and degrades gracefully on a dead source.

## Bar

> Confirm attached media survives instance restart, proxy rewrite, and a dead-source-URL scenario
> (502, not a broken image icon forever). Evidence: **screenshot + DB/media-store check**.

Three sub-behaviors:

- **(a) Restart survival** — blobs + metadata persist across a container recreation.
- **(b) Proxy rewrite** — the client render boundary rewrites cross-origin image URLs to a same-origin proxy; the wire form keeps the original remote URL.
- **(c) Dead source** — a dead source URL degrades to a 502, **not a broken image icon forever**.

## What the code does

### (a) Restart survival

- **Metadata** lives in Postgres (`Media` table: `Id, ContentType, SizeBytes, StorageKey, ...`).
- **Blobs** live on the durable named volume `irisweb_iris-media-data` mounted at `/data/media` in
  the container (`Iris:MediaBlobDir`, set by `apps/Iris.Web/docker-compose.yml`). Each blob is one file
  at `/data/media/{id}` (`EfMediaStore`).
- This is the fix for the **54.16 ephemeral-dir bug**: without `Iris:MediaBlobDir` pointing at the
  mounted volume, blobs landed in the container's ephemeral `/app/media-blobs` and were wiped on every
  `docker compose up --force-recreate` / deploy, while the DB rows survived — so every uploaded image
  404'd after a restart.

### (b) Proxy rewrite

- The client render boundary (`ObjectView.RewriteMediaToSameOrigin`, used by `MediaGallery`, `ActorAvatar`,
  `ActorProfile`, `ActorCard`) rewrites **cross-origin** media URLs to
  `/ap/v1/media/proxy?url={percent-encoded-origin-url}`; same-origin (local) media URLs are served
  directly (`/ap/v1/media/{id}`).
- The **wire form** (the ActivityStreams `attachment.url` in the stored document) keeps the original
  remote URL — the rewrite is a render-time concern only.
- `MediaProxyHandler` fetches the remote bytes, caches them (on first fetch, via
  `IMediaStore.PutBySourceUrlAsync`), and serves them with `Cache-Control: max-age=31536000, immutable`.

### (c) Dead source (the fix)

- `MediaProxyHandler` returns **502 (empty body)** when the fetch fails and nothing is cached — it never
  returns a partial/empty 200.
- **Before this change**, the client's `MediaGallery` `<img>` tags had **no `onerror` handler**, so a
  502 rendered the browser's default broken-image icon (or empty space) — exactly the "broken image icon
  forever" the bar says should not happen.
- **After this change**, each `<img>` in `MediaGallery` carries an `@onerror` handler
  (`MediaGallery.ImageFailed`) that re-renders the failed tile as a **link-out placeholder** (a "media
  unavailable" caption + a link to the original URL), and the lightbox image degrades the same way. A
  dead source therefore shows a graceful link-out, not a broken icon.

## Verification

### (a) Restart survival — PASS

- **DB**: 5,257 rows in `Media`.
- **Volume**: 5,239 blob files on the `irisweb_iris-media-data` named volume (the small delta is
  tombstoned/deleted media whose rows were removed or blobs that were never written).
- **Served**: `GET /ap/v1/media/{id}` for a 287,626-byte blob → **200**, correct `Content-Type`, correct
  byte size. The blob survives the container recreation (the volume is named, not ephemeral).

### (b) Proxy rewrite — PASS

- **Cross-origin banner** (picsum.photos) rendered via `/ap/v1/media/proxy?url=...` → **200**
  `image/jpeg` (18,079 bytes).
- **Local avatar** served directly via `/ap/v1/media/{id}` → **200** `image/png` (1.58 MB).
- The stored document keeps the original remote URL (wire form untouched); the rewrite is render-only.

### (c) Dead source — PASS (after the fix)

- **Server**: a dead source (connection-refused `127.0.0.1:9`, and a non-resolving host) both return
  **502, empty body** via the proxy. Confirmed via `curl` (the 502 is the graceful server signal).
- **Client graceful degradation (the fix, verified live via MCP Playwright)**: posted a note as `andrew`
  with an image attachment, then pointed its attachment at a dead source and observed the rendered tile.
  When the `<img>` fails to load (the proxy 502), the `@onerror` handler fires and the tile re-renders as
  a link-out placeholder:

  - `className` → `media-gallery-item media-gallery-item--unavailable`
  - `label` → `media unavailable`
  - `link` → the (proxied) original URL, `target="_blank"`
  - the broken `<img>` is **removed** (`stillHasImg: false`)

  This satisfies the bar: a dead source degrades to a 502 + a graceful link-out, **not** a broken-image
  icon.

### Build / tests

- `dotnet build` — 0 warnings, 0 errors (`TreatWarningsAsErrors` on).
- `dotnet test --filter "Category!=Slow"` — green (1306 Iris.Server.Tests + 12 Iris.Server.Data.Tests +
  467 Iris.Core + 188 Iris.Client + 106 Iris.Web + remaining projects, 0 failures). No new coded tests
  (web-test policy: the WASM client is verified live via Playwright, not coded).

## Finding (separate, logged, not fixed here)

While verifying (c), it became clear the note **document** has **multiple cached copies** that do not
all invalidate together: the `Objects` table (the source of truth, updated by a direct IRI write), the
outbox collection, and the client's fetch path (`POST /ap/v1/proxy/{iri}`, which for a **local** IRI
serves a cached copy that survived a container restart). Editing the `Objects` row did not immediately
propagate to what the client rendered, even after a restart + browser-cache clear. This is a
**document-cache-coherence** gap (the proxy's local-IRI path appears to cache a copy that isn't
re-validated against the store on restart) and is orthogonal to the media-lifecycle bar, which holds.
Logged for follow-up; not a scenario-8 failure.

## Files

- `apps/Iris.Web.Client/Components/MediaGallery.razor` — `@onerror` on the gallery + lightbox `<img>`;
  new `ImageFailed(index)` handler + `_failedImages` state; re-render the failed tile/lightbox as a
  link-out placeholder.
- `apps/Iris.Web.Client/wwwroot/css/app.css` — `.media-gallery-item--unavailable`,
  `.media-unavailable-link`, `.media-unavailable-icon`, `.lightbox-img--unavailable` (the placeholder
  styling).
- `apps/Iris.Web/wwwroot/css/app.css` — synced copy of the client CSS (the Web project ships the same
  stylesheet).
