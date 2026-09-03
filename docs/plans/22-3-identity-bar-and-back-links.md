# 22.3 — Identity bar (US-3) + back links (US-22) deep-dive

Phase 22 slice. Elaborates the two cross-cutting "make the tool work" stories that live in the app
shell rather than a single page: **US-3** (always-visible identity bar) and **US-22** (consistent
back links / deep-links). Together they close the "I always know which server I am on, and I can get
back to where I came from" half of the explorer.

## Current shape

- **`Layouts/MainLayout.razor`** — the app shell. The header (`.explorer-header`) is a flex row: the
  `h1` ("Iris · server explorer") on the left, and a single `.instance` span on the right that shows
  either `Session.ActorIri?.Value` (when logged on) or "not logged on". The nav row below it lists the
  nine explorer screens. There is **no dial-base indicator** and **no back-link slot**.
- **`Explorer/ExplorerSession.cs`** — the session. It already exposes everything the shell needs:
  - `IsLoggedIn`, `ActorIri`, `ResolvedActorIri` (the authoritative advertised IRI), `DialBaseUri`
    (what the browser actually dials — distinct from the advertised IRI host for local instances).
  - `NavigableObjectIri` / `NavigableActorIri` — the "last-viewed item" navigable state (19.8.5),
    set by `ObjectPage` (`SetNavigableObjectIri`) and `ActorDetail` (`SetNavigableActorIri`) on load,
    and preserved across instance switching. `Home` already renders a "Continue where you left off"
    card from these.
- **Detail pages** (`ActorDetail`, `ObjectPage`, `Community`) — each has its own `<h2>` heading and a
  load card. None has a back link; a user who deep-linked from a feed or from another actor has to use
  the nav row or the browser back button to return.

## What this slice adds

### 1. Identity bar (US-3) — `MainLayout.razor`

When logged on, the `.instance` span becomes a two-line identity bar:
- **Line 1** — the actor IRI (`Session.ResolvedActorIri ?? Session.ActorIri`), the "who am I".
- **Line 2** — `Dialing {Session.DialBaseUri}`, the "which server a read/write hits" (the dial base,
  which for a local instance is the host-published port, distinct from the advertised IRI host).

When logged out it keeps the single "not logged on" line. This is exactly what US-3 asks for: the
active identity **and** dial base always visible (not only on the Home page), so a read/write's target
server is known before acting. No new session surface is needed — `ResolvedActorIri` and
`DialBaseUri` already exist; the shell just renders them.

### 2. Back link (US-22) — new shared `Components/BackLink.razor`

A small shared component (the "back-link slot" US-22 names for `MainLayout`, realized as a reusable
component the detail pages opt into) that renders one link at the top of a detail page:
- If the session has a **navigable object** (`NavigableObjectIri`) → `← Back to object` deep-linking to
  `/object?iri={escaped}`.
- Else if it has a **navigable actor** (`NavigableActorIri`) → `← Back to actor` deep-linking to
  `/actor?iri={escaped}`.
- Else → `← Home` (links to `/`).

Deep-link targets mirror the exact patterns `Home`'s "Continue where you left off" card already uses
(`/object?iri=…`, `/actor?iri=…`), so navigation state stays consistent. The component injects
`ExplorerSession` itself (like `MainLayout`), so a page just drops in `<BackLink />`.

**Wiring** — `<BackLink />` placed immediately under the page `<h2>` (above the load card) on the
three detail pages: `ActorDetail`, `ObjectPage`, `Community`. These are the pages a user deep-links
*into*; the list/search pages (`Actors`, `Feed`) and the authoring screens don't need one.

### 3. CSS — `wwwroot/css/app.css`

- `.back-link` — a muted, underlined-on-hover link, small, with a left margin so it reads as a
  sub-heading above the page content.
- `.explorer-header .identity` (new, replacing the single `.instance` span) — a right-aligned,
  vertically-stacked block: the actor IRI (slightly brighter) over the dial-base line (muted, smaller),
  with the IRI wrapping cleanly (`max-width` + `word-break`).

## Key seams

- `ExplorerSession.ResolvedActorIri` / `ActorIri` / `DialBaseUri` — the identity (no change).
- `ExplorerSession.NavigableObjectIri` / `NavigableActorIri` — the back-link target (no change).
- `Uri.EscapeDataString` — IRI → query-string escaping (the same idiom the existing deep-links use).
- No new NuGet packages; the component uses only `Iris.Core.Identity` (`Iri`) + the existing session.

## Failure / empty states

- **Logged out:** the identity bar shows "not logged on"; `<BackLink />` renders nothing (it is only
  placed inside the logged-on branch of each page).
- **No navigable state yet:** `<BackLink />` degrades to `← Home`.
- **Navigable state present:** the back link targets the most recently recorded kind — an object if
  one was set, else an actor (object takes precedence, matching `Home`'s render order).

## Verification (manual, docker compose FQDN)

Per `docs/reference/TESTING.md` and the Phase 22 direction, the sample UI is **verified manually**
(not bUnit-tested) while in flux. On the compose stack (`iris-a`/`iris-b`/`iris-ui`):
1. Log on as `alice@iris-dev1.luit.ink` (dial base `http://localhost:8081`). Confirm the header shows
   the actor IRI **and** the "Dialing http://localhost:8081" line (US-3).
2. From Home, open an object (the community feed's first item) → confirm `ObjectPage` shows a
   "← Back to actor" / "← Back to object" back link (US-22). Open an actor (the object's author) →
   confirm `ActorDetail` shows a back link to the last-viewed item.
3. Log out → confirm the header shows "not logged on" and no back link renders.
4. The wire (the navigable-state setters + the session getters) is already proven by the existing
   `SampleServer.Tests` / integration coverage of `ExplorerSession`; this slice is pure shell/render.

## Scope / limits

- Back links are added to the three detail pages (actor, object, community). The identity bar is global
  (the shell). No other pages are touched.
- The back link targets the **last-viewed** item (single slot), not a full breadcrumb history — that
  matches the existing single-slot navigable state (19.8.5) and `Home`'s "continue where you left off"
  semantics. A true history/back stack is out of scope for this slice.
