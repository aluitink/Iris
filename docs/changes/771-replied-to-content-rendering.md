# 77.1 — Replied-to content rendering

## What was done

Phase 77 (Replied-to items): the replied-to content is now rendered with full
HTML/Markdown support instead of being shown as a raw IRI or a plain-text
truncation.

### ObjectDetail page (`/object?iri=...`)

The "In reply to" context card at the top of the object detail page now renders
the parent's content with the same rendering pipeline as the main object:
pre-rendered HTML is emitted verbatim, Markdown/plain text is run through
`Iris.Core.Rendering.Markdown.ToHtml` (HTML-escaped first, then Markdown
transforms applied). Previously the parent was shown as a 200-char plain-text
truncation with all HTML tags stripped via regex.

Added CSS for images, `<pre>` blocks, and `<blockquote>` elements inside the
parent context section so they display properly.

### ObjectView (feed cards)

When a reply is shown in a feed (home timeline, profile outbox, etc.), the
"in reply to" line now shows a 120-char plain-text preview of the parent's
content instead of the raw IRI. The parent is fetched in `OnInitializedAsync`
via `IActivityPubClient.GetObjectAsync`. If the fetch fails (network error,
404, etc.), the raw IRI is shown as before — the failure is non-fatal.

This adds one extra GET per reply card on initial render. Replies that are
not visible (scrolled off-screen) are not fetched because Blazor only
initializes components that are rendered.

## Key changes

- `ObjectDetail.razor`: `ParentPreview` (string, 200-char plain text) →
  `ParentRenderedContent` (MarkupString, full HTML/Markdown rendering).
- `ObjectView.razor.cs`: new `OnInitializedAsync` that fetches the parent's
  content when `ActivityParentIri` is set and stores a 120-char plain-text
  preview in `_parentPreview`.
- `ObjectView.razor`: the "in reply to" link text is now
  `_parentPreview ?? parent.Value`.
- `app.css`: `.object-parent-content img/pre/blockquote` styles.

## 77.2 — Thread context (2 levels of parent chain)

The ObjectDetail page now shows up to 2 levels of parent context (the immediate
parent + grandparent) instead of just the immediate parent. Each ancestor is
rendered with its author and content, separated by a subtle divider.

- `LoadParentAsync` now fetches the parent, then checks if the parent has its
  own parent and fetches that too (max 2 levels).
- Replaced single `ParentDoc`/`ParentHref`/`ParentAuthor`/`ParentRenderedContent`
  with a `ThreadContext` list + `AncestorAuthor`/`RenderAncestorContent` helpers
  that work for any ancestor in the chain.
- Added `.object-parent-separator` CSS for the divider between levels.

## Test counts

Full suite: 1666 passed, 0 failed, 17 skipped (1 known-flaky Server test in
full-suite run, green in isolation).
