# S3 — Object-detail 404s a local post's collections (Create-activity IRI)

- **Class:** bug (console-noise) — **Severity:** S3
- **Status:** open (re-confirmed Pass 27, 2026-09-20, on the rebuilt container)
- **Found:** Pass 11 (2026-09-20) — re-confirmed Passes 15, 18, 19, 27
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
