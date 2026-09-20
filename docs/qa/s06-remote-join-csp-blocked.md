# S6 — Join on a remote community is a silent no-op (CSP-blocked browser POST)

- **Class:** bug — **Severity:** S2
- **Status:** fixed (2026-09-20, verified Pass 27 against the 2026-09-20 03:31 UTC rebuild; deployed-commit label in PLAN.md was stale, see Pass 27 note)
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
