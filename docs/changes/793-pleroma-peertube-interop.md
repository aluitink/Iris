# 79.3 — Remaining interop gaps (Pleroma / PeerTube spot-check)

## Summary

Spot-checked the inbound wire format of **Pleroma** and **PeerTube** against Iris's polymorphic
ActivityStreams model. Found one genuine rendering gap (PeerTube `Video` objects don't render a
player) and fixed it. Pleroma inbound is clean.

## Findings

### Pleroma — inbound clean (no code change)

A Pleroma `Note` carries Pleroma-specific fields (`conversationId`, `emoji`, `toot`, `pleroma`,
`source`). None are typed on the Iris model. Verified via a round-trip test that **all** of them
survive serialize → store → deserialize through the ActivityStreams library's `[JsonExtensionData]`
bag. This confirms the Phase 56.2 finding: Pleroma inbound is clean.

### PeerTube — `Video` deserializes cleanly, but didn't render a player (fixed)

PeerTube posts are single **`Video`** objects whose `url` points at the media file and `preview` at
the thumbnail — the object's body IS the media, unlike an Iris/Mastodon note that carries media as
`attachment` entries.

- **Deserialization: clean.** The library maps the `"Video"` type to its concrete `Video` class;
  `name`/`content` map to the typed properties; `url`/`preview`/`duration` are preserved.
- **Rendering gap (fixed):** `ObjectView` rendered only the title + description text for a PeerTube
  video — no player — because `GetRichAttachments()` reads the `attachment` property (empty for a
  self-contained video object). The video's own `url` was never rendered.

## Changes

- **`IriExtensions.GetSelfMediaIri(this IObject?)`** (new public API, `src/Iris.Core/Identity/IriExtensions.cs`):
  returns the object's own `url` IRI when the object is a self-contained media type (`Video`,
  `Audio`, or `Image`) carrying a `url` (typed `Url` property or the `url` extension). Returns null
  for non-media objects (their media, if any, is in `attachment`).
- **`ObjectView.razor`**: when `SelfMediaSrc` is non-null, renders a `<video>` (or `<audio>` for
  `Audio` objects) player above the `MediaGallery`, honoring the sensitive-blur state.
- **`ObjectView.razor.cs`**: added `SelfMediaSrc` (rewritten same-origin via
  `RewriteMediaToSameOrigin`, so cross-origin PeerTube media routes through the media proxy),
  `IsVideoObject`, `IsAudioObject`.
- **`ForeignInteropRoundTripTests`** (new, `tests/Iris.Core.Tests`): 3 tests —
  1. PeerTube `Video` object deserializes to `Video`, `name`/`content` map, `GetSelfMediaIri()`
     resolves the `url`, and the round-trip preserves type + url.
  2. A plain `Note` reports no self-media IRI (guards against over-matching).
  3. A Pleroma `Note`'s extension fields (`conversationId`, `pleroma`, emoji) survive the round-trip.

## Decisions

- **Scope of self-media types:** `Video`, `Audio`, `Image` are treated as self-contained media. A
  bare `Image` object (not a note with an image attachment) is rare in the wild but handled for
  completeness. `Note`/`Article`/`Page` are never self-media (their media is in `attachment`).
- **No client-API change:** the fix reads the object's existing `url` property; no new fetch or
  endpoint is needed.
- **Same-origin rewrite:** PeerTube media is cross-origin, so it's routed through the existing media
  proxy (`RewriteMediaToSameOrigin`) to avoid CORS / mixed-content issues — consistent with how
  external `attachment` media is handled.

## Test counts

3 new tests (`ForeignInteropRoundTripTests`). Full suite green.
