# 74.3 — atomUri, context/conversation, contentMap on Minted Notes

**Phase:** 74 — Better Mastodon Compatibility
**Status:** Complete
**Date:** 2026-09-11

## Problem

Three more Mastodon-always-present fields were missing from Iris-minted
Notes:

| Field | Mastodon | Iris (before) |
|-------|----------|---------------|
| `atomUri` | The AP IRI (same as `url` for status objects) | Absent |
| `context` / `conversation` | The thread context IRI (parent's IRI for replies, own IRI for top-level) | Absent |
| `contentMap` | Locale-keyed content map (`{"en": "..."}`) | Absent |

Mastodon/Pleroma clients use `atomUri` to distinguish the AP address from
the HTML page URL, `context`/`conversation` for thread grouping, and
`contentMap` for localized rendering.

## Solution

`EnrichNoteForMastodon` (added in 74.2) extended with three additional
fields:

- **`atomUri`** — set to the note's own IRI (same as `url` for Iris, since
  the AP endpoint is the canonical address). Written via ExtensionData
  (Rule 6 — the library does not model it).
- **`context` / `conversation`** — for replies, the parent note's IRI
  (derived from `inReplyTo`'s first entry); for top-level posts, the note's
  own IRI (degenerate single-item context). Both fields are set to the same
  value (Mastodon convention). Written via ExtensionData (Rule 6 — the
  library models `Context` as `IEnumerable<IObjectOrLink>`, a multi-valued
  property, not a single IRI link).
- **`contentMap`** — a single `{"en": <content>}` entry matching the note's
  content. Written as a typed property (the library models `ContentMap` as
  `IEnumerable<IDictionary<string, string>>`).

All fields are additive and do not alter existing Iris extension terms or
the note's content/audience/tags.

## Design Decision

**Context derivation for replies:** Mastodon uses a generated
`/contexts/{rootId}-{statusId}` IRI for thread grouping. Iris does not
maintain a separate context registry; the closest equivalent is the parent
note's IRI (the thread the reply belongs to). This is sufficient for
Mastodon clients (they use `context` to group replies under a thread) and
avoids introducing a new IRI-minting scheme. If Iris later gains a
first-class thread-context concept, this can be updated.

## Files Changed

- `src/Iris.Server/ActivityPubServerExtensions.cs`
  - `EnrichNoteForMastodon`: added `atomUri`, `context`, `conversation`,
    `contentMap` enrichment logic.

## Verification

- `dotnet build`: 0 warnings, 0 errors.
- `dotnet test`: 1665 passed, 0 failed, 17 skipped.
- Live-verified via Playwright (Docker app, `iris.luit.ink:8088`):
  - Top-level post: `atomUri` = own IRI, `context`/`conversation` = own IRI,
    `contentMap` = `{"en": <content>}`.
  - Reply: `atomUri` = reply's own IRI, `context`/`conversation` = parent's
    IRI (matches `inReplyTo`), `contentMap` = `{"en": <reply content>}`.
  - Zero console errors.
