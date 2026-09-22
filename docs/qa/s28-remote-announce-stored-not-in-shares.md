# S28 — Remote Announce (Boost) delivered+stored on the author, but NOT surfaced in the note's `shares`

- **Class:** bug / data-integrity — **Severity:** S2
- **Status:** **CLOSED — QA re-verified (Pass 273, 2026-09-22, main `1b2e6b62`).** The shared-inbox Announce-drop is RESOLVED (`5355e968`) **and** the Boost's denormalized `sharedCount` materializes immediately. The **cross-instance Boost-button UI facet** (reopened Pass 272) is FIXED by: (1) `3836bebd` — root cause: `IrisNamespaceBase` derived the `iris:` namespace from the WASM's static canonical FQDN (`iris.luit.ink`) instead of the server's effective `BaseUri`, so the bar read `sharedCount` under the wrong namespace and fell back to the walk (0); (2) `18e803ba` — the bar's fast path no longer gates on `!isRemote`; (3) dev2 hardening `c9aeb423` — the server now advertises its effective base on `GET /local/v1/session/public` (`serverBaseUri`), surfaced as a `ServerBaseUri` claim, and `IrisNamespaceBase` **prefers the server-provided base** (authoritative even when the browser dials a different origin), falling back to `_advertiseBase`/`_browserBase`. **Re-verified live on the QA stack (Pass 273):** fresh cross-instance Boost (`ii-a1`@A note → `ii-b1`@B Boost), owner `ii-a1`@A sees **Boost button = 1** == "1 boost" header == Shares tab "(1)", 0 console errors, holds after reload. Wire `sharedCount=1` + `/shares totalItems=1`. **Status: CLOSED.**
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

## Live re-verification (2026-09-22, dev1 two-instance stack, commit `5355e968`)

**Root cause confirmed + fixed.** The shared-inbox recipient resolver (`ActivityPubServerExtensions.SharedInboxHandler`) fanned an inbound `Announce` out to the **announcer's LOCAL followers**. For a cross-instance Boost of a local note the announcer is **remote** (no local followers on the note's home), so the delivery was dropped as *"Shared inbox: no local recipient; accepting and dropping"* and the note's `/shares` + denormalized `sharedCount` stayed 0 — the S28/S37 boost-count facet (and the Pass-102 regression).

**Fix (`5355e968`):** the `Announce` branch now resolves the **announced object's owner** (`ResolveObjectOwnerForDeliveryAsync`, local object read from the object store) and addresses the delivery to the owner — mirroring the `Like` branch. The owner's `AnnounceActivityHandler` then records the announcer→object edge + calls `RefreshObjectCountsAsync` (immediate `sharedCount`), and fans the boost out to the announcer's followers itself (the follower-feed surface the shared inbox no longer duplicates). A remote-object Announce resolves no local owner → accepted + dropped (the boost belongs on the object's home). A Lemmy community relay (embedded `Create` object) is unaffected (not a bare object IRI).

