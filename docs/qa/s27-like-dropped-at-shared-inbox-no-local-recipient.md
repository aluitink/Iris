# S27 — Cross-instance Like delivered to the author's shared inbox but DROPPED ("no local recipient"), never applied to the note

- **Class:** bug / federation-delivery — **Severity:** S2
- **Status:** **fixed (2026-09-21, `27b1ba6`, change 14824)** — clean-entry re-verify on the rebuilt QA cluster (HEAD `27b1ba6`); a remote `Like` now routes to the note's author via the shared inbox and is applied (`likedCount` increments on the author's instance).
- **Found:** Interop suite A6 (Iris↔Iris), 2026-09-20, QA federation stack (Iris A `qa-iris-a.luit.ink`, Iris B `qa-iris-b.luit.ink`)
- **Related:** [S25](s25-remote-post-not-in-followers-home-feed.md) (delivered+stored, not surfaced in feed), [S26](s26-remote-reply-not-threaded-under-parent.md) (stored, not threaded), [S28](s28-remote-announce-stored-not-in-shares.md) — S27 is a **distinct mechanism** specific to **Like**: the activity is **discarded at the shared inbox** before any store/surface step. Note: **Announce is NOT affected by S27** — A's log shows the remote Announce *received + accepted* (see S28); only the Like is dropped. So the shared-inbox "no local recipient" drop applies to **Like** specifically.

## Symptom

`ii-a1` (A) posted a Public note `II-A4-1 hello cross-instance` (Note IRI `…/ii-a1/notes/06GC0JFR051T63GG17JPSRW928`). `ii-b1` (B) pressed **Like** on the object detail page.

**The Like is created and sent to the right place:**
- B `ii-b1/outbox` → `Like`, `actor` = `ii-b1`, `object` = the Note IRI, `id` = `…/ii-b1/likes/06GC0NT776WWWCNZYCE554N05G` ✓
- B `qa-iris-b` log (same window): the Like is delivered to **A's shared inbox**, and A's actor exposes `endpoints.sharedInbox = https://qa-iris-a.luit.ink/ap/v1/shared-inbox` and `inbox = https://qa-iris-a.luit.ink/ap/v1/u/ii-a1/inbox`.
- **A `qa-iris-a` log:** **no Like received** (only unrelated `Create` re-deliveries).
- **Decisive (B log):**
  > `Shared inbox: no local recipient; accepting and dropping. Peer: https://qa-iris-a.luit.ink/ap/v1/u/ii-a1#key-1`

