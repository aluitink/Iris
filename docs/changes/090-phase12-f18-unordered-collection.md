# 090 — Phase 12: F-18 — unordered `Collection` support in the client's collection enumeration

> 2026-08-30 · Phase 12 (Spec Conformance & Missing Features) · Gap closure (F-18)

## What was built

The client's `GetCollectionAsync` / `GetCollectionItemsAsync` now enumerate an **unordered**
`Collection` (the base ActivityStreams collection type), not just `OrderedCollection` /
`OrderedCollectionPage`. A remote that serves a stable collection (outbox, followers, a feed, a
search) as an unordered `Collection` — its document carrying the first page of items plus a self
`first` link — is now read correctly instead of its items being silently dropped.

## The fix

A single change in `src/Iris.Client/ActivityPubClient.cs`:

`FetchCollectionPageAsync` had two branches — an `OrderedCollectionPage` (page N>1) and an
`OrderedCollection` (page 1 served as the collection document) — and returned `null` for anything
else. A base `Collection` fell into the `null` path, so `GetCollectionAsync` yielded nothing.

The fix adds a third branch for a base `Collection`:

```csharp
if (obj is Collection { Id: not null } unordered)
{
    var items = unordered.Items is { } itemsEnumerable ? itemsEnumerable.ToList() : [];
    return new CollectionPage
    {
        Page = new OrderedCollectionPage { Id = unordered.Id, Items = items, TotalItems = unordered.TotalItems },
        Items = items,
        NextPage = null,      // an unordered Collection has no typed `next`
        PrevPage = null,
        TotalItems = unordered.TotalItems is { } total ? (int)total : null,
        PageId = new Iri(unordered.Id),
    };
}
```

Two notes:

- **The `is not OrderedCollection` guard is implicit in branch ordering.** The library's
  `OrderedCollection` derives from `Collection`, so the new `is Collection` branch would also match
  an `OrderedCollection`. It is placed *after* the `OrderedCollection` branch, so an `OrderedCollection`
  is handled there first (preserving the extension-data `next` resolution that lets the walk continue
  past page 1) and only a *base* `Collection` reaches the new branch.
- **The walk terminates after page 1 for a base `Collection`.** The ActivityStreams `Collection` type
  has no typed `next` property (only `CollectionPage` does), so the client cannot follow past page 1.
  This is acceptable for a rarely-used, low-priority shape (`OrderedCollection` covers the realistic
  case); a `CollectionPage` first page is still followed correctly via the existing
  `OrderedCollectionPage` branch.

The `ResolveFirstPageIri` helper already accepted a `Collection { First: { } first }`, so following
the collection's `first` link to the first page worked before this change — only the *page* handling
was missing.

## Tests

- **`CollectionTests.GetCollectionAsync_UnorderedCollection_YieldsPage1Items`** (new): a base
  `Collection` (collection document carrying 2 items + a self `first`) yields one page with the 2
  items, `NextPage == null`, `IsLastPage == true`.
- **`CollectionTests.GetCollectionItemsAsync_UnorderedCollection_FlattensItems`** (new): the 2 items
  are flattened in order.
- **`CollectionTests.GetCollectionItemsAsync_UnorderedCollection_WithLimit_StopsAtLimit`** (new): a
  limit of 1 yields only the first item.

## Files changed

- `src/Iris.Client/ActivityPubClient.cs` — `FetchCollectionPageAsync` now accepts a base `Collection`.
- `tests/Iris.Client.Tests/Collections/CollectionTests.cs` — 3 new tests + the unordered fixture
  documents / routing handler.

## Decisions

- **Terminate the walk after page 1 for a base `Collection`** rather than synthesize a `next` from
  extension data: the unordered shape is low-priority and rarely used, and the `OrderedCollection`
  path already handles the realistic (ordered, multi-page) case. Synthesizing a `next` for a base
  `Collection` would be speculative (there is no spec-mandated `next` to read).

## Test count

935 → 938 (+3), 0 failures.
