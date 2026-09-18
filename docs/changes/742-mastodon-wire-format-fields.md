# 74.2 — Mastodon Wire-Format Fields (url, sensitive, replies)

**Phase:** 74 — Better Mastodon Compatibility
**Status:** Complete
**Date:** 2026-09-11

## Problem

Iris minted Notes without three fields Mastodon always emits:

| Field | Mastodon | Iris (before) |
|-------|----------|---------------|
| `url` | The status IRI (or HTML page URL) | Absent |
| `sensitive` | Always present (`false` or `true`) | Only present when `true` |
| `replies` | Empty `OrderedCollection` with the replies IRI | Absent |

Mastodon clients use `url` to link to a status, rely on the explicit
`sensitive` flag for content-warn rendering, and follow `replies.id` to
poll for thread replies. Missing fields caused: broken status links,
ambiguous sensitivity handling, and no thread-reply navigation.

## Solution

`MintActivityIds` (the server-side ID-minting step in the outbox
publish path) now calls a new `EnrichNoteForMastodon` helper on every
minted embedded object:

- **`url`** — set to the note's own IRI (`{base}/ap/v1/u/{handle}/notes/{ulid}`).
  For AP interop the object IRI is the canonical resolvable address.
- **`sensitive`** — always emitted in `ExtensionData`. When the client
  already set `sensitive: true` (content warning), it is preserved;
  otherwise `false` is written. This matches Mastodon's "always present"
  convention.
- **`replies`** — an `OrderedCollection` whose `id` is
  `{noteId}/replies`. The server already serves this collection
  (interaction-collection endpoint), so this is a pure pointer that
  lets Mastodon clients navigate the thread.

All three fields are additive: they do not alter the existing Iris
extension terms (`iris:likedCount`, `iris:sharedCount`,
`iris:repliedCount`), the note's content, audience, or tags.

## Files Changed

- `src/Iris.Server/ActivityPubServerExtensions.cs`
  - `MintActivityIds`: calls `EnrichNoteForMastodon` on minted objects.
  - `EnrichNoteForMastodon` (new private static method): sets `Url`,
    ensures `sensitive` in `ExtensionData`, adds `Replies`
    `OrderedCollection`.

## Verification

- `dotnet build`: 0 warnings, 0 errors.
- `dotnet test`: 1665 passed, 0 failed, 17 skipped.
- Live-verified via Playwright (Docker app, `iris.luit.ink:8088`):
  - Public post: `url` = note IRI, `sensitive: false`, `replies` =
    `OrderedCollection` with `{noteId}/replies` IRI.
  - Sensitive post (CW checked): `sensitive: true` (preserved), `url`
    and `replies` present.
  - `GET {noteId}/replies` returns the matching `OrderedCollection`
    (`totalItems: 0`).
