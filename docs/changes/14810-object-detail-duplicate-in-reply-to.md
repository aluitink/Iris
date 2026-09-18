# 148.10 — Object detail: drop the duplicate "In reply to" context

> 2026-09-16 · Slice 148.10 · Phase 148 (UI follow-up)

## What was built

The object detail page (`/object?iri=…`) showed the parent context twice for a
reply: a dedicated thread-context card ("In reply to [author]" + parent content,
optionally + grandparent) rendered *above* the main object card, **and** the main
object card's own inline "In reply to" context (the `ObjectView` fetched-parent
card, Phase 90.1/117.6) rendered *inside* that same card. The two carried the
same parent, so the reader saw the identical "In reply to …" block twice on one
screen.

The fix adds a `SuppressParentContext` parameter to `ObjectView`. When set, the
component skips its inline parent-context card (both the `Create`-branch and the
bare-object branch, including the one-line "in reply to" fallback link). The
object detail page now passes `SuppressParentContext="true"` to its main
`ObjectView`, so the dedicated thread-context card is the single source of the
"In reply to" context. Every other `ObjectView` use (feed, profile outbox,
directory, replies tree, search) is unchanged and still shows its inline context.

## Key types & files

- `apps/Iris.Web.Client/Components/ObjectView.razor` — added the
  `[Parameter] bool SuppressParentContext`; guarded both parent-context render
  sites (`ActivityParentIri` in the `Create` branch, `ParentIri` in the bare-object
  branch) with `&& !SuppressParentContext`.
- `apps/Iris.Web.Client/Components/Pages/ObjectDetail.razor` — main card now
  `<ObjectView Item="ObjectDoc" SuppressParentContext="true" />`; the thread-context
  card above it (`LoadParentAsync` → `ThreadContext`) is unchanged and remains the
  single "In reply to" context.

## Tests

No new coded tests (UI-rendering change; the loop protocol for this phase is
Playwright-driven, no new coded tests). Build verified: `Iris.Web.Client`
compiles clean (0 warnings, 0 errors). Re-verify from a clean entry: open a reply
object's detail page and confirm the "In reply to" context appears exactly once
(the thread-context card above the main card), with no second "In reply to" block
inside the main card.

## Decisions

- **Suppress on the detail page rather than removing `ObjectView`'s context.** The
  inline context is the intended, primary treatment everywhere else (feed, outbox,
  replies tree, Phase 90.1/117.6). Only the detail page renders its *own* dedicated
  thread-context card, so only there is the inline one redundant. A parameter keeps
  `ObjectView` self-contained for its many other callers.
- **The detail page keeps the dedicated thread-context card.** It is richer than
  `ObjectView`'s single-parent card: it walks up to the grandparent (two-level
  context) and renders each ancestor as a separate "In reply to [author]" block with
  a separator. The inline card would show at most the immediate parent.
