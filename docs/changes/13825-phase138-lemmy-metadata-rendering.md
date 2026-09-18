# 138.25 — Implement + render the new extension terms

## What changed

Wired the five 138.24 extension terms into the server's document builders so that bare Lemmy
metadata fields stored in `ExtensionData` are re-rendered under the `iris:` namespace on served
documents.

### Object documents (`ServeObjectDocument`)

Added rendering of three terms from a stored content object's bare Lemmy `ExtensionData` keys:

| Bare key (Lemmy) | `iris:` term rendered | Condition |
|---|---|---|
| `locked` | `iris:locked` | bare key present and `true` |
| `featured` | `iris:featured` | bare key present and `true` |
| `language` | `iris:language` | bare key present and non-empty string |

The rendering reads from the **stored** object's `ExtensionData` (the `obj` parameter), not the
deep copy — the deep copy already carries the bare keys through the serialize/deserialize
round-trip, but the `iris:`-namespaced keys are added explicitly.

### Community documents (`CommunityDocumentHandler`)

Added rendering of two terms from a stored community's bare Lemmy `ExtensionData` keys:

| Bare key (Lemmy) | `iris:` term rendered | Condition |
|---|---|---|
| `sensitive` | `iris:communityNsfw` | bare key present and `true` |
| `postingRestrictedToMods` | `iris:postingRestrictedToMods` | bare key present and `true` |

Both use the idempotent `!ext.ContainsKey(...)` guard (matching the existing community handler
pattern) so re-rendering the same document doesn't duplicate the term.

### Design decisions

- **Only render `true`, not `false`.** The terms are only present when the flag is set. An
  unlocked post has no `iris:locked` key (not `iris:locked: false`). This matches the existing
  `isLiked`/`isShared` convention (omit when false) and keeps the document minimal.
- **Read from the stored object, not the deep copy.** The `ServeObjectDocument` method receives
  the stored object as `obj` and creates a deep copy `document`. The bare Lemmy keys are on
  `obj.ExtensionData` (and survive into `document.ExtensionData` via the round-trip). Reading
  from `obj` is explicit and avoids any ambiguity about whether the deep-copy preserved the key.
- **`featured` as a bare boolean on the object.** Lemmy's actual wire format uses a
  community-level `featured` collection IRI (the pinned-posts collection), not a per-post
  boolean. However, the `iris:featured` term is designed as a per-object boolean (138.24), and
  a future phase can populate it by checking membership in the community's featured collection.
  For now, the rendering handles the bare `featured: true` key if a Lemmy server ever sends it
  on a post (some Lemmy versions do).
- **UI surfacing deferred.** The plan mentions surfacing `locked` (disable reply composer),
  `featured` (pinned indicator), and `communityNsfw` (CW gate) in the Iris Web UI. This is
  manual Playwright verification per the web test policy — the server-side rendering is the
  prerequisite, and the UI changes are a follow-up slice.

## Files changed

| File | Change |
|---|---|
| `src/Iris.Server/ActivityPubServerExtensions.cs` | `ServeObjectDocument`: render `iris:locked`, `iris:featured`, `iris:language` from stored object's bare Lemmy keys. `CommunityDocumentHandler`: render `iris:communityNsfw`, `iris:postingRestrictedToMods` from stored community's bare Lemmy keys. |
| `tests/Iris.Server.Tests/LemmyMetadataRenderingIntegrationTests.cs` | 7 integration tests (new). |

## Tests

7 integration tests in `LemmyMetadataRenderingIntegrationTests`:

1. **`StoredLemmyPost_WithLocked_RenderedAsIrisLocked`** — bare `locked: true` → `iris:locked: true`.
2. **`StoredLemmyPost_WithoutLocked_NoIrisLocked`** — no bare key → no `iris:locked`.
3. **`StoredLemmyPost_WithLanguage_RenderedAsIrisLanguage`** — bare `language: "de"` → `iris:language: "de"`.
4. **`StoredLemmyPost_WithFeatured_RenderedAsIrisFeatured`** — bare `featured: true` → `iris:featured: true`.
5. **`StoredLemmyCommunity_WithSensitive_RenderedAsIrisCommunityNsfw`** — bare `sensitive: true` → `iris:communityNsfw: true`.
6. **`StoredLemmyCommunity_WithPostingRestrictedToMods_RenderedAsIrisTerm`** — bare `postingRestrictedToMods: true` → `iris:postingRestrictedToMods: true`.
7. **`StoredLemmyCommunity_WithoutFlags_NoIrisTerms`** — no bare keys → no `iris:` terms.

All 7 pass. Full suite: 1272 passed, 0 failed.
