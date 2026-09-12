# 96.1 — Reply count links to thread view

**Status:** COMPLETE
**Phase:** 96 — Utilize compatibility enhancements

## Objective

The user reported (PLAN.md Up Next 96): *"Utilize the various compatibility enhancements — see 74.*
there are various features we added to help support threads and other stuff, our UI should be
compatible with this and utilize it for created content."*

Phase 74 added several Mastodon wire-format fields to minted Notes, including `replies` (an
`OrderedCollection` pointer to `{noteId}/replies`). The UI now utilizes this field: the
EngagementBar's reply count is now a clickable link that navigates to the ObjectDetail page
(which shows the full thread: parent context + replies), instead of just displaying the number
inline.

## What was built

### `EngagementBar.razor`

Split the reply button into two elements:

1. **Reply icon** (a `<button>`) — links to `/compose?replyTo={noteIri}` to write a reply.
   Unchanged behavior.
2. **Reply count** (a `<a>`) — links to `/object?iri={noteIri}` to view the thread (the
   ObjectDetail page, which shows the parent context + replies section). Only rendered when
   `_replyCount > 0` (unchanged).

```razor
<!-- Before: single button with icon + count -->
<a class="engagement-btn" href="/compose?replyTo=..." title="Reply" aria-label="Reply">
    <svg>...</svg>
    @if (_replyCount > 0)
    {
        <span class="engagement-count">@_replyCount</span>
    }
</a>

<!-- After: icon button + separate count link -->
<a class="engagement-btn" href="/compose?replyTo=..." title="Reply" aria-label="Reply">
    <svg>...</svg>
</a>
@if (_replyCount > 0)
{
    <a class="engagement-btn engagement-count-link"
       href="/object?iri=..." title="View thread" aria-label="@_replyCount replies">
        <span class="engagement-count">@_replyCount</span>
    </a>
}
```

### `app.css`

Added styles for the new `.engagement-count-link` class:

```css
.engagement-count-link {
    text-decoration: none;
}

.engagement-count-link:hover .engagement-count {
    color: var(--accent);
    text-decoration: underline;
}
```

The count link has no underline by default (consistent with other engagement buttons) but
shows an accent color + underline on hover to indicate it's clickable.

## Verification

**Build:** `dotnet build -c Release` → 0 warnings / 0 errors (`TreatWarningsAsErrors` on).

**Full suite:** `dotnet test -c Release --filter "Category!=Slow"` → all tests pass (the only
intermittent failure is the pre-existing flaky federation test, unrelated to this change).

**Live Playwright verification:**
1. Logged in as `andrew`, posted a note ("Phase 96 test post: verifying reply count link to
   thread.").
2. Replied to the note ("This is a test reply to verify the reply count link.").
3. Navigated to the home timeline. The original post's engagement bar now shows:
   - A reply **icon** button (links to `/compose?replyTo=...`).
   - A reply **count** link showing "1" (links to `/object?iri=...`).
4. Clicked the reply count link. Navigated to the ObjectDetail page, which shows:
   - The post content.
   - The "Replies" section with the reply.
5. **0 console errors.**

## Notes / follow-ups

- This is a small, focused change that utilizes one of the Phase 74 compatibility fields
  (`replies`). Other Phase 74 fields (`sensitive`, `context`, `url`, `likes`, `shares`,
  `inReplyToAtomUri`, `contentMap`) are already handled or not directly relevant to the UI.
- The `likes`/`shares` fields could be utilized in a future slice by making the like/boost
  counts clickable to show "who liked this" / "who boosted this" (the collections), but that
  would require a new page (CollectionDetail) and is out of scope for this slice.
- UI-only (WASM), 0 new coded web tests per WASM manual-test policy. No new NuGet packages.
