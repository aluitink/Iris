# 22.1 deep-dive — `PagedCollection` shared component (US-20, US-9)

**Feeds roadmap item:** 22.0 / 22.1 (shared components). **Stories:** US-20 (the component), US-9
(enumerate + page any collection), and the paged-collection halves of US-5 / US-6.

## Current shape (what this consolidates)

Every paged collection in the sample UI is the *same* hand-rolled idiom, copy-pasted per card:

- A **state quartet** per card: `List<IObjectOrLink>? {Name}Items`, `bool {Name}HasMore`,
  `bool {Name}LoadingMore`, `string? {Name}Error`, plus a private `Iri? _{name}ResumeFrom`.
- **Three methods** per card: `LoadInitial{Name}Async` (reset + fetch first page), `LoadMore{Name}Async`
  (the "Load more" continuation), `EnumerateOne{Name}PageAsync` (the actual one-page fetch that breaks
  after the first yielded `CollectionPage`).
- A **markup block**: the `Loading…` / `empty` / `error` branches, the `<ul class="object-list">` of
  `<li>` items, and the `<button>Load more</button>`.

That idiom appears in `Feed.razor` (one card) and **eight** more times in `ActorDetail.razor` (outbox,
followers, following, target mutes/blocks/flags, inbox) and `Community.razor` (followers, following,
mutes/blocks/flags) — ~25 near-identical `Load*`/`LoadMore*`/`EnumerateOne*PageAsync` triplets and
~40 state fields.

The fetch body is always:

```
client.GetCollectionAsync(resumeFrom ?? firstIri, query, ct)
    → take the first yielded CollectionPage → append Items → HasMore = !page.IsLastPage
      → resumeFrom = page.NextPage
```

i.e. **one server page per "Load more" click** (20.3).

## The component

`PagedCollection.razor` (new, in `samples/SampleBlazorClient/Components/`).

**Parameters**

| Parameter | Type | Meaning |
|---|---|---|
| `Client` | `IActivityPubClient?` | The client to dial. `null` → the component does not load (renders nothing). |
| `CollectionIri` | `Iri?` | The IRI of the collection **or its first page**. `null` → does not load. |
| `Title` / `Description` | `string?` | Optional `<h3>` + muted intro paragraph (so the card is self-contained). |
| `PageSize` | `int?` | Items per page → `CollectionQuery(Limit:)`. `null` → the server's natural page size. |
| `BypassCache` | `bool` | `true` → `CollectionQuery(BypassCache: true)` (the `?refresh=true` half, US-6/20.4.2). |
| `ItemTemplate` | `RenderFragment<IObjectOrLink>?` | Renders one item. Defaults to `<ObjectView Item="…" />`. Lets a page render follower/actor links instead. |
| `EmptyMessage` | `string?` | The "no items" line. |
| `HeaderContent` | `RenderFragment?` | Optional extra markup under the title (e.g. a management-error line) that should stay visible. |

**Behavior**

- Loads the **first page in `OnParametersSetAsync`** when both `Client` and `CollectionIri` are non-null
  (so it works whether the page loads the collection IRI up-front or sets it later, e.g. after a
  deep-link resolve). Re-fetches (resets) when the `CollectionIri` *value* changes (a new actor).
- `Load more` fetches exactly **one more page** (walking `next`), appending to the accumulated list —
  identical to the current per-card semantics.
- Renders the standard state set (US-23): **loading** ("Loading…"), **error** (`.error` div + message),
  **empty** (`EmptyMessage`), else the item list; a **Load more** button only when a further page
  exists.
- **No `async void`; no `.Result`.** The client seam is the existing, integration-tested
  `IActivityPubClient.GetCollectionAsync(Iri, CollectionQuery?, CancellationToken)`.

## Why this is the right seam (and not a new backend)

The client already exposes exactly what's needed (`GetCollectionAsync` + `CollectionQuery`
limit/bypass, `CollectionPage.NextPage`/`IsLastPage`). This slice therefore introduces **no new
client/server seam** — it is a pure UI consolidation of an existing, already-tested wire. Per the
Phase-22 testing discipline (roadmap rule 5) it is **verified manually (Playwright MCP)**, not with a
new bUnit test. The wire it drives is already pinned by the existing server/client integration tests.

## Failure / empty states

- `Client`/`CollectionIri` null → renders nothing (the parent gates on it, as today).
- First-page fetch throws → `.error` with the message (no blank, no raw dump).
- Empty first page + no next → `EmptyMessage`.
- Mid-paging error → the error line renders under the items already loaded; "Load more" stays enabled
  so the user can retry.

## Adoption in this slice

To keep the change coherent and verifiable, this slice:
1. Adds `PagedCollection.razor` + `RawInspector.razor` (see `22-2-raw-inspector-component.md`).
2. Refactors **`Feed.razor`** (the simplest card, one collection) onto `PagedCollection`.
3. Refactors **`ActorDetail.razor`'s Outbox card** onto `PagedCollection` and its **Raw inspector card**
   onto `RawInspector`.

The remaining 7 `ActorDetail` cards + the `Community` cards are the same mechanical swap and are
deliberately left for 22.2 (detail pages on the shared components) to keep this slice focused and
low-risk.
