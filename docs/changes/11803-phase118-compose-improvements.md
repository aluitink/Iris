# 118.3 — Compose improvements: fix the @/# autocomplete trigger + surface known hashtags

## Summary

The compose page's `@mention` / `#hashtag` auto-complete **control** already existed (built across
54.14 / 71.5): an in-progress-token detector, a debounced popover with candidates, mention
resolution (known actors + live instance search), hashtag detection, and `tag`-array population on
every post path. But the popover **never actually opened** — a pre-existing bug — and the `#`
autocomplete offered only the typed token with no way to discover tags the user already uses.

This phase (1) fixes the trigger so the dropdown opens as the user types, for **both** `@` and `#`,
and (2) gives the `#` autocomplete a real candidate source: the signed-in actor's **previously-used
hashtags**, filtered by the typed token (mirroring how the `@` autocomplete defaults to the actor's
follows).

## Context

118.3's plan text asks for "a control to support @mention and #hashtag; something that shows an
auto-complete dropdown and selection of known actors" plus "populat[e] tags and mentions" on the
outbox. The detection, resolution, tag-population, and Mastodon-style rendering were all present, so
the remaining gaps were:

1. **The popover never rendered.** The content textarea used `@onkeyup="OnContentInput"`. Blazor's
   `@onkeyup` on a `<textarea>` did not reliably invoke the handler, and the two-way `@bind="Content"`
   on the same element registers its own generated input handler that suppresses the explicit
   autocomplete trigger. Result: typing `@` or `#` produced no dropdown (confirmed live — the char
   counter stayed at `0/500` while text was present, i.e. the handler was not running).
2. **The `#` autocomplete had no discovery source.** With no instance hashtag directory, it offered
   only the typed `#tag` as its sole candidate (a deliberate earlier choice), so a user could not see
   tags they had used before.

## Changes

### Compose.razor

- **Textarea trigger**: replaced `@bind="Content" @onkeyup="OnContentInput"` with an explicit
  `@oninput="OnContentInputEvent"`. `@oninput` fires on every value change (keystroke, paste, IME),
  and — with the two-way binding removed — nothing else registers a competing input handler on the
  element.
- **`OnContentInputEvent(ChangeEventArgs)`** (new): reads the freshly-typed value into `Content`
  (`e.Value`), then calls the existing `OnContentInput()` (token detection + debounced popover
  population). `Content` is now updated manually rather than via `@bind`; all other readers (char
  counter, the post button's empty-guard, every post path) read the same field, so they see the fresh
  value.
- **`_knownHashtags`** (new field): the signed-in actor's previously-used hashtags, loaded lazily and
  cached for the page's lifetime — mirroring `_knownActors`.
- **`GetHashtagCandidatesAsync(query, ct)`** (new): replaces the inline "typed tag only" branch in
  `RunAutocompleteAsync`. An empty token returns the known hashtags; a non-empty token filters them
  (ordinal, case-insensitive `Contains`). The typed `#tag` is **always** appended as a confirmable
  candidate when it matches no known hashtag, so a brand-new tag can still be inserted as a canonical
  token.
- **`LoadKnownHashtagsAsync(ct)`** (new): a **bounded** read of the actor's own outbox
  (`client.GetCollectionItemsAsync(actorId.OutboxOf(), new CollectionQuery(Limit: HashtagSourceLimit))`),
  extracting the `Hashtag` `tag` entries from each authored object (`obj.GetHashtagTags()`). An outbox
  item is a `Create`/`Update` wrapping the object, so the authored object is read from
  `activity.Object.FirstOrDefault()` (falling back to the item itself). Tag names are normalized (strip
  leading `#`, trim). Non-fatal on failure (an empty set simply means the typed token is the only
  candidate) and `OperationCanceledException` is swallowed (a superseded keystroke).
- **`HashtagSourceLimit = 40`** (new const): the cap on recent outbox objects scanned to build the
  known-hashtag list — a small, cheap first-page read.

No CSS changes were needed — the `.compose-autocomplete*` styles were already in place (both
`wwwroot/css/app.css` copies).

## Verification

- `dotnet build Iris.slnx` — 0 warnings / 0 errors. `dotnet test Iris.slnx` — all suites green
  (the only intermittent failure is the known load-flaky
  `FollowEdgeConvergenceIntegrationTests`, which passes in isolation).
- Live (Playwright, fresh browser with caching disabled, signed in as `andrew`):
  - Typing `#` in the compose box opens the popover listing the actor's own hashtags
    (`#1183check`, `#smartphone`, `#openhardware`, `#freesoftware`, …).
  - Typing `#1183` filters to `#1183check` (known) + `#1183` (the typed token).
  - Typing `@al` opens the mention popover (`alice`, `lowqualityfacts`, `piefed-test`).
  - Selecting a candidate splices it into the text and closes the popover.
  - The char counter now tracks typed text (confirming `@oninput` fires and `Content` updates).
- Tag population was confirmed earlier: a posted note carries `#1183check` in its `tag` array with the
  local search href (`/search?q=%231183check`).

## Open / follow-ups

- The known-hashtag list is bounded to the actor's most recent 40 outbox objects; a dedicated instance
  hashtag directory (if ever added) would replace this source.
- The `@` autocomplete for an **empty** token shows the actor's follows; an actor with no follows gets
  an empty list (correct — no known actors), and live search still runs for a non-empty token.
