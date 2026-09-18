# 121.1 — Notifications: replace raw IRI links with friendly labels

**Date:** 2026-09-13
**Branch:** phase-32-production-app

## Scope

Notification rows on the Notifications page showed raw IRI fragments (e.g. `u alice`, `notes 06G84EEMA8PZR6FPZSJSVH2PPR`) as the clickable link label whenever no content preview was available. This affected follow requests (object is the actor's own IRI) and likes/boosts where the embedded object carries no human-readable `name` or `summary`.

## Change

**File:** `apps/Iris.Web.Client/Components/NotificationRow.razor`

In `RenderCardBody`, the `else if (objectIri is { } target)` branch previously fell back to `ShortLabel(target)` (a truncated raw IRI path). It now calls a new `FriendlyLabel(activity, target)` method.

### New `FriendlyLabel` method

Inspects the IRI's absolute path segments and returns a human-readable label:

| IRI pattern | Example path | Label |
|---|---|---|
| `/ap/v1/u/{user}` | `.../u/alice` | `View alice's profile` |
| path contains `notes` | `.../notes/06G8...` | `View note` |
| `/ap/v1/c/{name}` | `.../c/lemmy` | `View lemmy` |
| anything else | — | falls back to `ShortLabel(iri)` |

The `activity` parameter is available for future use (e.g. distinguishing Like vs Announce) but the current logic keys off the IRI path alone.

## Verification

- **Build:** `dotnet build apps/Iris.Web/Iris.Web.csproj -c Release` — 0 warnings, 0 errors.
- **Web tests:** `dotnet test tests/Iris.Web.Tests --no-build -c Release` — 88/88 pass.
- **Live (Docker app, signed in as alice):**
  - Follow request rows: link label reads "View bob's profile" (was "u bob").
  - Like rows (no embedded name): link label reads "View note" (was "notes 06G84EE...").
  - 0 console errors.

## Files changed

- `apps/Iris.Web.Client/Components/NotificationRow.razor` — +34 / -1 (new `FriendlyLabel` method + call-site swap).
