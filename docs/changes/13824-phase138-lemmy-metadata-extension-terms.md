# 138.24 — Lemmy-specific metadata inventory + extension-term design

## What changed

Designed and added five new `iris:`-namespaced extension terms for Lemmy-specific metadata fields
that Iris stores in `ExtensionData` but never surfaced. Also fixed a pre-existing bug in the
`GetBool` client helper and added the previously-missing terms to the namespace document.

### Inventory

The object store is **lossless** for Lemmy extension fields — `source`, `sensitive`,
`postingRestrictedToMods`, `featured`, `language`, `published`, `endpoints` all round-trip
through the store intact (verified by the 135.1b(2) test). The gap was not data loss but
**missing term definitions**: the fields sat inert in `ExtensionData` with no `iris:` term, no
namespace declaration, no client reader, and no UI treatment.

### New extension terms

| Term | Wire key | Type | Where rendered | Purpose |
|---|---|---|---|---|
| `CommunityNsfw` | `{ns}communityNsfw` | `boolean` | Community (Group) doc | Source community is NSFW/sensitive; clients should CW-gate all content |
| `Locked` | `{ns}locked` | `boolean` | Content object doc | Source post/comment is locked; disable reply composer |
| `Featured` | `{ns}featured` | `boolean` | Content object doc | Object is pinned/featured in source community |
| `Language` | `{ns}language` | `string` | Content object doc | ISO-639 language code of the content |
| `PostingRestrictedToMods` | `{ns}postingRestrictedToMods` | `boolean` | Community (Group) doc | Source community restricts posting to moderators |

### Pre-existing terms now declared in namespace doc

`IsDisliked`, `DislikedCount`, `Score`, `DislikeActivityIri`, and `RemovedBy` (added in
138.18/138.23) were missing from `BuildNamespaceDocument`. Added them alongside the new terms.

### `GetBool` bug fix

The `GetBool` helper in `IrisDocumentExtensions` returned `true` for **both** JSON `true` and
JSON `false` (it checked `ValueKind is True or False` instead of `ValueKind == True`). This was
latent: the server never writes `isLiked: false` or `isShared: false` (it omits the term
instead), so the bug was invisible. Fixed to `ValueKind == True` so the new terms (which *do*
write explicit `false`) read correctly. No behavior change for existing callers.

## Files changed

| File | Change |
|---|---|
| `src/Iris.Core/IrisExtensionTerms.cs` | Added 5 new term constants with full XML-doc. |
| `src/Iris.Server/ActivityPubServerExtensions.cs` | Added 10 terms (5 new + 5 previously-missing) to `BuildNamespaceDocument`. |
| `src/Iris.Client/IrisDocumentExtensions.cs` | Added 6 client readers (`GetRemovedBy`, `GetCommunityNsfw`, `GetLocked`, `GetFeatured`, `GetLanguage`, `GetPostingRestrictedToMods`); added `GetString` helper; fixed `GetBool` bug. |
| `tests/Iris.Server.Tests/LemmyExtensionTermsIntegrationTests.cs` | 7 integration tests (new). |

## Tests

7 integration tests in `LemmyExtensionTermsIntegrationTests`:

1. **`ClientReader_CommunityNsfw_ReadsBoolFromExtensionData`** — null/true/false round-trip.
2. **`ClientReader_Locked_ReadsBoolFromExtensionData`** — null/true/false round-trip.
3. **`ClientReader_Featured_ReadsBoolFromExtensionData`** — null/true/false round-trip.
4. **`ClientReader_Language_ReadsStringFromExtensionData`** — null/"de"/"en" round-trip.
5. **`ClientReader_PostingRestrictedToMods_ReadsBoolFromExtensionData`** — null/true/false round-trip.
6. **`ClientReader_RemovedBy_ReadsIriFromExtensionData`** — null/IRI round-trip.
7. **`AllNewTerms_HaveDistinctWireKeys`** — no term-name collisions.

All 7 pass. Full suite: 1264 passed, 1 known flaky (passes in isolation).

## Design notes

- **Term naming.** `CommunityNsfw` (not `Nsfw`) to avoid collision with the object-level
  `sensitive` field (which is a core AP term, not an `iris:` extension). The community-level flag
  is a distinct concept: it applies to *all* content from the community, not just individual
  posts. `Locked` (not `ThreadLocked`) because the wire key should be short and the
  `iris:`-namespace already disambiguates. `Featured` (not `IsFeatured`) to match the
  Lemmy wire-field name and the existing `isLiked`/`isShared` convention (which use `is`
  prefix for per-requester state; `featured` is per-object, not per-requester).
- **Rendering deferred to 138.25.** This slice designs the terms and adds the client readers.
  The server-side rendering (writing the terms into the object/community document builders,
  mirroring `EnrichNoteForMastodon`) and the UI treatment (disabled composer for `locked`,
  pinned indicator for `featured`, CW for `communityNsfw`) are 138.25's scope.
- **`GetBool` fix is backward-compatible.** The server never writes explicit `false` for
  `isLiked`/`isShared` (it omits the term), so the fix changes no observable behavior for
  existing callers. The new terms *do* write explicit `false` (e.g. `locked: false` on an
  unlocked post), which requires the corrected reader.
