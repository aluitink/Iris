# 74.1 — Sensitive content blur now includes the image

## Summary

The sensitive-content (CW) blur previously only blurred the text content.
The image/media gallery was rendered outside the `.object-sensitive` div and
was fully visible even before the user clicked "Show". This change extends
the blur to cover the media as well.

## Changes

### `MediaGallery.razor`

- New `Blurred` parameter (`bool`, default `false`).
- All rendered media (image gallery, video players, audio players, document
  gallery) is wrapped in a `<div class="media-gallery-wrap">`.
- When `Blurred` is true, the wrapper also gets the `object-content--blurred`
  class (`filter: blur(8px); user-select: none; pointer-events: none`).
- The lightbox overlay is *outside* the wrapper (it only appears on click,
  which is impossible while `pointer-events: none` is active).

### `ObjectView.razor`

- Create section: `<MediaGallery ... Blurred="@ActivityMediaBlurred" />`
- Bare-object section: `<MediaGallery ... Blurred="@ObjectMediaBlurred" />`

### `ObjectView.razor.cs`

- `ActivityMediaBlurred => ActivityIsSensitive && !ActivityRevealed`
- `ObjectMediaBlurred => IsSensitive && !Revealed`

### `app.css`

- `.media-gallery-wrap` — no visual effect by default; the existing
  `.object-content--blurred` class provides the blur when applied.

## Verification

- Build: 0 warn / 0 err. Full suite: 1665 passed, 0 failed, 17 skipped.
- Live (Playwright on `:8088`):
  - Navigated to a sensitive post with an image attachment.
  - Initial state: `.media-gallery-wrap` has `object-content--blurred` class,
    computed `filter: blur(8px)`, `pointer-events: none`. Image is blurred.
  - Clicked "Show": class removed, `filter: none`, `pointer-events: auto`.
    Image and text both visible.
  - Clicked "Hide": class re-applied, `filter: blur(8px)`. Image blurred again.
  - Zero console errors throughout.

## Design note

The blur is applied to a *wrapper* div around the entire media gallery rather
than to individual `<img>`/`<video>` elements. This is simpler (one class
toggle) and correctly handles all media types (images, video, audio, documents)
in a single stroke. `pointer-events: none` on the wrapper prevents the user
from opening the lightbox or interacting with media while the content is
blurred — matching the intent that sensitive content is not viewable until
explicitly revealed.
