# 161h — Error & empty states: Actors, Community, Instance, Feed pages (19.8.7)

## Summary

Phase 19.8.7: add error handling to the four Blazor pages that previously let exceptions propagate
unhandled. All four now follow the existing error-handling pattern (catch + Error property +
`<div class="error">` render) used by ObjectPage, ActorDetail, Home, and Compose.

## What changed

### `Actors.razor`

- Added `Error` property.
- `SearchAsync`: added `catch (Exception ex) { Error = ex.Message; }`; clears `Error` before the
  search.
- Render: when `Error` is set, shows a `.error` div instead of the results. Empty results already
  handled ("No matching actors or content.").

### `Community.razor`

- Added `LoadError` and `SearchError` properties.
- `LoadAsync`: added `catch (Exception ex) { LoadError = ex.Message; }`; clears `LoadError` before
  the load.
- `SearchAsync`: added `catch (Exception ex) { SearchError = ex.Message; }`; clears `SearchError`
  before the search.
- Render: `LoadError` shows a `.error` div replacing the feed/members cards; `SearchError` shows a
  `.error` div in the search card. Empty search results now show "No matching actors or content."
  (previously the empty-results case was not handled).

### `Instance.razor`

- Added `Error` property.
- `OnInitializedAsync`: added `catch (Exception ex) { Error = ex.Message; }`.
- Render: when `Error` is set, shows a `.error` div (previously the exception propagated unhandled
  — the page would crash).

### `Feed.razor`

- `LoadInitialAsync`: added `catch (Exception ex) { LoadError = ex.Message; }` (previously the
  exception propagated unhandled on initial load).
- `LoadMoreAsync`: added `catch (Exception ex) { LoadError = ex.Message; }` (previously the
  exception propagated unhandled on "Load more").
- The `LoadError` property and `.error` render already existed (they were just never set).

## What's already handled (no change)

- **ObjectPage**: 404/unknown object, invalid IRI, fetch errors, write errors — all handled.
- **ActorDetail**: fetch errors, follow-decision errors, write errors, relay errors — all handled.
- **Home**: logon errors, feed load errors, outbox errors — all handled.
- **Compose**: write errors — handled.
- **Deliver**: write errors — handled.

## Tests

No new tests — the existing 84 SampleBlazorClient.Tests tests still pass (they drive the client
directly, not through Blazor rendering). The 404-object state is already exercised in CI
(`S10RawDeliveryTests.Deliver_RawFollowToUnknownInbox_IsNotFound`); the proxy-fallback state is
already exercised in CI (`S8InspectorAndProxyTests.ProxyFallback_Direct401_RetriesThroughHomeProxyAndSucceeds`).
The remaining verification (driving each error state through the UI and confirming the rendered
message) is a live/UI-verification item.

Full suite green: **1,254 tests, 0 failed**. Build clean (`TreatWarningsAsErrors` on).
