# S25 — Remote post delivered to the follower's inbox but NOT surfaced in the follower's home feed

- **Class:** bug / data-integrity — **Severity:** S2
- **Status:** FIXED (dev2, live-verified 2026-09-22) — `GetDeliveredContentAsync` in `FeedService` includes delivered remote content in the follower's feed. Live-verified on dev2 stack: B follows A (cross-instance), A's post appears in B's home feed.
- **Found:** Interop suite A4 (Iris↔Iris), 2026-09-20, QA federation stack (Iris A `qa-iris-a.luit.ink`, Iris B `qa-iris-b.luit.ink`)
- **Related:** [S18](s18-local-follow-timeline-empty.md) (local follow → empty timeline — same "followed post not in home feed" family, cross-instance here), [S24](s24-cross-instance-follow-state-inconsistent.md) (same A2 follow setup), [S4](s04-communities-following-remote.md)

## Symptom

Fresh instances A, B. `ii-a1` (A) and `ii-b1` (B) follow each other (A2). `ii-a1` (A) composes a **Public** note with body exactly `II-A4-1 hello cross-instance`.

**The post is correctly published AND correctly delivered** to the follower:
- A `ii-a1/outbox` → `Create → Note`, `content` = `II-A4-1 hello cross-instance`, `to` = `as#Public`, `cc` = `ii-a1/followers`, `attributedTo` = `ii-a1` ✓
- B `qa-iris-b` log (same window):
  - `Inbox received Create https://qa-iris-a.luit.ink/ap/v1/u/ii-a1/creates/06GC0JFR051T63GG17JPSRW924 … to https://qa-iris-b.luit.ink/ap/v1/u/ii-b1`
  - `Handler CreateActivityHandler processed Create … — ok`
  - `Inbox accepted: Create from …/ii-a1 targeting …/notes/06GC0JFR051T63GG17JPSRW928. Recipient: …/ii-b1, Peer: qa-iris-a.luit.ink`
- B can fetch the note via proxy: `GET B /ap/v1/proxy/<note>` → **200** (full Note doc).

**But the post is NOT in `ii-b1`'s home feed:**
- B UI `/home` → **"Your timeline is empty. Follow people to see their posts here."** (persists after hard reload + 10 s retry; 0 console errors).
- B feed wire `GET B /ap/v1/u/ii-b1/feed` (and `?source=people`) → `OrderedCollection`, `totalItems: 2`, `orderedItems` = **two `Follow` activities only** (`ii-a1→ii-b1`, `ii-b1→ii-a1`). The `Create` for `II-A4-1` is **absent from the feed** even though it was accepted into the inbox.

So the follower's home feed is composed only of Follow events; the delivered remote Note is not included. The post is unreachable in the follower's timeline even though it is in the inbox and fetchable by IRI.

## Root cause (suspected)

The home-feed query for a user assembles the feed from a subset of inbox/stored objects that **excludes remote `Create`-delivered Notes** — it currently surfaces only `Follow` activities. The `CreateActivityHandler` processes and stores the Note (proxy fetch works, log says "ok"/"accepted"), but the feed-composition step does not include objects delivered via a remote `Create` addressed to the user (or does not join the user's followed-actor set to the stored remote Notes). Compare S18 (local followed post not in home timeline).

No `file:line` yet — needs a code pass on the `feed` endpoint's query (why only Follow activities are returned) and how `CreateActivityHandler` stores the Note relative to the feed source.

## Fix (agreed approach)

- The user's home `feed` must include **Notes delivered via remote `Create`** from actors the user follows (in addition to the user's own posts and local follows), ordered by `published`.
- Ensure `CreateActivityHandler` stores the remote Note such that the feed query can see it (same store/path the feed reads).

## Re-verify (clean entry)

1. `ii-a1` (A) and `ii-b1` (B) follow each other.
2. `ii-a1` composes a Public note `II-A4-1 hello cross-instance`.
3. B log shows `Inbox accepted: Create … Recipient: ii-b1@B`.
4. B `/home` (hard reload, +1 retry after 10 s) **shows the post**: body `II-A4-1 hello cross-instance`, author `ii-a1@qa-iris-a.luit.ink`. ← the fix
5. `GET B /ap/v1/u/ii-b1/feed` includes the `Create`/Note (not only Follow activities).
6. Object detail + author profile render on B with no error cards; 0 console errors.

**Re-verification evidence (Interop A4, 2026-09-20, QA stack):** A outbox Create correct; B log shows Create delivered + accepted + CreateActivityHandler ok; B proxy fetch of note = 200. Yet B `/home` = "Your timeline is empty" (after reload + 10 s retry), and `GET B /ap/v1/u/ii-b1/feed` = 2 items, both `Follow`, no `Create`. 0 console errors. **S25 OPEN.**

## Re-test (fresh rebuild, 2026-09-20)

**CONFIRMED — reproduces (original S25-open code).**

Build note: the fresh `up -d --build` (image built 2026-09-20T21:10 UTC) predates the working-tree `src/Iris.Server/Services/FeedService.cs` S25 fix (`GetDeliveredContentAsync`, added 21:58 UTC, **not baked into the image** — verified absent from the running `Iris.Server.dll`). So this re-test ran the **original, unfixed** feed code; S25 reproduces exactly as first found.

`ii-a1` (A) posted `II-A4-1 hello cross-instance` (Note `…/ii-a1/notes/06GC1BE5K4VF0KPYEKDJV1HARR`, `to`=Public, `cc`=followers) while `ii-b1` (B) follows `ii-a1`.
- A outbox `Create → Note` correct.
- B log: `Inbox received Create … to …/ii-b1`; `CreateActivityHandler processed … — ok`; `Inbox accepted: Create from …/ii-a1 targeting …/notes/06GC1BE5K4VF0KPYEKDJV1HARR. Recipient: …/ii-b1, Peer: qa-iris-a.luit.ink` (delivered + accepted).
- B `/home` = **"Your timeline is empty."**
- `GET B /ap/v1/u/ii-b1/feed` = only the two `Follow` activities (no `Create`/Note for the remote post).

Note: B **does** have the A4 note in its object store — `GET B /ap/v1/proxy/<note>` → 200 (the `CreateActivityHandler` stored the embedded Note, `StoreEmbeddedObjectAsync`). The defect is purely the **feed query**, which does not include the delivered remote post (it returns only Follow activities). A working-tree fix (`FeedService.GetDeliveredContentAsync`, uncommitted, not in this build) targets exactly this gap; it has **not** been validated yet (needs a rebuild + re-test). **S25 OPEN (reproduces on a fresh build of the original code; a fix is in progress in the working tree but unbaked/unvalidated).**

## Re-test (interop A4, 2026-09-21, fresh QA cluster)

**CONFIRMED — reproduces.** `ii-a1` (A) posted `II-A4-1 hello cross-instance` (Note `…/ii-a1/notes/06GC3AWSHG64NJHJ24EM27HZSW`, `to`=Public, `cc`=ii-a1/followers) while `ii-b1` (B) follows `ii-a1`.
- A outbox `Create → Note` correct.
- B stored the note: `GET B /ap/v1/u/ii-a1/notes/06GC3AWSHG64NJHJ24EM27HZSW` → **200** (proxy/object fetch works).
- B `/home` (as ii-b1) = **"Your timeline is empty. Follow people to see their posts here."** (0 console errors).
- The remote Note is **not** in ii-b1's home feed despite being delivered + stored.

**S25 OPEN (reproduces on the 2026-09-21 fresh cluster).**
