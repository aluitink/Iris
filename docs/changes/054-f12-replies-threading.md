# 054 — F-12 interpret `inReplyTo` / `tag` / `attachment` (replies + client)

> 2026-08-29 · Slice 12.9 · Phase 12 (Spec Conformance & Missing Features)

## What was built

Closes gap **F-12** (`inReplyTo` / `tag` / `attachment` thread rendering, server-side + client round-trip).

- **Reply edge store.** The `CreateActivityHandler` now interprets an inbound `Create`'s `inReplyTo`: when the referenced object is a local content object, the parent → child reply edge is recorded in a new `IReplyStore` (parent IRI → set of reply IRIs, idempotent + thread-safe) so the parent's replies can be threaded.
- **Replies collection endpoint.** The parent's replies are served as a paged `OrderedCollection` at `GET {object-iri}/replies` (items are the reply IRIs — links; a client resolves a reply's full object via the object endpoint). Because a route segment cannot follow `{**path}` (ASP0017), the `/replies` surface is dispatched *inside* the object-document handler by checking for a trailing `/replies` segment and branching to `ObjectRepliesAsync`.
- **Object-document route moved to `{**path}`.** The object-document route changed from `group.MapGet("/o/{**path}", …)` to `group.MapGet("/{**path}", …)` (at the `/ap/v1` group root) so the **object IRI IS the endpoint IRI** — a client fetching `GET {objectIri}` reaches the route, matching federation behavior. More specific routes (`/u/{handle}`, `/u/{handle}/{collection}`, `/c/{name}`, …) still match first by routing priority, so the catch-all only serves content objects.
- **Client-side.** `IActivityPubClient.PostReplyAsync` posts a reply (`inReplyTo` + an `@mention` `tag`) over the signed wire; `GetRepliesAsync` enumerates the parent's replies (a paged collection read through the `CollectionPageCache`).
- **IRI helpers.** `IriExtensions.RepliesOf()` / `GetParentIri()` / `GetMentionIris()` / `GetAttachmentIris()` build and interpret the IRIs. `IPersistenceProvider.Replies` exposes the store.

*Scope note:* this is the **server-side** reply threading + client round-trip. Client-side *rendering* (threaded UI, mention highlighting, attachment display) remains a client concern (Phase 13).

## Key types & files

| Type / file | Role |
|---|---|
| `src/Iris.Server/IReplyStore.cs` | Reply edge storage interface (parent IRI → reply IRIs). |
| `src/Iris.Server.InMemory/InMemoryReplyStore.cs` | In-memory implementation (thread-safe, idempotent). |
| `src/Iris.Server/CreateActivityHandler.cs` | Records the parent → child reply edge from `inReplyTo`. |
| `src/Iris.Server/ActivityPubServerExtensions.cs` | Route moved to `/{**path}`; `ObjectDocumentHandler` branches on trailing `/replies` → `ObjectRepliesAsync`. |
| `src/Iris.Server/IPersistenceProvider.cs` | `Replies` property. |
| `src/Iris.Server.InMemory/InMemoryPersistenceProvider.cs` / `InMemoryPersistenceExtensions.cs` | Wires `InMemoryReplyStore` + DI. |
| `src/Iris.Client/IActivityPubClient.cs` / `ActivityPubClient.cs` | `PostReplyAsync` / `GetRepliesAsync`. |
| `src/Iris.Core/IriExtensions.cs` | `RepliesOf` / `GetParentIri` / `GetMentionIris` / `GetAttachmentIris`. |

## Tests

653 → **689** (+36):

- `tests/Iris.Core.Tests/IriExtensionsTests.cs` — 14 new (IRI helpers).
- `tests/Iris.Server.Tests/InMemoryReplyStoreTests.cs` — 9 new (store semantics).
- `tests/Iris.Server.Tests/CreateActivityHandlerTests.cs` — 3 new (reply-edge recording).
- `tests/Iris.Server.Tests/ReplyIntegrationTests.cs` — 7 new (end-to-end: post reply → `GET {object-iri}/replies` → read back; client `PostReplyAsync`/`GetRepliesAsync` round-trip over the in-process `TestServer`).

## Decisions

- **The object IRI is the endpoint IRI (route moved to `{**path}`).** The old `/o/{**path}` serving prefix meant a client fetching the object IRI (`/ap/v1/u/…`) did not match the route (404). Moving the catch-all to the group root makes the object IRI the endpoint IRI, matching federation behavior. More specific routes still win by routing priority. This is the key fix for the client round-trip and is recorded inline here (no separate decision doc — it is a routing detail with no cross-cutting trade-off).
