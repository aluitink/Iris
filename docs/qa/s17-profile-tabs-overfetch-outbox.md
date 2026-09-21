# S17 — Profile tabs over-fetch the entire outbox (on load + every tab switch)

- **Class:** perf / request-spam — **Severity:** S2
- **Status:** fixed (commit `645b3660`, 2026-09-21) — **awaiting QA re-verify** — Likes tab now reads the scoped `/liked` collection (1 request, no fan-out); Your posts/Replies tabs bounded to 4 pages by `FilteredTopUpMaxPages=3` (commit `fc684ced`)
- **Found:** Pass 29 (2026-09-20) — re-confirmed Pass 35
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

**Re-verification evidence (Pass 35, 2026-09-20, andrew, deployed `bb28dcf`):** fresh load of `/profile` → "Your posts" fired `outbox` **13 times** (pages 1–13) before rendering; only ~3 items shown. Switched to **Replies** tab → fired the **full 13-page outbox fan-out again** (requests 95–107, identical sequence). Replies tab rendered an empty list. Still 26 outbox requests for 2 tabs viewed. STILL OPEN.

**Re-verification evidence (Pass 38, 2026-09-20, andrew, rebuilt container post-`bdc0e66`):** fresh load of `/profile` → "Your posts" fired `outbox` **4 times** (pages 1–4) before rendering (reduced from 13, but still a multi-page fan-out, not single-page). Switched to **Replies** tab → fired the **full 13-page outbox fan-out again** (requests 86–98, pages 1–13). Replies tab rendered an **empty list** (no "No replies yet" message). Switched to **Likes** tab → fired the **full 13-page outbox fan-out a third time** (requests 99–111, pages 1–13). Total: **29 outbox requests** for 3 tabs viewed. STILL OPEN (reduced on initial load but tab-switch fan-out unchanged).

**Re-verification evidence (Pass 39, 2026-09-20, andrew, deployed `4f5dd5c`):** fresh load of `/profile` → "Your posts" fired `outbox` **6 times** (pages 1–6) before rendering. Switched to **Replies** tab → fired the **full 13-page outbox fan-out again** (requests 88–100, pages 1–13). Replies tab rendered an **empty list**. Total: **19 outbox requests** for 2 tabs viewed. STILL OPEN (initial load reduced to 6 pages but tab-switch fan-out still pulls all 13 pages).

**Re-verification evidence (Pass 41, 2026-09-20, andrew, deployed `59ff4ec`):** fresh load of `/profile` → "Your posts" fired `outbox` **8 times** (pages 1–6, plus duplicates of 1–2, plus 2 ERR_ABORTED on pages 4–5). Switched to **Replies** tab → fired the **full 13-page outbox fan-out again** (requests 92–108, pages 1–13, with 3 ERR_ABORTED on pages 7–9). Total: **19+ outbox requests** for 2 tabs viewed. STILL OPEN (initial load still multi-page fan-out, tab-switch still pulls all 13 pages).

**Re-verification evidence (Pass 42, 2026-09-20, andrew, deployed `65ccfa0`):** fresh load of `/profile` → "Your posts" fired `outbox` **10 times** (pages 1–8, with 4 ERR_ABORTED on pages 4, 5, 6, 7). Switched to **Replies** tab → fired the **full 13-page outbox fan-out again** (requests 97–109, pages 1–13). Total: **21 outbox requests** for 2 tabs viewed. STILL OPEN (initial load multi-page fan-out with aborts, tab-switch still pulls all 13 pages).

**Re-verification evidence (Pass 43, 2026-09-20, andrew, deployed `65ccfa0`):** fresh load of `/profile` → "Your posts" fired `outbox` **11 times** (pages 1–11, all 200, no aborts this time). Switched to **Replies** tab → fired the **full 13-page outbox fan-out again** (requests 94–106, pages 1–13). Total: **24 outbox requests** for 2 tabs viewed. STILL OPEN (initial load multi-page fan-out, tab-switch still pulls all 13 pages).

**Re-verification evidence (Pass 49, 2026-09-20, andrew, deployed `65ccfa0`):** fresh load of `/profile` → "Your posts" fired `outbox` **8 times** (pages 1–8, all 200). Switched to **Replies** tab → fired the **full 13-page outbox fan-out** (requests 91–103, pages 1–13). Switched to **Likes** tab → fired the **full 13-page outbox fan-out a third time** (requests 104–116, pages 1–13). Switched back to **Your posts** → no additional outbox requests (cached). Total: **34 outbox requests** for 3 tabs viewed. Replies tab rendered an **empty list** (no "No replies yet" message). Likes tab rendered an **empty list** with a "Load more" button (no likes to load). STILL OPEN (initial load multi-page fan-out, tab-switch still pulls all 13 pages each time).

**Re-verification evidence (Pass 61, 2026-09-20, andrew, deployed `65ccfa0`):** fresh load of `/profile` → "Your posts" fired `outbox` **4 times** (pages 1–4, all 200). Switched to **Replies** tab → fired the **full 13-page outbox fan-out** (requests 87–99, pages 1–13). Switched to **Likes** tab → fired the **full 13-page outbox fan-out a third time** (requests 100–112, pages 1–13). Total: **30 outbox requests** for 3 tabs viewed. Replies tab rendered an **empty list** (no "No replies yet" message). Likes tab rendered an **empty list**. STILL OPEN (initial load reduced to 4 pages, but tab-switch still pulls all 13 pages each time).

