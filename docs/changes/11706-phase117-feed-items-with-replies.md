# 117.6 — Feed Items with Replies: Show Parent Media in Reply Context Card

## Summary

When a reply is rendered in the feed or on an object detail page, the "In reply to"
context card now displays the parent object's media attachments (images, video,
audio) in addition to its text content. Previously only the parent's text was shown.

## Context

Phase 117.1/101 added the "In reply to" context card to `ObjectView.razor`, showing
the parent's author and text content. However, if the parent post had an image or
other media attachment, the reply card did not show it — the reader had to navigate
to the parent post to see the media. This was inconsistent with the goal of making
replies self-contained.

## Changes

### ObjectView.razor.cs

- Added `ParentRichAttachments` property: reads `GetRichAttachments()` from the
  already-fetched `_parentObject`. Returns empty list when the parent has no
  attachments or has not yet been fetched.

### ObjectView.razor

- **Create branch** (~line 92): Added `<MediaGallery>` inside the
  `.object-parent-context` div, after the `ParentContent` div, gated on
  `ParentRichAttachments.Count > 0`.
- **IObject branch** (~line 410): Same addition for the direct-object rendering path.

### app.css (both Client and Web copies)

- Added `.object-parent-context .media-gallery { margin-top: var(--space-2); }`
  to separate the media gallery from the text content above it.

## Verification

- `dotnet build` — 0 warnings, 0 errors.
- `dotnet test` — 1,143 pass (WASM manual-test phase: no new coded tests).
- Live verification (Playwright on Docker app):
  - Created a reply to andrew's note (which has a turkey screenshot attachment).
  - Navigated to the parent note's object detail page.
  - The reply card's "In reply to andrew" context card now shows:
    - The label "In reply to andrew"
    - The parent's text "test"
    - **The parent's turkey image** rendered via MediaGallery
  - No console errors.
