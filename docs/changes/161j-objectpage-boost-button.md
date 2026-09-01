# 161j — ObjectPage boost/unboost button (19.8.6 / 19.6.1 UI wiring)

## Summary

Phase 19.8.6 (write-screen round-trips) + the UI half of 19.6.1 (management via ActivityStream): the
object view's **Boost** button is wired end-to-end. The ObjectPage now offers a Boost/Unboost toggle
next to the existing Like/Unlike toggle, calling the client's `AnnounceAsync` (boost) /
`UnannounceAsync` (unboost) — the one-call methods added in change 161i — through the signed pipeline
to the acting actor's outbox.

## What changed

### `ObjectPage.razor` (the object view's write card)

- **`HasBoosted`** state property (mirrors `HasLiked`).
- **`ToggleBoostAsync()`** — when logged on and an object is loaded, toggles between
  `client.AnnounceAsync(actorIri, objectIri)` (boost) and
  `client.UnannounceAsync(actorIri, objectIri)` (unboost), reusing the page's existing
  `WriteBusy` / `WriteResult` / `WriteError` machinery (the same guard + catch/finally shape as the
  Like/Unlike toggle).
- **Markup** — a `Boost`/`Unboost` button + a "You boosted this." indicator, placed between the
  Like/Unlike toggle and the author-only Delete button, so the read-only viewer sees Like + Boost and
  the author additionally sees Delete.

The button is available to any logged-on actor (a boost re-shares the object to the actor's
followers); it is not gated on authorship, unlike Delete.

## Tests

Two new in-process screen tests in `S7ScreenTests` (the dial-base, all-local host — the same pattern
as the existing `ObjectLike_Like_…` / `ObjectUnlike_Undo_…` tests, so the boost lands as a local,
non-federated write with no proxy fallback):

- **`ObjectBoost_Announce_SurfacesInAuthorOutbox`** — seeds a bob note, calls the client's
  `AnnounceAsync` (the exact call the button makes), and asserts the Announce is stored with the
  deterministic `{actor}/announces/{object}` IRI and lists in the actor's outbox.
- **`ObjectUnboost_Undo_RemovesTheBoostEdge`** — boosts then unboosts and asserts both the Announce
  and the Undo-of-Announce are stored, and that the Undo references the exact Announce by its
  deterministic IRI.

These complement the client-level integration tests from change 161i (which pin `AnnounceAsync` /
`UnannounceAsync` against the live outbox endpoint) and the raw-inspector wire coverage from 161g.

Full suite green: **1,258 tests, 0 failed**. Build clean (`TreatWarningsAsErrors` on).

## Scope note

This wires the **UI** boost button for 19.8.6 / 19.6.1. The remaining live/UI-verification item for
19.6.1 is the **raw inspector** — driving the button through the browser and confirming the rendered
signed message matches the ActivityStream activity (a live/UI item requiring the Docker env +
RayvenMX, per the Phase 19 header).
