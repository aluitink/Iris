# 621 — Phase 62 closeout: bug hunt (find + document + fix + re-pass)

> 2026-09-10 · Slice 62.4 · Phase 62 (bug hunt)

## What was built

Phase 62 was a full-system manual bug hunt: find and document defects (62.1), clear
blockers + finish the review (62.2), fix the remaining bug-class findings (62.3), and
re-pass from clean entries until the app is clean (62.4). This change doc records the
closeout — the final clean re-pass and the routing of open findings to the next phases.

Six defects were fixed across 62.2–62.4 (all live-verified via MCP Playwright on a
DB-backed same-origin server):

| Finding | Sev | Class | What was wrong | Fix |
|---|---|---|---|---|
| **B-001** | S1 | blocker | Directory actor links were the literal `Actor.Id` placeholder (Razor string literal, not a C# expression). | `DirectoryCard.razor:8` `ActorId="Actor.Id"` → `@Actor.Id`. |
| **B-005** | S1 | blocker | Profile "Your posts" tab showed foreign (RayvenMX) posts — `OutboxFilter.IsContentItem` ignored the author. | Added `OutboxFilter.IsOwnContentItem(item, authorIri)`; wired the "Your posts" tab to it. |
| **B-003** | S2 | bug | Directory card `aria-label` leaked a `RangeSelectIterator` .NET type name (bound to `@Actor.Name`, a `List<string>`). | `DirectoryCard.razor`: added a `DisplayName` property (first non-empty name → preferred username → `Actor.Id`); bound `aria-label` to it. |
| **B-010** | S3 | bug | Notifications **Delete** rows linked to the actor IRI, not the deleted object (no separate object IRI exists for a Delete). | `NotificationRow.razor`: detect self-delete (`Type=="Delete" && objectIri==actorIri`) and render a muted "deleted their account" caption instead of a link. |
| **B-011** | S3 | bug | Remote actors whose IRI last-segment is a numeric snowflake ID rendered the raw digits as the name. | `NotificationRow.razor`: `DisplayNameFallback(iri)` — a numeric last-segment falls back to the URI host (e.g. `fairy.id`). |
| **B-014** | S2 | bug | Compose "no POST" — earlier blocked by the B-016 CORS defect + the MCP-input artifact. | Resolved by B-016 (CORS fix); the `@bind` counter updates under a native input event and the signed POST is same-origin. Verified end-to-end in 62.4. |

Two **deeper S1 defects** were discovered while re-verifying in 62.3 and fixed the same slice:

| Finding | Sev | Class | What was wrong | Fix |
|---|---|---|---|---|
| **B-015** | S1 | bug | Login always failed with "unknown username" on any EF-backed host. `WebAppFactory` bound `IUserAccountStore`/`IInstanceMetadataStore` to the **in-memory** stores via `TryAddSingleton` *before* `AddEntityFrameworkPersistence`'s `TryAddSingleton<EfUserAccountStore>`, so the in-memory binding shadowed the EF store (DI first-registration-wins) and the EF host used an empty store. | Moved the in-memory `IUserAccountStore`/`IInstanceMetadataStore` defaults into the in-memory persistence branch, so under EF the durable `EfUserAccountStore` is the sole binding. |
| **B-016** | S1 | bug | Cross-origin CORS on **every** local AP read/write: the 55.2 multi-instance override set `advertiseBase` to the browser origin, so `SameOriginApHandler` (which rewrites only requests matching `_advertiseBase.DnsSafeHost`) never rewrote the FQDN — local reads stayed cross-origin and CORS-blocked. | `Program.cs` now keeps the original FQDN (`canonicalAdvertiseBase`) as the rewrite base and passes it to `ActorSessionAccessor` as a new `rewriteBase` argument (`_rewriteBase` → `SameOriginApHandler`), while the effective base (browser origin) still drives `DialBaseUri`/`ProxyBaseUrl`/`IrisNamespaceBase` (the 55.2 multi-instance fix is preserved). |

## 62.4 clean-entry re-pass (converged)

Two consecutive clean re-passes from a fresh login on a DB-backed same-origin server
(`localhost:8089`, `IRIS_ADVERTISE_BASE=https://iris.luit.ink`, Postgres at
`172.19.0.2:5432`), using native DOM input events (MCP synthetic input cannot reliably
drive the Blazor WASM app — see the tracker's Methodology note). Stop condition met
(two consecutive clean re-passes).

Re-verified per page:

- **`/login`** — `andrew`/`Password1` → 302 → `/` (B-015).
- **`/home`** — timeline renders (own + remote posts); **0 console errors** (B-016).
- **`/directory`** — all actor cards expose "Show posts by <name>" (B-003, no
  `RangeSelectIterator` leak) and link to real IRIs (B-001).
- **`/notifications`** — 14 self-delete "deleted their account" captions (B-010), no raw
  numeric names (B-011). The 410s on proxy fetches are the remote instances' correct
  response for gone actors (expected; feeds the caption).
- **`/compose`** — native input → counter updates; signed POST to
  `/ap/v1/u/andrew/outbox`; "Posted (HTTP 202)" + create link; note appears on the home
  timeline (B-014 end-to-end).
- **`/profile`** — "Your posts" shows no foreign posts (B-005 regression check).
- **`/communities`, `/search`, `/settings`** — 0 console errors.
- **Authless** — the Blazor client gates to the sign-in prompt (no broken content).

**Remaining (expected, not defects):** 410s on `/notifications` for gone remote actors
(the correct remote response); one stale `http://localhost:8088/ap/v1/u/alice` actor row
in the dev DB (data drift from an earlier deploy — `andrew`/`bob` use the FQDN and fetch
same-origin with 0 errors). Production same-origin is unaffected.

## Key types & files

- `apps/Iris.Web/WebAppFactory.cs` — B-015: in-memory `IUserAccountStore`/`IInstanceMetadataStore` moved into the in-memory persistence branch.
- `apps/Iris.Web/Program.cs` + `apps/Iris.Web.Client/Accounts/IActorSessionAccessor.cs` — B-016: `canonicalAdvertiseBase` (rewrite base) vs effective `advertiseBase`; new `rewriteBase` ctor param → `_rewriteBase` → `SameOriginApHandler`.
- `apps/Iris.Web.Client/Components/DirectoryCard.razor` — B-001 + B-003: `@Actor.Id` href + `DisplayName` property.
- `apps/Iris.Web.Client/Components/NotificationRow.razor` — B-010 + B-011: self-delete caption + `DisplayNameFallback(iri)`.
- `apps/Iris.Web.Client/wwwroot/css/app.css`, `apps/Iris.Web/wwwroot/css/app.css` — `.notification-self-delete` styling (kept in sync).
- `src/Iris.Server.Data/OutboxFilter.cs` — B-005: `IsOwnContentItem(item, authorIri)`.
- `docs/changes/620-bug-hunt-tracker.md` — the full finding log (all rows now `fixed` with Verify cells).

## Tests

No new coded tests (Phase 62 is a WASM manual-test phase — verification is live Playwright
per the PLAN.md test policy). `dotnet build` clean (0 warn/0 err); full suite green
(0 failures). The Test-debt ledger (620 tracker) is **empty** — no tests were deleted or
skipped during 62.x, so closeout restored nothing and consciously kept all 1,660+ passing
tests. (One flaky 16s delivery integration test,
`FollowEdgeConvergenceIntegrationTests.Follow_Unfollow_Refollow_Cycle_EdgesConvergeOnBothInstances_StableCollections`,
fails intermittently in the full run but passes in isolation and is unrelated to auth.)

## Decisions

- **Routing of remaining findings:** no open **bug**/**blocker** findings remain — the bug
  hunt converged. UX-class findings (if any surface in the visual review) route to Phase 63;
  the six request-spam patterns observed in 62.1 (actor N+1, proxy N+1, per-item
  likes/shares fan-out, public-feed double-fetch) are drafted in the 620 tracker's
  "Phase 64 — request spam & network efficiency" section and route to Phase 64.
- **Verification environment:** 62.3/62.4 verified on a same-origin server
  (`localhost:8089`). The Docker deploy dials `localhost:8088` with
  `AdvertiseBase=https://iris.luit.ink` — a dev-deploy config side effect (the app code is
  correct; the stale `localhost:8088` actor IRI in the dev DB is data drift, not a code
  defect). The B-016 fix is what makes the FQDN case correct; the 8088 row simply doesn't
  match either the origin or the FQDN, so the rewriter (correctly) leaves it alone.
- **Build gotcha (logged for future slices):** `apps/Iris.Web.Client/publish/` caches a
  stale WASM — the `BuildAndCopyClient` MSBuild target skips re-publish if
  `blazor.webassembly.js` exists. Clear that dir before a Docker rebuild or you test an old
  client.
