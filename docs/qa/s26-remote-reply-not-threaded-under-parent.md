# S26 — Remote reply delivered and stored, but NOT threaded under the parent Note's `replies`

- **Class:** bug / data-integrity — **Severity:** S2
- **Status:** open
- **Found:** Interop suite A5 (Iris↔Iris), 2026-09-20, QA federation stack (Iris A `qa-iris-a.luit.ink`, Iris B `qa-iris-b.luit.ink`)
- **Related:** [S25](s25-remote-post-not-in-followers-home-feed.md) (same "remote object delivered+stored but not surfaced" family), [S4](s04-communities-following-remote.md)

## Symptom

`ii-a1` (A) posted a Public note `II-A4-1 hello cross-instance` (Note IRI `…/ii-a1/notes/06GC0JFR051T63GG17JPSRW928`). `ii-b1` (B) replied with `II-A5-1 reply from B`.

**The reply is correctly created and delivered:**
- B `ii-b1/outbox` → `Create → Note`, `content` = `II-A5-1 reply from B`, `inReplyTo` = the parent Note IRI (exact match), `attributedTo` = `ii-b1` ✓
- A `qa-iris-a` log (same window):
  - `Inbox received Create https://qa-iris-b.luit.ink/ap/v1/u/ii-b1/creates/06GC0M7BDT33KQTBCYV0PD1XQ0 … to https://qa-iris-a.luit.ink/ap/v1/u/ii-a1`
  - `Handler CreateActivityHandler processed Create … — ok`
  - `Inbox accepted: Create from …/ii-b1 targeting …/notes/06GC0M7BDT33KQTBCYV0PD1XQ4. Recipient: …/ii-a1, Peer: qa-iris-b.luit.ink`
- A can fetch the reply Note via proxy: `GET A /ap/v1/proxy/<reply>` → **200**, `inReplyTo` = parent ✓ (reply Note is **stored** on A).

**But the reply is NOT threaded under the parent:**
- `GET A <parent Note>` → `replies` = `OrderedCollection` with **no items** (empty). The reply, though stored on A with the correct `inReplyTo`, does not appear in the parent's `replies` collection.

So a remote reply is delivered, accepted, and the reply Note is fetchable on the parent's instance, yet the parent Note's `replies` collection stays empty — the thread does not assemble across instances. (UI consequence: even if S25's home feed is fixed, the parent object page would show no replies.)

## Root cause (suspected)

`CreateActivityHandler`, when it receives a remote `Create` whose `object.inReplyTo` references a **local** Note, does not append the reply Note to that local Note's `replies` `OrderedCollection` (or the `replies` collection query does not join stored remote Notes by `inReplyTo`). The reply Note is stored (proxy fetch works) but the threading link to the parent's `replies` is never recorded. No `file:line` yet — needs a code pass on `CreateActivityHandler` (does it record `inReplyTo` → parent `replies`?) and the `replies` collection query.

## Fix (agreed approach)

- When a remote `Create` is accepted whose `object.inReplyTo` is a **local** Note, the reply Note must be added to that Note's `replies` `OrderedCollection` (and the `replies` endpoint must return it), so threads assemble across instances.

## Re-verify (clean entry)

1. `ii-a1` (A) posts a Public note `II-A4-1 hello cross-instance`.
2. `ii-b1` (B) replies `II-A5-1 reply from B`.
3. A log shows `Inbox accepted: Create … Recipient: ii-a1@A`.
4. `GET A <parent Note>` → `replies.orderedItems` **includes** the reply Note (IRI `…/ii-b1/notes/06GC0M7…`), `inReplyTo` = parent. ← the fix
5. The reply Note is also fetchable via `GET A /ap/v1/proxy/<reply>` (already works).

**Re-verification evidence (Interop A5, 2026-09-20, QA stack):** B outbox reply Create correct (`inReplyTo`=parent); A log shows Create delivered + accepted + CreateActivityHandler ok; `GET A /ap/v1/proxy/<reply>` = 200 (reply stored, `inReplyTo`=parent). Yet `GET A <parent Note>` `replies` = empty `OrderedCollection`. **S26 OPEN.**

## Re-test (fresh rebuild, 2026-09-20)

**NOT REPRODUCED — appears fixed on the fresh build.** Same setup: `ii-a1` (A) posted `II-A4-1` (Note `…/ii-a1/notes/06GC1BE5K4VF0KPYEKDJV1HARR`); `ii-a2` (A) replied locally (`II-A5-1`); `ii-b1` (B) replied cross-instance (`II-A5-2`, Note `…/ii-b1/notes/06GC1CF9FTZMHD2CE533RGHFJ0`, `inReplyTo` = the A4 note).
- A log: `Inbox accepted: Create … Recipient: ii-a1` (the B reply delivered + accepted).
- `GET A <parent note>/replies` → `orderedItems` now **includes BOTH** the local reply (`ii-a2`) **and the remote reply** (`ii-b1` `06GC1CF9FTZMHD2CE533RGHFJ0`).

The cross-instance reply is now threaded under the parent. (Note: A5's UI-surfacing assertions remain blocked by S25, but the wire-level threading that S26 reported broken is now correct.) **S26: not reproduced on the fresh build (likely fixed; re-confirm on next regression run).**

## Re-test (interop A5, 2026-09-21, fresh QA cluster)

**CONFIRMED FIXED — not reproduced.** `ii-a1` (A) posted `II-A4-1 hello cross-instance` (Note `…/ii-a1/notes/06GC3AWSHG64NJHJ24EM27HZSW`); `ii-b1` (B) replied `II-A5-1 reply from B` (Note `…/ii-b1/notes/06GC3BJ0SHJMT4ZV87KYY056ZC`, `inReplyTo` = the parent Note IRI).
- `GET A <parent Note>/replies` → `orderedItems` **includes** the remote reply (ii-b1's note), `inReplyTo` = parent.
- A object-detail UI (as ii-a1) renders the reply nested under the parent ("In reply to ii-a1").

Cross-instance reply threading is correct. **S26: FIXED (not reproduced on the 2026-09-21 fresh cluster).**

## Re-test (Pass 154, 2026-09-21, build `38ae87c`) — S26 re-confirmed FIXED

- The existing cross-instance reply **II-S26-5** (B note `…/ii-b1/notes/06GC6337X9…`, `inReplyTo` = A parent `…/ii-a1/notes/06GC5MR7…`, `attributedTo` = ii-b1) is still threaded correctly on `38ae87c`: `GET A <parent Note>/replies` → `totalItems`=1, `orderedItems` **includes** the remote reply IRI `06GC6337X9` (the B reply), `inReplyTo` intact.
- **S26: FIXED (re-confirmed on `38ae87c`).**
