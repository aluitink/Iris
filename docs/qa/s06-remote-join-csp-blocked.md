# S6 — Join on a remote community is a silent no-op (CSP-blocked browser POST)

- **Class:** bug — **Severity:** S2
- **Status:** closed (QA re-verified live 2026-09-22 Pass 264 on the fresh build rebuilt from `main` HEAD — remote Join/Leave drives a server-side Follow/Undo, no CSP-blocked browser POST, 0 console errors)
- **Found:** Pass 12 (2026-09-20) — re-confirmed Passes 14, 15; fixed in Inbox ② Phase 4 (Join/Leave → Follow/Undo), live-verified Pass 27
- **Related:** folded into [Inbox ② community simplification](../plans/community-simplification.md)

## Symptom

On a remote community detail page (which shows **both** "Unfollow" *and* "Join"), clicking **Join** fires a **direct browser `POST` to the remote `/inbox`** (`https://lemmy.luit.ink/c/interop/inbox`) → **CSP-blocked** (`connect-src 'self'`, 2 console errors) → button **stays "Join"** (no "Leave", no error).

## Root cause

`RequestJoinAsync` → `DeliverAsync(communityIri.InboxOf(), …)` at `ActivityPubClient.cs:374` delivers the Join from the browser to the remote inbox, which the page's CSP forbids. `JoinButton.razor:119-122` has an empty `catch` that swallows the failure → silent no-op.

## Fix

Route Join/Leave through a **server-side** endpoint — the server owns the recipient hop (like `FollowAsync` posts to the actor's own outbox) — and surface the error in `JoinButton`.

Implemented in Inbox ② Phase 4 ("Join/Leave drive Follow/Undo", commit `68ae703`): the remote Join now drives a server-side Follow/Undo instead of a direct browser POST; `JoinButton.razor` was deleted in favor of the single Follow/Join/Leave button.

## Re-verify (after rebuild)

Rebuild the container, then on a remote community: Join → button flips to Leave, no console errors, the follow edge exists server-side; Leave round-trips back.

**Re-verification evidence (Pass 27, 2026-09-20, andrew):** the remote community `lemmy.luit.ink/c/interop` now shows a **single "Unfollow"** button (Join/Leave are gone — the old double-button symptom is resolved), with **0 console errors** and no direct browser POST to the remote inbox. FIXED.

**Re-verification evidence (Pass 264, 2026-09-22, ii-b1@B, fresh build rebuilt from `main` HEAD):** A genuine **remote** community was available on the stack (A's `qa-pass261-test` community, cached on B under A's IRI `https://qa-iris-a.luit.ink/ap/v1/c/qa-pass261-test`). Logged into B as `ii-b1` (which did NOT follow it) and opened the community detail:
1. The page shows a **single "Join"** button (the old double-button "Unfollow + Join" symptom is resolved).
2. Clicking **Join** → button flips to **"Leave"**; the network shows `POST https://qa-iris-b.luit.ink/ap/v1/u/ii-b1/outbox` → **202** (a **server-side Follow from ii-b1's own outbox** — NOT a direct browser POST to A's `/inbox`). All remote community reads go through `POST /ap/v1/proxy/…` (A's community doc / members / feed). **0 console errors** (no CSP-blocked cross-origin POST).
3. DB confirms the follow edge landed on B: `Edges` Kind=0 `ii-b1 → …/qa-pass261-test` (CreatedAt 04:57:27).
4. Clicking **Leave** → button flips back to **"Join"**; the B-side follow edge is **removed** (state restored, empty query). **0 console errors.**
**CLOSED** — the remote Join/Leave fix (`68ae703`) is live and correct on the fresh build: server-side Follow/Undo, no CSP-blocked browser POST, clean round-trip.
