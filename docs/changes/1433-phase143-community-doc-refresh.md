# 143.3 — Community document `?refresh=true` (F-142.1/2)

**Commit:** `626072e`
**Findings:** [F-142.1](../plans/phase-142-consistency-review.md) (S2) + [F-142.2](../plans/phase-142-consistency-review.md) (S2)

## Problem

`GET /ap/v1/c/{name}` (community document) never honored the `?refresh=true` cache-bypass contract. The handler unconditionally emitted `Cache-Control: max-age=60, stale-while-revalidate=300`, so callers requesting a fresh read (e.g., an operator who just toggled a setting) could receive a stale copy from an intermediate cache for up to 60s.

The actor document handler (`GET /ap/v1/u/{handle}`) already honored the contract: it checked `HasRefreshBypass(context)`, passed the bypass flag to the local document cache, and emitted `no-cache` on bypass.

## Fix

`CommunityDocumentHandler` now calls `HasRefreshBypass(context)` and emits `no-cache` when the caller requests a fresh read, matching the actor document handler's contract. The community document is read directly from persistence (no server-side cache), so the bypass only affects the response header — no cache-invalidation code is needed.

## F-142.2 disposition

F-142.2 (community document invalidation on outbox-published settings changes) is a **non-issue**: the community document has no server-side cache to invalidate. It is read directly from the store on every request. The client-side caching gap (intermediates serving a stale copy) is closed by the `?refresh=true` → `no-cache` header change in F-142.1.

## Verification

- `dotnet build` clean.
- `dotnet test` green (1283/1283 Iris.Server.Tests; the 2 known-flaky background-delivery tests passed on re-run).
