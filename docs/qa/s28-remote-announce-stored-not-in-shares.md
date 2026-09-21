# S28 — Remote Announce (Boost) delivered+stored on the author, but NOT surfaced in the note's `shares`

- **Class:** bug / data-integrity — **Severity:** S2
- **Status:** **PARTIALLY FIXED (Pass 112, current build)** — the Pass-102 **regression (Announce dropped at the shared inbox "no local recipient") is RESOLVED**: the remote Announce is now **delivered + accepted + stored** (`AnnounceActivityHandler processed … ok`, `Inbox accepted`), and it's **surfaced** — `GET A <note>/shares` → 200 (contains the boost) + the **Shares tab lists the booster (ii-b1)**. **Remaining (count facet):** the author's `shares`/`sharedCount` are still **None** and the note `shares.totalItems` = **0** (the **count is not materialized** — the same count-materialization pattern as [S37](s37-remote-like-stored-but-likedcount-not-materialized.md) for Likes); the author's `/shares` endpoint 404s. **Status: OPEN (narrowed) — count materialization only.**
- **Found:** Interop suite A7 (Iris↔Iris), 2026-09-20, QA federation stack (Iris A `qa-iris-a.luit.ink`, Iris B `qa-iris-b.luit.ink`)
- **Related:** [S25](s25-remote-post-not-in-followers-home-feed.md) (delivered+stored, not surfaced), [S26](s26-remote-reply-not-threaded-under-parent.md) (stored, not threaded) — same family. **Distinct from [S27](s27-like-dropped-at-shared-inbox-no-local-recipient.md):** the Announce is **received + accepted** by A (not dropped at the shared inbox as the Like is).

## Symptom

`ii-a1` (A) posted a Public note `II-A4-1 hello cross-instance` (Note IRI `…/ii-a1/notes/06GC0JFR051T63GG17JPSRW928`). `ii-b1` (B) pressed **Boost**.

**The Announce is created, delivered, and accepted:**
- B `ii-b1/outbox` → `Announce`, `actor` = `ii-b1`, `object` = the Note IRI, `id` = `…/ii-b1/announces/06GC0PAK5ZQ7T62FPW3VEM983R` ✓
- **A `qa-iris-a` log** (decisive — *not* dropped, unlike the Like in S27):
  - `Inbox received Announce https://qa-iris-b.luit.ink/ap/v1/u/ii-b1/announces/06GC0PAK5ZQ7T62FPW3VEM983R … to https://qa-iris-a.luit.ink/ap/v1/u/ii-a1`
  - `Handler AnnounceActivityHandler processed Announce … — ok`
  - `Inbox accepted: Announce from …/ii-b1 targeting …/notes/06GC0JFR051T63GG17JPSRW928. Recipient: …/ii-a1, Peer: qa-iris-b.luit.ink`

**But it is not surfaced:**
- `GET A <parent Note>` → `shares` = **empty** (count 0), even though `sharedCount` = **1** (local count is incremented) and the Announce was accepted.
- So the remote Boost is accepted/stored on the author, but the note's `shares` collection does not include the booster (the Shares tab / `shares` wire is empty on A).

## Root cause (suspected)

`AnnounceActivityHandler` accepts the remote Announce and increments the local `sharedCount`, but does not record the booster in the note's `shares` `OrderedCollection` (or the `shares` query does not join stored remote Announces). Compare S26 (`replies`) and S25 (feed): stored/accepted remote objects are not surfaced in their note-level collections. No `file:line` yet — needs a code pass on `AnnounceActivityHandler` (does it append to `shares`?) and the `shares` collection query.

## Fix (agreed approach)

- When a remote `Announce` is accepted for a **local** Note, record the announcer in that Note's `shares` `OrderedCollection` (and the Shares tab / `shares` endpoint must return it), consistent with the local `sharedCount` that is already incremented.

## Re-verify (clean entry)