**Live evidence (A `dev1-iris-a.luit.ink`, B `dev1-iris-b.luit.ink`; fresh A note + fresh signed B Boost):**
- A note `…/s37a/notes/06GCE3ZPP6PB3R5919TYA7YBJW`; B `s37b` signed `Announce` → B outbox **202**, minted `…/s37b/announces/06GCE8XQJ4NGN8FG5NW84YND7C` (`to`=s37a, the note's owner — 136.8 audience rewrite intact).
- **A log (decisive — NOT dropped, the Pass-102 failure mode is gone):** `Inbox received Announce …/06GCE8XQJ4… from …/s37b to …/s37a` + `Handler AnnounceActivityHandler processed Announce … — ok` + `Inbox accepted: Announce from …/s37b targeting …/notes/06GCE3ZP…. Recipient: …/s37a, Peer: dev1-iris-b.luit.ink`. (No `Shared inbox: no local recipient; accepting and dropping` line.)
- **Wire (materialized IMMEDIATELY — no 30 s wait, before the fix it was dropped/None):** `GET A <note>` → `…/ns#sharedCount: 1` + `…/ns#likedCount: 1` + `…/ns#score: 1`; `GET A <note>/shares` → `totalItems: 1` (the boost: `actor`=s37b, `object`=the Note IRI).
- **UI facet (CLOSED — see below):** the object-detail **Boost button** now renders the denormalized `sharedCount` and updates live.

## Live UI re-verification (2026-09-22, dev1 stack, Playwright, build includes `5355e968`)

The earlier "Boost button renders 0" was observed on the **older `7620faa1` build, before the count-refresh fix**. On the current dev1 build the object-detail page's `EngagementBar` fast path reads `iris:likedCount`/`iris:sharedCount` off the fetched object doc and the button reflects it live:

- Fresh local account `s3ui`@`dev1-iris-a.luit.ink`; posted a Public note `…/s3ui/notes/06GCEEYB66V6BG4EFKAF82D4Z4` via the UI (compose).
- Opened the object-detail page (`/object?iri=<Note IRI>`). Initial **Like** and **Boost** buttons both rendered **"0"** (correct — no likes/boosts yet) — the bar is on the denormalized fast path, not a 0-embedded-collection fallback.
- Pressed **Like** → button updated to **"1"** + `[pressed]`; wire `GET <note>` → `ns#likedCount: 1` + `ns#score: 1` (immediate).
- Pressed **Boost** → button updated to **"1"** + `[pressed]`; wire `GET <note>` → `ns#sharedCount: 1`; `GET <note>/shares` → `totalItems: 1`; `GET <note>/likes` → `totalItems: 1`.
- **0 console errors** on the object-detail page (the `connect-src 'self'` CSP errors seen earlier were an artifact of driving the WASM client through `127.0.0.1` instead of the FQDN; via the FQDN the same-origin AP fetches succeed).

**Verdict: S28 FIXED — wire `sharedCount` + `/shares` + object-detail Boost-button UI all verified live.**

> **CAVEAT (added Pass 272):** the Boost-button UI verification above was a **local self-Boost** (s3ui@A boosting s3ui's own note, then viewing as s3ui) — the viewer is the booster, so `isShared`/the fast path reflect it. That does **not** cover the **cross-instance** case (a B actor boosts an A note; the A **owner** views). Pass 272 found the button still shows **0** in that case. See below.

## Re-verify (Pass 272, 2026-09-22, QA stack, build carries `5355e968` + `e1e1aa88`)

**Wire fully FIXED; the cross-instance Boost-button UI facet is REOPENED.** Repro: `ii-a1` (A) posted a fresh Public note via the UI (Note `…/ii-a1/notes/06GCG0FRQGDTVK2TZQGH4P821G`); `ii-b1` (B) signed in on B, opened the A note, pressed **Boost** (cross-instance Announce).

**Wire (A) — correct, the `5355e968` fix holds:**
- `GET A <note>` → `…/ns#sharedCount: 1` (and `…/ns#likedCount: 0`); the object doc is a plain `Note` (type Note), top-level `sharedCount=1`, embedded `shares.totalItems=0`.
- `GET A <note>/shares` → `totalItems: 1` (the Announce, `actor`=ii-b1, `object`=the Note IRI).

**UI (A object-detail page, signed in as the owner ii-a1) — the residual defect (live DOM, 0 console errors):**
- **Boost button (`EngagementBar`): `engagement-count` = "0"** (not pressed).
- **Shares tab: "Shares (1)"** and the header summary line: **"1 boost"** — both correct.
- So on the SAME page the button says 0 while the tab/header say 1.

**Analysis (why the button diverges from the tab, both reading `doc.GetSharedCount(ns)`):**
- The page's tab count (`ObjectDetail.razor` `LoadEngagementAsync` fast path) and the header summary read `ObjectDoc.GetSharedCount(ns)` → 1, and both render correctly.
- The `EngagementBar` (`apps/Iris.Web.Client/Components/EngagementBar.razor:193`) seeds `_boostCount` from the **same** `Object.GetSharedCount(ns)` in its fast path (lines 193–202) — which *should* also yield 1.
- Yet the button renders 0. The fast path is skipped when `Ui.IsRemoteObjectIri(iri)` is true, when `ns` is null, or when `Object` is null; for this local A note (same host as the browser origin) none of those should hold. The exact runtime branch that leaves `_boostCount=0` was **not confirmed statically** and needs a dev with runtime tracing (add a log in the `EngagementBar` fast path / fallbacks, or instrument `_boostCount` on render).
- **Hypothesis (dev to confirm):** in the **cross-instance** case the object doc's `sharedCount` extension is present on the **wire** but is either (a) not carried on the in-memory `Object` instance the `EngagementBar` receives (a cached/stale `ObjectDoc` copy, or a deserialization that drops the extension for the bar's path), or (b) the `EngagementBar` takes the `isRemote`/fallback branch and reads the embedded `shares.totalItems=0` instead of the top-level `sharedCount`. The tab's path evidently reads the top-level extension (→1), so the two paths disagree on the same doc.

**Verdict (Pass 272, QA stack):** **wire FIXED; the cross-instance Boost-button UI facet is OPEN (low).** The `5355e968`/`e1e1aa88` wire fix is correct and verified; the remaining gap is purely the object-detail **Boost button** count (0) for a **cross-instance** Boost, diverging from the correct Shares-tab/header count (1) on the same page. **File to dev:** trace the `EngagementBar` fast-path seed for a cross-instance-boosted note (why `_boostCount` stays 0 when `doc.GetSharedCount(ns)=1`).

## Resolved (dev1, 2026-09-22, commits `18e803ba` + `3836bebd`, live-verified on the dev1 two-instance stack)

**Root cause (confirmed via runtime tracing, not the static hypotheses above):** the `EngagementBar` fast path read the counter with `ns = Session.IrisNamespaceBase?.Value`, and `IrisNamespaceBase` derived the namespace from **`_rewriteBase`** — the canonical FQDN baked into the WASM's **static** `appsettings.json` (`https://iris.luit.ink`). But the **server** writes the `iris:` extension counters under its **own** `BaseUri` (`Iris:AdvertiseBase` = the dial host, `https://dev1-iris-a.luit.ink` on the dev1 stack). On any multi-instance / renamed deployment the static FQDN ≠ the dial host, so the bar read `iris:sharedCount` under the **wrong** namespace, `GetSharedCount(ns)` returned **null**, the fast path was skipped, and the bar fell through to the `/shares` walk — which yields **0** for a cross-instance Boost the owner views (the note's embedded `shares.totalItems` is 0), while the Shares tab (which walks the live `/shares` collection) correctly showed 1. The tab's `LoadEngagementAsync` used the **same** `Session.IrisNamespaceBase`, so it too read the wrong namespace — but its fallback (the `GetSharesAsync` walk with `BypassCache`) recovered the true count, which is why the tab was right and the button was wrong. (A temporary `console.log` in the bar's fast path confirmed `ns=https://iris.luit.ink/ns# … sharedCount=null` at runtime — the smoking gun.)