**Re-verification evidence (Pass 69, 2026-09-20, andrew, Dev fix deployed):** fresh load of `/profile` → "Your posts" fired `outbox` **6 times** (pages 1–6, all 200). Switched to **Replies** tab → fired the **full 13-page outbox fan-out** (requests 89–101, pages 1–13). Switched to **Likes** tab → fired the **full 13-page outbox fan-out a third time** (requests 102–114, pages 1–13). Total: **32 outbox requests** for 3 tabs viewed. Replies tab rendered an **empty list**. Likes tab rendered an **empty list**. STILL OPEN (initial load increased to 6 pages, tab-switch still pulls all 13 pages each time).

**Re-verification evidence (Pass 76, 2026-09-20, andrew, Dev fix deployed):** fresh load of `/profile` → "Your posts" fired `outbox` **9 times** (pages 1–9, all 200). Switched to **Replies** tab → fired the **full 13-page outbox fan-out** (requests 92–104, pages 1–13). Switched to **Likes** tab → fired the **full 13-page outbox fan-out a third time** (requests 105–117, pages 1–13). Total: **35 outbox requests** for 3 tabs viewed. Replies tab rendered an **empty list** (no "No replies yet" message). Likes tab rendered an **empty list** with a "Load more" button. STILL OPEN (initial load increased to 9 pages, tab-switch still pulls all 13 pages each time).

**Re-verification evidence (Pass 80, 2026-09-20, andrew, Dev fix deployed, container 12:15:50):** fresh load of `/profile` → "Your posts" fired `outbox` **9 times** (pages 1–9, all 200). Switched to **Replies** tab → fired the **full 13-page outbox fan-out** (requests 92–104, pages 1–13). Switched to **Likes** tab → fired the **full 13-page outbox fan-out a third time** (requests 105–117, pages 1–13). Total: **35 outbox requests** for 3 tabs viewed. Replies tab rendered an **empty list**. Likes tab rendered an **empty list** with a "Load more" button. **Identical to Pass 76** — no change. STILL OPEN.

**Re-verification evidence (Pass 89, 2026-09-20, andrew, container 12:43:01):** fresh load of `/profile` → "Your posts" fired `outbox` **9 times** (pages 1–9, all 200). Switched to **Replies** tab → fired the **full 13-page outbox fan-out** (requests 92–104, pages 1–13). Switched to **Likes** tab → fired the **full 13-page outbox fan-out a third time** (requests 105–117, pages 1–13). Total: **35 outbox requests** for 3 tabs viewed. Replies tab rendered an **empty list**. Likes tab rendered an **empty list** with a "Load more" button. **Identical to Pass 76 and Pass 80** — no change. STILL OPEN.

**Re-verification evidence (Pass 90, 2026-09-20, andrew, container 12:53:13):** fresh load of `/profile` → "Your posts" fired `outbox` **11 times** (pages 1–11, all 200). Switched to **Replies** tab → fired the **full 14-page outbox fan-out** (requests 94–106, pages 1–14). Switched to **Likes** tab → fired the **full 14-page outbox fan-out a third time** (requests 107–119, pages 1–14). Total: **39 outbox requests** for 3 tabs viewed. Replies tab rendered an **empty list**. Likes tab rendered an **empty list** with a "Load more" button. **Increased from 35 to 39** (more outbox items exist now). STILL OPEN.

**Re-verification evidence (Pass 91, 2026-09-20, andrew, container 13:19:52):** fresh load of `/profile` → "Your posts" fired `outbox` **11 times** (pages 1–11, all 200). Switched to **Replies** tab → fired the **full 14-page outbox fan-out** (requests 94–106, pages 1–14). Switched to **Likes** tab → fired the **full 14-page outbox fan-out a third time** (requests 107–119, pages 1–14). Total: **39 outbox requests** for 3 tabs viewed. **Identical to Pass 90** (39 outbox requests). STILL OPEN.

**Re-verification evidence (Pass 92, 2026-09-20, andrew, container 13:26:35):** fresh load of `/profile` → "Your posts" fired `outbox` **11 times** (pages 1–11, some with ERR_ABORTED). Switched to **Replies** tab → fired the **full 14-page outbox fan-out** (requests 105–117, pages 1–14). Switched to **Likes** tab → fired the **full 14-page outbox fan-out a third time** (requests 118–130, pages 1–14). Total: **39 outbox requests** for 3 tabs viewed. **Identical to Pass 90/91.** STILL OPEN.

**Re-verification evidence (Pass 93, 2026-09-20, andrew, container 13:26:35):** Same container as Pass 92. S17 not re-tested this pass (focused on S22 root cause). **Still OPEN** (39 outbox requests for 3 tabs, unchanged since Pass 90).

**Re-verification evidence (Pass 94, 2026-09-20, andrew, container 13:26:35):** Not re-tested this pass (focused on session/hydration exploration). **Still OPEN** (unchanged since Pass 90).

**Re-verification evidence (Pass 96, 2026-09-20, andrew, container 14:09:44):** fresh load of `/profile` → "Your posts" fired `outbox` **11 times** (pages 1–11). Switched to **Replies** tab → fired the **full 14-page outbox fan-out** (requests 94–106). Switched to **Likes** tab → fired the **full 14-page outbox fan-out a third time** (requests 107–119). Total: **39 outbox requests** for 3 tabs viewed. **Identical to Pass 90/91/92.** STILL OPEN.