1. `ii-a1` (A) posts a Public note `II-A4-1 hello cross-instance`.
2. `ii-b1` (B) presses **Boost**.
3. A log shows `Inbox accepted: Announce … Recipient: ii-a1@A`.
4. `GET A <parent Note>` → `shares` includes `ii-b1` (Shares tab shows the boost). ← the fix
5. `sharedCount` stays consistent with the `shares` items.

**Re-verification evidence (Interop A7, 2026-09-20, QA stack):** B outbox `Announce` correct (actor `ii-b1`, object = Note IRI); A log shows Announce received + processed ok + **accepted** (not dropped); `GET A <note>` `shares` = empty but `sharedCount` = 1. **S28 OPEN.**

## Re-test (fresh rebuild, 2026-09-20)

**CANNOT REPRODUCE via UI (same tooling limitation as S27).** A cross-instance `Announce`/Boost is POSTed to the **author's** outbox (`POST /ap/v1/u/{handle}/outbox`), which requires **AP-HTTP-Sign** and cannot be forged with a raw in-browser `fetch`. The **UI also blocks boosting remote objects** (`apps/Iris.Web.Client/Ui/UiContext.cs:704`), so `ii-b1` (B) has no Boost control for a remote A note to drive via Playwright. The original S28 evidence came from a signed client. To re-verify on the fresh build, repeat with a **signed CLI/AP client**. **S28 status this run: not re-testable via Playwright; OPEN (unconfirmed on fresh build).**

## Re-test (interop A7, 2026-09-21, fresh QA cluster)

**PARTIALLY IMPROVED — reproduces (now testable via UI; the remote-boost block is gone).** Note: the prior run's note that "the UI blocks boosting remote objects" (`UiContext.cs:704`) is **no longer the case** — `ii-b1` (B) had a working **Boost** control on a remote A note.