**Fix:**
- `3836bebd` (root cause) — `ActorSessionAccessor.IrisNamespaceBase` now derives the namespace from **`_advertiseBase`** (the effective dial base, which `Program.cs` sets to the browser origin in the multi-instance case — i.e. the server's own `BaseUri`) instead of `_rewriteBase`. `_advertiseBase == _rewriteBase` in the single-instance case, so behavior is unchanged there. This makes the bar (and every other `iris:` extension read) resolve the counters under the namespace the server actually wrote them in.
- `18e803ba` (belt-and-braces) — the `EngagementBar` fast path no longer gates on `!isRemote`, so a **non-Iris remote** note the owner views also prefers the server-rendered `iris:` counters over the note's embedded `shares.totalItems`.

**Live verification (dev1 stack, Playwright, 2026-09-22):** `s28a` (A) posted a fresh Public note (`…/s28a/notes/06GCGE9N3BHFM9EB6PH6B0GYHR`); `s28b` (B) signed in on B, opened the A note, pressed **Boost** (cross-instance Announce, button → pressed, count 1 on B). On A, signed in as the owner `s28a`, the object-detail page now renders **Boost button = 1**, matching the **"1 boost"** header and the **Shares tab** — the three previously-diverging surfaces agree. Wire re-confirmed: `GET A <note>` → `…/ns#sharedCount: 1`; `GET A <note>/shares` → `totalItems: 1`.

**Regression:** `tests/Iris.Web.Tests/LikeCountIntegrationTests.cs::ObjectDocument_SharedCount_IsAuthoritativeBoostCount_RegardlessOfHost` pins that the object document's `iris:sharedCount` (read under the instance's own `{Base}/ns#`) is the authoritative Boost count and diverges from the note's embedded `shares.totalItems` (0) — the exact invariant the button must honor. Fast suite green (Web 114/0, Client 194/0, Server 1441/0, Core 467/0, etc.).

