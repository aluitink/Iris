# S3 — Object-detail 404s a local post's collections (Create-activity IRI)

- **Class:** bug (console-noise) — **Severity:** S3
- **Status:** open (re-confirmed Pass 39, 2026-09-20, on deployed `4f5dd5c`)
- **Found:** Pass 11 (2026-09-20) — re-confirmed Passes 15, 18, 19, 27, 35
- **Related:** distinct from [S13](s13-remote-lemmy-404-noise.md) (remote Lemmy collections)

## Symptom

Opening a **local** post's object-detail page via its deep-link IRI (`/object?iri=…/creates/{id}` — the deep-link the Profile tab and the post's own Reply button produce) 404s the post's own `/replies` + `/likes` + `/shares` collections — **3 console errors** — even though the post renders fine with "No replies yet."

## Root cause

A local post has **two IRIs**: the **Create activity** (`…/creates/{id}`, the `?iri=` deep-link value) and the **stored Note object** (`…/notes/{id}`, a *different* id).

`ObjectDetail.razor`'s `ObjectIri` getter (`:339-349`) returns the **raw `IriParam`** (the activity IRI) and derives `/replies` + `/likes` + `/shares` from it (`:403`, `:409`; replies load `:504`). The server serves those collections only for the **stored object** IRI — an activity IRI is "an object this instance does not store" → 404 (`ObjectRepliesAsync` / `ObjectLikesAsync`, `ActivityPubServerExtensions.cs:7856-7862,7917-7919`).

Verified live: `…/notes/…5M/replies|likes|shares` = **200 empty collections** (correct); `…/creates/…5G/replies|likes|shares` = **404** (the bug).

## Fix

The page already resolves the Note via `SubjectObject` (content + Reply href render from it — the Reply link even points at `…/notes/…5M`). Derive the collection walks from the **resolved Note IRI** (`SubjectObject.Id` / the object the doc wraps), not the raw `?iri=` activity IRI.

Secondary server-side option: serve a Create activity's object's collections too, or 302 the activity IRI → object IRI.

## Re-verify

Open a local post's object detail via the `/object?iri=…/creates/{id}` deep-link: 0 console errors, Replies/Likes/Shares tabs load the (empty) collections.

**Re-verification evidence (Pass 27, 2026-09-20, andrew):** navigating a Create-activity IRI (`…/creates/06GBSGQTYCMVSXEYC9XCMK6MPM`) still yields **"Object not found"** (404). STILL OPEN.

**Re-verification evidence (Pass 35, 2026-09-20, andrew, deployed `bb28dcf`):** two fresh posts' Create-activity IRIs (`…/creates/06GBV4N99HPN3H8STQQ74F5YB0`, `…/creates/06GBV4YCNNZ54BN9Y7W6QMPFQ0`) both → **404** via `curl` and in-browser ("Object not found"). The profile's object-detail link for the first post points to the **Note** IRI (`…/notes/06GBV4YCNNZ54BN9Y7W6QMPFQ4` → 200, 0 console errors, Replies tab loads empty collection correctly). The Reply link also uses the Note IRI. The Create-activity IRI 404 is only reachable if a client constructs `?iri=…/creates/{id}` directly. STILL OPEN (narrower scope than previously thought — the UI now routes through Note IRIs).

**Re-verification evidence (Pass 38, 2026-09-20, andrew, rebuilt container post-`bdc0e66`):** Create-activity IRI `…/creates/06GBV4N99HPN3H8STQQ74F5YB0` → in-browser: **"Object not found"** + 1 console 404. `curl` confirms: `…/creates/…/replies` = **404**; `…/notes/06GBV4YCNNZ54BN9Y7W6QMPFQ4/replies` = **200**. Profile "Open post" links now use **Note** and **Object** IRIs (no Create-activity IRIs in the UI). STILL OPEN (narrow scope — only reachable via direct `?iri=…/creates/{id}` deep-link).

**Re-verification evidence (Pass 39, 2026-09-20, andrew, deployed `4f5dd5c`):** created fresh post "QA Pass 39 S3 re-verify post" (Note IRI: `…/notes/06GBVQB2920X0D9R9611ANV1MG`, Create IRI: `…/creates/06GBVQB28WTGQCAYWQM58KD4JC`). Navigating to the Create-IRI → **"Object not found"** + 1 console 404. Note IRI → 200, 0 console errors, clean render. STILL OPEN (narrow scope — only reachable via direct `?iri=…/creates/{id}` deep-link).

**Re-verification evidence (Pass 41, 2026-09-20, andrew, deployed `59ff4ec`):** Create-IRI `…/creates/06GBVQB28WTGQCAYWQM58KD4JC` → **HTTP 404** (net::ERR_HTTP_RESPONSE_CODE_FAILURE in Playwright). Note IRI `…/notes/06GBVQB2920X0D9R9611ANV1MG` → 200, clean render. Profile "Open post" links use Note/Object IRIs (no Create-activity IRIs in UI). STILL OPEN (narrow scope — only reachable via direct `?iri=…/creates/{id}` deep-link).