- `ii-b1` (B) boosted ii-a1's post (Note `…/ii-a1/notes/06GC3AWSHG64NJHJ24EM27HZSW`) from B. B UI: Boost button `pressed`, count 1 (A7.1 ✓ local).
- B `ii-b1/outbox` → `Announce` activity, `object` = the remote Note IRI ✓.
- **A (author's instance):** the Announce **did land** — A object-detail UI (as ii-a1) shows **"1 boost"** and the **Shares tab lists ii-b1** (Shares (1)). This is an **improvement** over the original run (where `shares` was empty).
- **But the Boost button count on A stays 0 / not pressed** even though the Shares tab shows the boost and "1 boost" text appears. Also `GET A <note>/shares` wire returned **count 0** (the Shares *collection endpoint* is empty while the object-detail Shares *tab* renders the item) — an inconsistency between the tab and the wire collection.

So the remote Boost now reaches the author (Shares tab populated), but the **button count** and the **`/shares` collection endpoint** do not reflect it. **S28: partially improved (Shares tab now populated) but the button-count / `shares`-endpoint discrepancy remains — OPEN on the 2026-09-21 fresh cluster.**

## Re-verify (Pass 102, 2026-09-21, build `27b1ba6`)

**REGRESSION — the remote Announce is now DROPPED at the shared inbox (not accepted), so nothing surfaces.** This is a *different* (and more severe) failure mode than the earlier "accepted but not surfaced" runs: the shared-inbox routing change made for S27 (Like) / S33 (Undo) now **drops the Announce** because the note's author (a local actor) is not resolved as a local recipient for an inbound Announce.

- `ii-a1` (A) posted `II-A7-3 reverify S28 remote boost shares` (Note `…/ii-a1/notes/06GC48G96XE3WTTV3KK0D39QQ8`, `to`=Public).
- `ii-b1` (B) pressed **Boost** from B (B outbox → `Announce`, `actor`=ii-b1, `object`=the Note IRI; B UI Boost pressed, count 1 — local side ✓).
- **A (author's instance) log:** `Shared inbox: no local recipient; accepting and dropping. Peer: …/ii-b1#key-1` — the Announce is **dropped**, not accepted, and **no `AnnounceActivityHandler processed` line** appears (unlike the original run, which logged `Handler AnnounceActivityHandler processed Announce … ok` + `Inbox accepted`).
- **A note wire:** `GET A <note>` → `shares.totalItems` = **0**, `sharedCount` = **None** (no local count increment at all); `GET A <note>/shares` → `totalItems` 0.
- **A object-detail UI (as ii-a1):** Boost button count **0**, Shares tab = **"No boosts yet."**

**Verdict (build `27b1ba6`): S28 OPEN — REGRESSED.** The remote Boost is now **dropped at the shared inbox** ("no local recipient"), so the author's `shares` / `sharedCount` / Shares tab are all empty. This is the same class as S27 (Like dropped at the shared inbox) — the shared-inbox recipient resolution does not route an inbound `Announce` (or `Like`) to the **local note author**. The earlier "partially improved" state (Shares tab populated, 2026-09-20 fresh cluster) is **gone** on the current build. The S28 fix in dev's uncommitted WIP (`AnnounceActivityHandler` + `/shares`) has **not** been deployed to the QA cluster, so this re-verify is against the pre-fix build `27b1ba6`.

## Re-verify (Pass 112, 2026-09-21, current build `11fbec6`/`aebe420`)

**The Pass-102 REGRESSION is RESOLVED — the remote Announce is delivered + accepted + stored, and now surfaced (Shares tab + `/shares` endpoint). Only the count is not materialized.** (The S28 fix — shared-inbox routing of an inbound `Announce` to the local note author + the `/shares` endpoint — is now **deployed**; it was the uncommitted WIP in Pass 102.)

- `ii-a1` (A) posted a fresh Public note `II-S28 reverify remote boost shares (fresh, Pass 112)` (Note `…/ii-a1/notes/06GC4RR4CN76NCSW2WJKQ3BPZW`).
- `ii-b1` (B) pressed **Boost** from B (B outbox → `Announce`, `actor`=ii-b1, `object`=the Note IRI).
- **A (author's instance) log (decisive — NOT dropped, the Pass-102 failure mode is gone):**
  - `Inbox received Announce …/ii-b1/announces/06GC4RXBM9… from …/ii-b1 to …/ii-a1`
  - `Handler AnnounceActivityHandler processed Announce … — ok`
  - `Inbox accepted: Announce from …/ii-b1 targeting …/notes/06GC4RR4…. Recipient: …/ii-a1, Peer: qa-iris-b.luit.ink`
  - (a `re-delivery … skipping dispatch` line follows — idempotency working)
- **Surfaced (the original S28 "not in shares" gap is FIXED):**
  - `GET A <note>/shares` → **200**, body contains the boost (`actor`=ii-b1, `object`=the Note IRI, `id`=…/ii-b1/announces/06GC4RXBM9…) ✓
  - **A object-detail UI Shares tab → lists ii-b1** (the booster) ✓
- **Still NOT materialized (the remaining count facet):**
  - `GET A /u/ii-a1` → `shares` = **None**, `sharedCount` = **None** (the actor's shares collection + count are absent)
  - `GET A <note>` → `shares.totalItems` = **0** (the note's embedded count is 0 despite the stored boost)
  - **Boost button count = 0** (not pressed)
  - `GET A /u/ii-a1/shares` (author-level) → **404**

**Verdict (current build): S28 PARTIALLY FIXED.** The shared-inbox drop (Pass-102 regression) is resolved and the boost is now delivered + stored + surfaced in the note's `shares`/Shares tab. **Remaining:** the **count** is not materialized — author `shares`/`sharedCount`=None, note `shares.totalItems`=0, Boost button 0 (and the author-level `/shares` 404s). This is the **same count-materialization pattern as S37** (remote Like stored + `/likes` correct, but `likedCount`=0). **Suggested dev follow-up:** when a remote `Announce` is accepted for a local Note, **increment the note's `shares.totalItems` + the actor's `sharedCount`** (and expose the author's `/shares` collection) so the counts match the stored boost. **Status: OPEN (narrowed) — count materialization only.**