## Re-verify (Pass 273, 2026-09-22, QA stack — dev2 hardening `c9aeb423` + dev1 `3836bebd`/`18e803ba`, merged to main `1b2e6b62`)

**CONFIRMED FIXED on the QA stack.** The QA cluster was rebuilt to `1b2e6b62` (was `a915d9c4`, 2026-09-21 — stale; `src/` carried the S28 fix + S40/S21/S24-D2 since). `GET /local/v1/session/public` now advertises `serverBaseUri=https://qa-iris-a.luit.ink` (the authoritative namespace base the dev2 fix added), and the deployed wasm carries the `ServerBaseUri` claim logic.

Repro (fresh, cross-instance, QA stack): `ii-a1` (A) posted a fresh Public note via the UI — the note's **canonical IRI is the `notes/` form** `…/ii-a1/notes/06GCGV9ZFKC24Q60Z9AQCJTBA0` (the compose `creates/` IRI is the wrapper Activity, not the Note). `ii-b1` (B) signed in on B, opened the A note, pressed **Boost** (cross-instance `Announce`; B log `Inbox accepted: Announce … targeting …/notes/06GCGV9ZFKC24Q60Z9AQCJTBA0`, `Handler AnnounceActivityHandler processed … ok`).

**Wire (A) — correct:** `GET A <note>` → `…/ns#sharedCount: 1`; `GET A <note>/shares` → `totalItems: 1`.

**UI (A object-detail page, signed in as the owner ii-a1, fresh context, 0 console errors) — the Pass-272 defect is GONE:**
- **Boost button (`EngagementBar`): "1"** (was "0" in Pass 272).
- **Header: "1 boost"**; **Shares tab: "Shares (1)"**.
- All three surfaces agree, and it holds after a reload (persistent, not a transient render).

**Note on the `creates/` vs `notes/` IRI:** viewing the note via its `creates/` IRI shows Boost "0" / Shares (no count) because the `creates/` resource is the Create *activity* (which carries no `sharedCount`), not the Note. The Boost correctly targets + surfaces on the Note's canonical `notes/` IRI, which is what the UI links to in practice (outbox/profile/object deep-links resolve to the Note). Not a defect — the owner's real view (the Note) is correct.

**Verdict (Pass 273, QA stack):** **S28 cross-instance Boost-button UI facet CLOSED — re-verified live on the QA stack.** The dev2 hardening (server-advertised `ServerBaseUri` → `IrisNamespaceBase`) makes the client read `iris:sharedCount` under exactly the namespace the server stamped, so the Boost button now matches the header + Shares tab for a cross-instance Boost the owner views. **Status: CLOSED (QA re-verified, Pass 273, main `1b2e6b62`).**
