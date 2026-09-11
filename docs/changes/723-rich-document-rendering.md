# 72.3 — Richer inline document rendering

- **Status:** implemented (build + image-path live-verified; no live document/PDF data to test the new paths against)
- **Phase:** 72 (Efficiency + UX residuals), slice 3
- **Resolves:** the Phase 71.2 residual — non-image documents (PDFs, etc.) rendered as a bare link-card (icon + name + open-in-new-tab) instead of an inline preview

## Problem

`MediaGallery.razor` rendered non-image/non-video/non-audio attachments as a small link-card (a `.object-attachment` row: icon + 48×48 preview thumb + name + type). A document with a `Preview` image read as a tiny thumbnail next to a link — not as part of the post. PDFs got the same link-card treatment (no inline preview).

## Fix

Replaced the `.object-attachment` link-card with a **document grid** (`.doc-gallery`) that mirrors the image `.media-gallery` grid layout (single/two/three/four column classes). Each document card:

- **Image preview** — when the attachment has a `Preview` with an image URL, the preview fills the card (like a media grid item), with a name + type label overlaid on a gradient scrim at the bottom.
- **PDF inline** — when the attachment URL ends in `.pdf` (no image preview), an `<iframe>` renders the PDF inline (bounded height, `object-fit: contain` on a white background), with the same name + type label overlay.
- **Generic placeholder** — for other documents (no preview, not a PDF), a large icon fills the card, with the same label overlay.

The name link keeps `target="_blank"` and adds a `download` attribute (the browser downloads the file rather than navigating to it).

### Files

- `apps/Iris.Web.Client/Components/MediaGallery.razor` — document loop replaced with the `.doc-gallery` grid; new `IsPdf()` / `IsImagePreview()` helpers.
- `apps/Iris.Web.Client/wwwroot/css/app.css` — `.object-attachment` rules replaced with `.doc-gallery` rules (grid layout + card + preview/PDF/placeholder + label overlay).

## Verification

- **Build:** `dotnet build apps/Iris.Web.Client` 0 warn / 0 err. **Full suite** (`dotnet test Iris.slnx`): **975 passed, 17 skipped, 0 failed**.
- **Image path live-verified** on `:8088` (fresh `docker compose build --no-cache` + `--force-recreate`, browser cache cleared): a note with an `Image` attachment renders the `.media-gallery` with 1 image, **0** `.doc-gallery`, **0** `.object-attachment` (the old class is gone), **0 console errors** — confirming the change does not regress the image path.
- **Document/PDF paths not live-verified** — the live DB has no document/PDF attachments (all `attachment` arrays are empty or contain `Image` type only). The new code paths (image-preview card, PDF iframe, generic placeholder) are verified by build + code inspection only.

## Residual

- The PDF iframe loads the PDF in a sandboxed context; some browsers may block inline PDF rendering depending on the `Content-Disposition` header the server sends. If the server sends `Content-Disposition: attachment`, the iframe will trigger a download instead of rendering inline. (The Iris media endpoint serves the blob with the original `mediaType` and no `Content-Disposition`, so same-origin PDFs should render inline.)
- No new coded tests (WASM manual-test policy — UI verified via Playwright; the document/PDF paths lack live data to drive).
