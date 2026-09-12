# Phase 104 — Notification Improvements

## What was built

Improved the notification list (`/notifications`) with three changes:

1. **Accurate verbs for likes/boosts** — The "liked your post" / "boosted your post" verbs were previously shown for *all* likes/boosts in the inbox, even when the liked/boosted note was not actually the signed-in user's post (delivered via CC/fan-out). Now the verb checks the note's `attributedTo` (author) against the signed-in user's IRI, in addition to the IRI prefix check (which only works for local notes). When the author can't be confirmed, the verb falls back to "liked a post" / "boosted a post".

2. **Reply notifications show the parent post's author** — For `Create` activities that are replies, the verb now shows "replied to {author}'s post" when the parent note's author is known (via `inReplyTo.attributedTo` on the embedded parent note). Falls back to "replied to your post" (when the parent is the user's own note) or "replied" (when the author can't be determined).

3. **Card-style target rendering for embedded notes** — When the inbox object is an embedded `IObject` note (not a bare IRI link), the target renders as a compact note card: the note's author handle (linked to their actor page) above a content preview (linked to the note's object page). When the object is a bare `ILink` (the common case for local-to-local deliveries), the existing text-link fallback is used.

## Key changes

- **`NotificationRow.razor`**:
  - `VerbFor` method: `Like` and `Announce` cases now use `IsSelfNoteObject` (checks both IRI prefix and `attributedTo`) instead of unconditionally saying "your post". `Create` case uses `IsSelfNoteFromInReplyTo` + `ParentNoteAuthorLabel` for accurate reply verbs.
  - New helper methods: `IsSelfNoteObject(IObject)` (self-note check by IRI prefix + attributedTo), `IsSelfNoteFromInReplyTo(IObject)` (parent note self-check via embedded inReplyTo), `ParentNoteAuthorLabel(IObject)` (extracts the parent note's author handle).
  - Target rendering: new `else if` branch for `IObject` notes that renders `.notification-note-card` with author + content preview.

- **`app.css`** (client + server): new `.notification-note-card`, `.notification-note-author`, `.notification-note-content` styles.

## Test counts

- **0 new coded tests** (WASM manual-test policy — Phase 45+).
- Full fast suite: **1415 passed / 0 failed**.
- Live Playwright-verified:
  - "liked a post" verb shows for likes where the note's author can't be confirmed (correct fallback).
  - "sent you a follow request" unchanged.
  - Note targets render as text links (bare IRI inbox objects) — expected; card rendering activates when embedded notes are present.
  - 0 console errors.

## Decisions

- **`attributedTo`-based self-note detection** — The IRI prefix check (`IsSelfNote`) only works for local notes (IRI path under the actor's IRI). Remote notes have different IRI structures. Checking `attributedTo` (the note's author) against the signed-in user's IRI works for both local and remote notes, as long as the inbox stores the note with its `attributedTo` populated. The `attributedTo` check is a superset of the IRI prefix check.
- **Conservative verb fallback** — When the note's author can't be determined (bare IRI link, no embedded `attributedTo`), the verb says "liked a post" / "boosted a post" rather than guessing "your post". This avoids the false-positive "boosted your post" the user reported.
- **Card rendering is opt-in by data shape** — The note card only renders when the inbox object is an embedded `IObject` with content. This is the correct behavior: the inbox stores activities as-received, and the level of embedding depends on the sending instance. Local-to-local deliveries may store bare IRI references; the card rendering will activate when richer objects are available.
