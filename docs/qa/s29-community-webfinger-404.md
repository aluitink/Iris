# S29 — Community (Group) not resolvable via WebFinger (`acct:!name@host` → 404) even though the Group document exists

- **Class:** bug / discovery — **Severity:** S2
- **Status:** open
- **Found:** Interop suite A8 (Iris↔Iris), 2026-09-20, QA federation stack (Iris A `qa-iris-a.luit.ink`)
- **Related:** A8.1 (community bootstrap). Distinct from the person-actor WebFinger (which works).

## Symptom

`ii-a1` (A) created a community `ii-comm` (name "II Comm"). The Group document is correct and public:

- `GET A /ap/v1/c/ii-comm` → **200**, `type` = `Group`, `id` = `https://qa-iris-a.luit.ink/ap/v1/c/ii-comm`, `preferredUsername` = `ii-comm`, `name` = "II Comm", `attributedTo` (owner) = `…/u/ii-a1`, `members` collection present.

But **WebFinger resolution of the community fails**:
- `GET A /.well-known/webfinger?resource=acct:!ii-comm@qa-iris-a.luit.ink` → **404** (empty body).
- `GET A /ap/v1/webfinger?resource=acct:!ii-comm@…` → **404**.
- Control (works): `GET A /.well-known/webfinger?resource=acct:ii-a1@qa-iris-a.luit.ink` → **200**, `self` → `…/u/ii-a1`.

So a **Group/community** cannot be discovered via WebFinger (`acct:!handle@host`), even though its document exists at `/ap/v1/c/{handle}` and is fetchable by IRI. Person-actor WebFinger is unaffected.

> Note (route clarification from this run): the **working** WebFinger route is the standard `/.well-known/webfinger` (the `/ap/v1/webfinger` path returns 404 for everyone). Earlier suite notes that referenced `/ap/v1/webfinger` were using the wrong route; the 404 there was a test error, not a bug. The community `!` 404 is reproduced on **both** routes, so it is a real Group-resolution gap.

## Root cause (suspected)

The WebFinger handler resolves `acct:handle@host` only for **Person** actors (`/ap/v1/u/{handle}`) and does not handle the community/group form `acct:!handle@host` (`/ap/v1/c/{handle}`). The `!` prefix (or the `c/` namespace) is not mapped in the WebFinger resolver, so it falls through to 404. No `file:line` yet — needs a code pass on the WebFinger resolver (does it branch on the `!` / community namespace to `/ap/v1/c/{handle}`?).

## Fix (agreed approach)

- WebFinger `acct:!{handle}@{host}` must resolve to the community Group document `…/ap/v1/c/{handle}` (mirroring `acct:{handle}@{host}` → `…/ap/v1/u/{handle}`), so remote instances can discover and follow a community.

## Re-verify (clean entry)

1. Create a community `ii-comm` on A.
2. `GET A /ap/v1/c/ii-comm` → 200 Group doc (already works).
3. `GET A /.well-known/webfinger?resource=acct:!ii-comm@qa-iris-a.luit.ink` → **200**, `self` → `…/ap/v1/c/ii-comm`. ← the fix
4. Person webfinger `acct:ii-a1@…` still 200 (no regression).

**Re-verification evidence (Interop A8, 2026-09-20, QA stack):** `GET A /ap/v1/c/ii-comm` = 200 Group doc (correct); `GET A /.well-known/webfinger?resource=acct:!ii-comm@…` = 404; `GET A /ap/v1/webfinger?resource=acct:!ii-comm@…` = 404; control `GET A /.well-known/webfinger?resource=acct:ii-a1@…` = 200. **S29 OPEN.**

## Re-test (fresh rebuild, 2026-09-20)

**CONFIRMED — reproduces.** `ii-a1` created community `ii-comm` on the fresh stack (name "II Community").
- `GET A /ap/v1/c/ii-comm` → **200** Group doc (`id` `…/ap/v1/c/ii-comm`, `preferredUsername` `ii-comm`, `attributedTo` owner `…/u/ii-a1`).
- `GET A /.well-known/webfinger?resource=acct:!ii-comm@qa-iris-a.luit.ink` → **404**.
- Control: `GET A /.well-known/webfinger?resource=acct:ii-a1@qa-iris-a.luit.ink` → **200**.

S29 OPEN (reproduces on a fresh build).

## Re-test (interop A8, 2026-09-21, fresh QA cluster)

**CONFIRMED FIXED — not reproduced.** `ii-a1` created community `ii-a8-community` on A (name "II-A8 Test Community").
- `GET A /ap/v1/c/ii-a8-community` → **200** Group doc (`id` `…/ap/v1/c/ii-a8-community`, `preferredUsername` `ii-a8-community`, owner `…/u/ii-a1`).
- `GET A /.well-known/webfinger?resource=acct:!ii-a8-community@qa-iris-a.luit.ink` → **200** (community resolves via WebFinger). ← the fix
- Control: `GET A /.well-known/webfinger?resource=acct:ii-a1@qa-iris-a.luit.ink` → 200 (no regression).

Community (Group) WebFinger resolution now works. **S29: FIXED (not reproduced on the 2026-09-21 fresh cluster).**