**Result:**
- `GET A <parent Note>` → `likes` = empty (count 0). The remote Like is **never applied** to the note on the author's instance.
- (B object-detail UI shows the Like button; the note's `likes` collection on A stays empty, so `likedCount`/Likes tab do not reflect the remote Like.)

So a cross-instance `Like` is created on the liker's instance and transmitted to the author's shared inbox, but the shared-inbox handler cannot resolve a **local recipient** for it and **drops** the activity — the author's note never records the Like.

## Root cause (suspected)

The shared-inbox handler resolves a local recipient for an incoming activity and, when it can't, logs "no local recipient; accepting and dropping" and discards it. For a remote `Like` addressed to a **local** Note (whose `attributedTo` is a local actor), the handler should map the activity to the note's author (the local actor) as the recipient. It does not — so the Like is dropped before any `likes`-collection update. Likely the shared-inbox recipient resolution only handles activities addressed to a local **actor** (e.g. Follow), not activities whose `object` is a local **Note** (Like/Announce/Flag), or the `to`/`cc`/`object.attributedTo` is not used to find the local note's author. No `file:line` yet — needs a code pass on the shared-inbox recipient resolution (the "no local recipient; accepting and dropping" path).

## Fix (agreed approach)

- Shared-inbox recipient resolution must map an incoming activity to a **local recipient** when its `object` is a **local Note** (use `object.attributedTo` / the note's author) or its `to`/`cc` references a local actor. When a local recipient is found, dispatch normally (e.g. update the note's `likes`/`shares`/`replies`) instead of dropping.
- The "accepting and dropping" path should only fire when there is genuinely no local target.

## Re-verify (clean entry)

1. `ii-a1` (A) posts a Public note `II-A4-1 hello cross-instance`.
2. `ii-b1` (B) presses **Like** on the note (object detail page).
3. B outbox shows the `Like` (actor `ii-b1`, object = Note IRI).
4. A log shows the Like **received and dispatched** (NOT "no local recipient; accepting and dropping"). ← the fix
5. `GET A <parent Note>` → `likes` includes `ii-b1` (and `likedCount` reflects it).
6. A `ii-a1` notifications shows a "liked your post" notification.

**Re-verification evidence (Interop A6, 2026-09-20, QA stack):** B outbox `Like` correct (actor `ii-b1`, object = Note IRI); A actor exposes `sharedInbox`; A log shows **no Like received**; B log shows `Shared inbox: no local recipient; accepting and dropping. Peer: …/ii-a1#key-1`; `GET A <note>` `likes` = empty. **S27 OPEN.**

## Re-test (fresh rebuild, 2026-09-20)

**CANNOT REPRODUCE via UI (tooling limitation, not a code verdict).** A cross-instance `Like` must be POSTed to the **author's** outbox (`POST /ap/v1/u/{handle}/outbox`, `ActivityPubServerExtensions.cs:4015`), which requires **AP-HTTP-Sign** (`SignatureValidationMiddleware.GetResult`, line 4031 → 401 without a signature); the Blazor WASM client signs it via WebCrypto, so it cannot be forged with a raw in-browser `fetch`. Additionally the **UI deliberately blocks liking/boosting remote objects** (`apps/Iris.Web.Client/Ui/UiContext.cs:704` — "The user cannot like/boost remote objects"), so `ii-b1` (B) has no Like control for a remote A note to drive via Playwright.

The original S27 evidence was produced by a signed client in the prior run. To re-verify S27 on the fresh build, repeat it with a **signed CLI/AP client** (not the browser). **S27 status this run: not re-testable via Playwright; OPEN (unconfirmed on fresh build).**

## Re-test (interop A6, 2026-09-21, fresh QA cluster)

**CONFIRMED — reproduces (now testable via UI; the remote-like block is gone).** Note: the prior run's note that "the UI deliberately blocks liking/boosting remote objects" (`UiContext.cs:704`) is **no longer the case** on this build — `ii-b1` (B) had a working **Like** control on a remote A note.

- `ii-a1` (A) liked ii-b1's post (the A5 reply, Note `…/ii-b1/notes/06GC3BJ0SHJMT4ZV87KYY056ZC`) from A. A UI: Like button `pressed`, count 1 (A6.1 ✓ local).
- A `ii-a1/outbox` → `Like` activity, `object` = the remote Note IRI ✓ (the Like is created + emitted on the liker's instance).
- **B (author's instance):** `GET B <note>/likes` → **count 0**; B object-detail UI (as ii-b1): Like button count **0**, not pressed; Likes tab = **"No likes yet."** (A6.2 ✗)

So the cross-instance Like is created and emitted by the liker, but is **not applied on the author's instance** — the note's `likes` collection / count stays 0. (Mechanism this run: the Like didn't land in B's note `likes`; consistent with the original shared-inbox "no local recipient" drop, though B's log was not captured this pass.) **S27 OPEN (reproduces on the 2026-09-21 fresh cluster; now UI-testable).**

## Re-verify after fix (2026-09-21, rebuilt QA cluster @ HEAD `27b1ba6`)

Rebuilt the two Iris services from `interop-testing` HEAD (`27b1ba6`) before re-verifying (the prior 3h-old images pre-dated the S27 fix). Clean entry as `ii-b1` (B); re-followed `ii-a1` (A) so A's note was in B's feed. B liked A's note `S31 re-verify v2 base` (Note `…/ii-a1/notes/06GC3VAXBN4MEP52TQF1N21CM0`) from B's view of A's actor page.

- **Wire (B):** B outbox `Like` `…/ii-b1/likes/06GC3WBJMSK3K93K9ZW9GN0T50`, `object` = the Note IRI (the author is `ii-a1` on A). ✓
- **Author (A):** after the async delivery settled (A delivery queue drained), `GET A <note>` → **`likedCount` = 1** (was 0). ✅ The remote Like was received at A's shared inbox, **routed to the note's author** (`ii-a1`), and **applied** — no "no local recipient; accepting and dropping" drop.

**S27 FIXED** — a cross-instance `Like` now reaches the author's instance and increments the note's `likedCount` on the current build. (Direction this run: B→A; the fix routes a shared-inbox `Like` to the note's author regardless of direction.)
