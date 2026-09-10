# 71.1–71.5 — Note card + compose polish (implementation)

Phase 71 (Note card + compose polish) is a distillation of a review of the feed/outbox
note card (`ObjectView.razor`) and the compose page (`Compose.razor`). This change
implements five of the six slices (71.1–71.5); 71.6 (review remote user content with
attachments) is a live-verification pass and is not code.

**No new coded tests** (WASM manual-test policy) — verified by build (0 warn/0 err)
and the existing web test suite (63/63 green).

## 71.1 — Note card renders from all available content

The feed/outbox `Create` branch of `ObjectView.razor` previously showed only the note's
**text** (`@ActivityContent`) + a `MediaGallery` for image attachments. It did not
render hashtags, mentions, audience, in-reply-to, emojis, poll, or article metadata —
the way the `IObject` branch (object-detail view) does.

**Change:** the `Create` branch now builds the card from the full embedded object.
Activity-scoped computed properties were added to `ObjectView.razor.cs` that read the
embedded content object (not the activity wrapper, which carries none of these fields):

| Property | Source |
|---|---|
| `ActivityContentObject` | `ActivityEmbeddedObject ?? Obj` |
| `ActivityParentIri` | `ActivityEmbeddedObject?.GetParentIri()` |
| `ActivityAudienceIris` | `ActivityEmbeddedObject?.GetAudienceIris()` |
| `ActivityMentionIris` | `ActivityEmbeddedObject?.GetMentionIris()` |
| `ActivityHashtagTags` | `ActivityEmbeddedObject?.GetHashtagTags()` |
| `ActivityUpdated` | `ActivityEmbeddedObject?.GetUpdated()` |
| `ActivityIsSensitive` | `ActivityEmbeddedObject?.IsSensitive()` |
| `ActivitySummary` | `ActivityEmbeddedObject?.GetSummary()` |

The `Create` branch markup now renders (in order): content (with sensitive-blur, see
71.4), in-reply-to, audience, updated timestamp, article duration, article language,
mentions, hashtags, custom emojis, media attachments (all types via `MediaGallery`),
poll, and finally the engagement bar (see 71.3).

## 71.3 — Interaction bar at the bottom of the post

The `EngagementBar` previously sat between the content and the `MediaGallery` in the
`Create` branch, so media rendered *below* the bar.

**Change:** the `EngagementBar` is now rendered **last** in the `Create` branch (after
all content, metadata, attachments, and poll), so the bar is the post's footer. The
bar is shown when `IsContentCreate` (the embedded object is a `Note` or `Article`).

## 71.4 — Sensitive content blurred until reveal

A `sensitive` note previously rendered a notice + "Show" button and the content was
only *hidden* (absent from the DOM until revealed).

**Change:** the content is now rendered **blurred** (CSS `filter: blur(8px)`,
`user-select: none`, `pointer-events: none`) behind the notice, so the shape of the
post is visible. The blur is removed on reveal (the click-to-reveal interaction is
kept). This applies to both the `Create` branch (new `ActivityRevealed` state) and
the `IObject` branch (existing `Revealed` state, now drives a CSS class instead of a
conditional render).

New CSS class: `.object-content--blurred`.

## 71.5 — Compose @handle autocomplete shows known actors

The `@handle` autocomplete dropdown previously queried instance search only, offering
nothing for an empty token or unknown handles.

**Change:**

1. **`UiContext.GetFollowingActorIrisAsync()`** — new public method that resolves the
   signed-in actor's following collection (the IRIs of all followed actors), cached in
   the existing following-set cache (2-min TTL). `IsFollowingAsync` now delegates to
   this method (the underlying walk is unchanged; the method now also returns the
   ordered IRI list, not just a membership check).

2. **`Compose.razor`** — the mention autocomplete (`GetMentionCandidatesAsync`,
   replacing `SearchMentionCandidatesAsync`) now:
   - For an **empty token** (the user just typed `@`): shows the signed-in actor's
     **follows** as the default candidate list (the accounts the user actually knows).
     Each followed actor's document is fetched via `UiContext.GetActorAsync` (coalesced,
     5-min TTL cache) to get the display name + handle.
   - For a **non-empty token**: filters the follows by the typed query, then
     supplements with live instance-search results (de-duplicated by handle, known
     actors first).
   - The `_knownActors` list is cached per page instance (loaded once, reused across
     keystrokes).

`UiContext` is now injected into `Compose.razor`.

## Files changed

| File | Change |
|---|---|
| `apps/Iris.Web.Client/Components/ObjectView.razor` | Rebuilt `Create` branch (71.1, 71.3, 71.4); sensitive-blur in `IObject` branch (71.4) |
| `apps/Iris.Web.Client/Components/ObjectView.razor.cs` | Activity-scoped computed properties (71.1); `ActivityRevealed` field (71.4) |
| `apps/Iris.Web.Client/Components/Pages/Compose.razor` | `@using Iris.Web.Client.Ui`; `UiContext` injection; `_knownActors` field; `GetMentionCandidatesAsync` + `LoadKnownActorsAsync` (71.5) |
| `apps/Iris.Web.Client/Ui/UiContext.cs` | `FollowingEntry` record now carries `List<Iri>`; `GetFollowingActorIrisAsync` (71.5); `IsFollowingAsync` delegates |
| `apps/Iris.Web.Client/wwwroot/css/app.css` | `.object-content--blurred` (71.4) |

## Design decisions

- **`ActivityContentObject` = `ActivityEmbeddedObject ?? Obj`:** the `Create` branch is
  only entered when `Item is Create`, so `Obj` is the activity and `ActivityEmbeddedObject`
  is the embedded Note/Article. The `?? Obj` fallback is defensive (the `Create` branch
  is never reached with `Obj` as the content object directly).
- **Sensitive-blur via CSS class, not conditional render:** the content is always in
  the DOM (blurred), so the "Show"/"Hide" button toggles a CSS class rather than
  mounting/unmounting the content element. This is simpler and avoids a layout shift
  on reveal.
- **`_knownActors` cached per page instance:** the follows list is loaded once when the
  compose page is open and reused across keystrokes. A follow/unfollow elsewhere in the
  app invalidates the `UiContext` following cache, but the compose page's own
  `_knownActors` snapshot is not invalidated (acceptable: the compose page is a
  short-lived surface and the list is only used for autocomplete suggestions).
- **Search supplements follows, not replaces them:** when the user types a query, the
  follows are filtered first (known actors first in the list), then live search results
  that don't match a known handle are appended. This ensures the user's own contacts
  are always visible even if the instance search is slow or incomplete.

## Residual (not fixed this pass)

- **71.2 (attachments inline with text):** documents already render after the text in
  the `MediaGallery` (which is now positioned after all content in the `Create` branch
  per 71.1). The document card style (icon + name + open-in-new-tab) is a link-card
  rather than a fully inline document preview; a richer inline rendering (e.g. a PDF
  preview) is deferred as a follow-up.
- **71.6 (remote user content with attachments):** live-verification pass (no code
  change expected — the render path is the same `ObjectView` component).
- **Playwright verification:** the compose textarea is not drivable via MCP Playwright
  (Blazor `@bind` never registers synthetic input — a known automation limitation from
  Phase 70.3). The autocomplete (71.5) and sensitive-blur (71.4) interactions should be
  verified by a human or via DOM inspection.
