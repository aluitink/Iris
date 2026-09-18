# 132.3 — Nested reply threading in the object detail view

**Date:** 2026-09-13
**Slice:** 132.3 (PLAN "Up Next" — extend the 132.2 Replies tab from a flat list to a nested, expandable/collapsible thread)
**Commits:** `adbf9a6` — `feat(web): nested reply threading in object detail (Phase 132.3)`

## Problem

Phase 132.2 surfaced the object's interactions as a Replies / Likes / Shares tab bar, but the **Replies tab was still a flat list** of the object's *direct* replies. A reply-to-a-reply (a reply whose `inReplyTo` points at another reply rather than the root) rendered as a top-level item in the list, flattened alongside its siblings. The reader could not see the conversation's *thread structure* — which reply answered which — and had no way to collapse a busy reply's own replies to focus on the top-level exchange. This was called out as the follow-up in the 132.2 change doc ("*Nested / expandable reply threading* … is a larger follow-up → 132.3").

## Data model (why a recursive walk is required)

The server's per-object `/replies` collection serves **direct replies only**: `RecordReplyAsync(parent, child)` (in `ActivityPubServerExtensions`) records one edge per `inReplyTo` link, with `parent` = the object the reply's `inReplyTo` points to. So `GetRepliesAsync(rootIri)` returns only the replies whose `inReplyTo` is the *root*. A reply's own replies live under *that reply's* `/replies` collection. Building a thread therefore requires walking each reply's `/replies` recursively — there is no single endpoint that returns the full subtree.

## Change

The Replies tab now renders a **nested thread** (a `ReplyNode` tree) instead of a flat `ul.object-list`:

- **`ReplyThreadNode`** (new, `apps/Iris.Web.Client/Components/ReplyThreadNode.cs`) — a `record` holding one reply: `Iri`, its resolved `IObject?`, and `List<ReplyThreadNode> Children`. `ChildCount` computes the total replies in the subtree (own children + their subtrees); `HasReplies` is `ChildCount > 0` and gates the "N replies" affordance.
- **`ReplyNode.razor`** (new recursive component) — renders one node: its `ObjectView` card, a **"Show/Hide N replies"** toggle (shown only when `HasReplies`), and its nested children (indented, inside a `role="group"` container). **Collapsed by default** (`Expanded` starts `false`), per the slice spec. The toggle flips `Expanded` and re-renders; the child container is only in the DOM while expanded.
- **`ObjectDetail.razor`** — the Replies tab panel now renders `<div class="reply-thread">` of `<ReplyNode>` roots (replacing the flat `<ul class="object-list">`). New `BuildReplyTreeAsync` + `BuildSubtreeAsync` + `LoadResolvedRepliesAsync` build the tree: starting from the already-loaded flat `Replies` (the root's direct replies), it recursively fetches each reply's own `/replies` and resolves each child via `GetObjectAsync`, until the depth limit. The existing "Show more replies" infinite-scroll and "No replies yet." empty state are preserved.

### Bounding the walk

The recursive walk is bounded two ways to keep the (potentially large) number of collection fetches from exploding on long threads:

- **Depth** — `MaxReplyDepth = 3` (a constant). The root's direct replies are depth 1; a node's children are fetched only while `depth < MaxReplyDepth`. A reply at the depth limit renders with no children (its replies were not walked, so it shows no "N replies" affordance for the un-walked level).
- **Breadth** — each node's `/replies` fetch is limited to **20 items** (`CollectionQuery { Limit = 20 }`), the same page size as the root's load.

The walk is best-effort: an unresolvable reply (404, network) is skipped, and a failed collection fetch leaves the node with the children it did manage to load. A failure never blanks the whole thread.

### CSS

New styles in **both** `app.css` copies (kept in sync per the project convention):

- `.reply-thread` — the vertical list of top-level reply nodes.
- `.reply-node` / `.reply-node-card` — one reply (the `ObjectView` card, same surface/border as the old flat list items).
- `.reply-node-toggle` — the "Show/Hide N replies" button (accent color, underline on hover).
- `.reply-node-children` — the nested container, **indented (`padding-left`) with a left rule (`border-left`) to show the thread lineage**.

## New / changed API

- `ReplyThreadNode` (new `record`) — `Iri`, `Object`, `Children`, `ChildCount`, `HasReplies`.
- `ReplyNode.razor` (new Blazor component) — param `Node` (`required ReplyThreadNode`); renders the node + its expand/collapse subtree.
- `ObjectDetail.razor`: `ReplyTree` (`List<ReplyThreadNode>`), `MaxReplyDepth` (`const int = 3`), `BuildReplyTreeAsync`, `BuildSubtreeAsync(List<IObject?>, int)`, `LoadResolvedRepliesAsync(Iri)`. `LoadRepliesAsync` / `LoadMoreRepliesAsync` now call `BuildReplyTreeAsync` after loading so the tree tracks the flat list (including "show more").

## Decision (recorded per the loop's "Open Questions" default)

**Collapse-by-default at every level, with a bounded walk (depth 3, 20/node).** The slice spec said "collapsed by default, showing a 'N replies' affordance." I chose to collapse *every* node's children (not just the top level) so a deep thread opens as a clean list of top-level replies, each expandable into its own subtree. Bounding the walk (rather than fetching the entire subtree eagerly) keeps the initial load's network cost proportional to the visible depth — a reader who never expands a reply never triggers the fetch of its children's siblings' subtrees. A full, unbounded thread view (lazy "load deeper" past depth 3) is a natural follow-up but is out of scope here; the current bound is generous for typical conversations.

## Verification

Live verification via MCP Playwright (per the web test policy — no new coded web tests; live Docker app, real browser, **fresh context + `Network.setCacheDisabled` via CDP** to defeat the Blazor WASM immutable framework cache). A 3-level thread was created (root note → level-2 reply → level-3 reply) and the root's object-detail view inspected:

- **Thread structure:** `.reply-thread` rendered with **1** top-level `.reply-node` (the level-2 reply — the only *direct* reply to the root; the level-3 reply is correctly nested under it, not a sibling). The old `ul.object-list` is gone.
- **Collapsed by default:** the level-2 node showed a **"Show 1 reply"** toggle (its `ChildCount` = 1 = the level-3 reply) with **no** `.reply-node-children` in the DOM (the level-3 reply was not rendered until expanded).
- **Expand:** clicking "Show 1 reply" revealed the nested level-3 reply (a child `.reply-node` with the level-3 content and no toggle of its own, since it has no replies); the toggle text became **"Hide 1 reply"** and `aria-expanded` flipped.
- **Collapse:** clicking "Hide 1 reply" removed the child container (back to 1 node, no children in the DOM) and reverted the toggle to "Show 1 reply".
- **No console errors** across the loads. Test posts (root + 2 replies) were deleted after verification.

`dotnet build Iris.slnx` — clean. `dotnet test Iris.slnx` — all tests pass (one known-flaky federation test in `Iris.Server.Tests` passed on isolated re-run; this slice is client-only and touches no server code).

## Out of scope

- **Lazy "load deeper" past the depth bound.** A reply at `MaxReplyDepth` (3) does not fetch its own replies; there is no "load more levels" control. A conversation deeper than 3 levels renders flat at the bound.
- **Virtualization / infinite scroll *within* the thread.** Each node's children are capped at 20 (one page); a reply with >20 direct replies shows only the first 20 in its subtree (no per-node "load more" — the root-level "Show more replies" still pages the top-level list).
