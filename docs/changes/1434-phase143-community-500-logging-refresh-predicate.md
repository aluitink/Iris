# 143.4 — Community outbox 500 logging + shared `?refresh` predicate (F-142.3/5)

**Commit:** `0939aa4`
**Findings:** [F-142.5](../plans/phase-142-consistency-review.md) (S3) + [F-142.3](../plans/phase-142-consistency-review.md) (S3)

## F-142.5 — Community outbox 500 logging

`CommunityOutboxPublishHandler`'s catch block returned a bare 500 with no logging, making unhandled publish failures invisible. The actor outbox handler (`OutboxPublishHandler`) logs the exception via `ILoggerFactory` before returning 500.

**Fix:** The community handler now resolves `ILoggerFactory` from `context.RequestServices` (same pattern as the actor handler — no extra DI parameter needed) and logs the exception with the community name and activity type before returning 500.

## F-142.3 — Shared `?refresh` predicate

`CommunityCollectionEndpointAsync` used an inline strict `.Equals("true", OrdinalIgnoreCase)` check for the `?refresh` query parameter, while all other cacheable endpoints (actor document, actor collections) use the shared `HasRefreshBypass(context)` helper (which returns true for any non-empty value except `"false"`).

**Fix:** Replaced the inline check with `HasRefreshBypass(context)`. `?refresh=1` (and other truthy values) now bypass the community collection-page cache, consistent with the rest of the server.

## Verification

- `dotnet build` clean.
- `dotnet test` green (1283/1283 Iris.Server.Tests; 1 known-flaky background-delivery test failed on first run, passed on re-run).
