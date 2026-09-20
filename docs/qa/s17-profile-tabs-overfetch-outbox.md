# S17 — Profile tabs over-fetch the entire outbox (on load + every tab switch)

- **Class:** perf / request-spam — **Severity:** S2
- **Status:** open (found Pass 29)
- **Found:** Pass 29 (2026-09-20)
- **Related:** [s16](s16-poll-votes-not-persisted.md) (polls also missing from "Your posts"), the Profile pagination ("Load more") control

## Symptom

On **Profile → Your posts** (the default tab), and on each of the **Replies** and **Likes** tabs, the client fetches the user's **entire outbox** by fanning out **all pages** of `GET /ap/v1/u/<handle>/outbox` (`?page=2 … ?page=N`) at once — instead of fetching a single page and loading more on demand.

Repro (andrew, ~130 posts → 13 outbox pages):

1. Fresh load of `/profile` → the "Your posts" tab fires `outbox` **13 times** (pages 1–13) before anything renders. Only **3 items** are rendered; a **"Load more"** button is present.
2. Click **Replies** → the client re-fires the **full 13-page outbox fan-out** again (a second identical 1–13 sequence). The Replies tab shows an **empty** list — it pulled all of andrew's posts to render nothing.
3. Click **Likes** → the client re-fires the **full 13-page outbox fan-out** a third time.
4. Click "Load more" on "Your posts" → fires **more** outbox pages **past the real data** (pages 14–16 — empty/overflow pages the server returns 200 for), and only a couple more items render.

Net: ~40 redundant `outbox` requests for a single profile visit (3 full fan-outs of 13 + 3 overflow pages), all `200`, **0 console errors**. The cost scales linearly with the user's post count and is repeated on **every tab switch**.

## Root cause

The profile tabs are not scoped to their own collection and do not paginate. Each content tab (Your posts / Replies / Likes) drives the same full-outbox pager: it walks `?page=` until the server stops returning items, pulling the whole outbox client-side, and it re-runs that walk whenever the active tab changes. "Load more" continues the same unbounded walk (it kept requesting pages 14–16 that contain no posts).

Expected: each tab fetches **one page** of **its own** collection (Replies → the actor's replies; Likes → the like/liked collection; Your posts → outbox page 1), and only fetches the next page when "Load more" is clicked — and stops once a page is empty.

## Fix

- Scope each tab to its own collection/endpoint rather than reusing the full outbox pager.
- Fetch a single page on tab activation; gate "Load more" to the next page and **stop on the first empty page** (don't keep requesting past the end).
- Don't re-fetch the whole collection on tab switch — cache the already-loaded pages or re-fetch only the active tab's first page.

## Re-verify

Clean entry, `/profile` (a user with a large outbox):
- "Your posts" fires **one** `outbox` request on load (page 1 only); "Load more" fires exactly one more, and clicking it past the end fires **no** further requests.
- Switching to **Replies** / **Likes** fires a request scoped to that tab's collection (not the full outbox), once — not the 13-page fan-out.
- Total `outbox`/collection requests for a visit ≈ (pages actually viewed) + (tabs actually opened), not 3× the full outbox.
