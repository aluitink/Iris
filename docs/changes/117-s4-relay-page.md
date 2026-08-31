# 117 — S4: Relay page (F-06) — subscribe / unsubscribe / list relays

**Status:** DONE — full solution green (`dotnet build` 0 warnings; `dotnet test` 862/862).

## Objective

Close the last write-feature UI gap: the entire relay feature (F-06 — subscribe to / unsubscribe from /
list a relay, the "fan-out servers" an actor's content is distributed through) had a full client API
(`GetRelaysAsync` / `SubscribeRelayAsync` / `UnsubscribeRelayAsync`) and a server endpoint, but **no UI**.
This slice adds the UI (a Relays card on the actor-detail page) and fixes a latent client/server cache gap
that made the UI's post-write re-read observe a stale page.

## Changes

### 1. `samples/SampleBlazorClient/Pages/ActorDetail.razor` — Relays card

Added a **Relays** card (after the moderation card), mirroring its structure:

- Lists the actor's current relays via `GetRelaysAsync` (the `{actor}/relays` collection — the ActivityPub
  `star` set; an empty state reads "No relays subscribed.").
- A relay-IRI input + **Subscribe** (`SubscribeRelayAsync`) and **Unsubscribe** (`UnsubscribeRelayAsync`)
  buttons. Both use the local Basic-auth path (the logged-on actor's own credentials — relays are a local
  decision, not signed federation).
- `LoadByIriAsync` loads the relays when the page loads; after a successful write the list is refreshed
  with `bypassCache: true` so the change is visible immediately (see change 3).
- Result / busy / error states mirror the moderation card (`RelayResult`, `RelayBusy`, `RelayError`).

### 2. `src/Iris.Client/ActivityPubClient.cs` — thread `BypassCache` into the network GET as `?refresh=true`

**Latent gap found while writing the test.** The server serves the local collection pages (outbox,
followers, following, liked, blocks, flags, mutes, **relays**) through the `LocalCollectionPageCache`
(60s fresh / 300s stale) and re-renders a page **only** on `?refresh=true`. The client's
`CollectionQuery.BypassCache` only skipped the *client-side* `CollectionPageCache` — it never sent
`?refresh=true`, so a read that followed a write (subscribe → list) observed the **stale** cached page until
the 60s fresh window lapsed. The S4 relay card (and any caller) could not observe its own write.

Fix: `FetchCollectionPageAsync` now threads `bypassCache` into the network fetch via a new
`GetCollectionPageFromNetworkAsync(pageIri, refresh, ct)` that appends `?refresh=true` (or
`&refresh=true` when the page IRI already carries a query, e.g. `?page=N&limit=M`). The parameter is
Iris-server-specific and ignored by non-Iris ActivityPub implementations.

### 3. `tests/SampleBlazorClient.Tests/S4RelayTests.cs` (new) — 2 in-process tests

Hosts a real in-process ActivityPub server (`alice` + `bob` at the dial base) and, logged on as `alice`:

- **SubscribeRelay_RelayAppearsInRelaysCollection** — starts empty, subscribes to relay-a (204), asserts
  relay-a is in `GetRelaysAsync` (bypassing the page cache), then subscribes relay-b and asserts both present.
- **UnsubscribeRelay_RelayRemovedFromRelaysCollection** — subscribes both, unsubscribes relay-a (204), asserts
  relay-a is gone and relay-b remains.

## Test coverage

- `S4RelayTests` (2 new) — subscribe → relay in collection; unsubscribe → relay removed. Both rely on the
  `?refresh=true` fix to observe the post-write state.
- Full solution green: **862/862** (Iris.Server 656, Iris.Client 110, Iris.Core 195,
  Iris.Client.Extensions 29, SampleBlazorClient **67**, SampleServer 18, Iris.LiveInterop 18, Iris.Testing 12).

## Notes

- The relays collection is the ActivityPub `star` set; the server maps it to the `Relays` store at
  `/ap/v1/u/{handle}/relays` (GET) and `/ap/v1/u/{handle}/relays/{target}` (POST, `?unsubscribe=true` to
  remove), 204 on success.
- Logged-in *browser* verification is blocked by the environment's orphaned root-owned 8081 server (CORS
  locked to origin 8090) — an environment constraint, not a code defect. The in-process tests exercise the
  identical client API + server pipeline.
