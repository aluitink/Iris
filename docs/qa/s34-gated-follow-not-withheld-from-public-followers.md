# S34 — Gated (manually-approved) follow request is added to the public `followers` collection BEFORE acceptance

- **Class:** bug / data-integrity (privacy/authorization) — **Severity:** S2
- **Status:** open
- **Found:** Interop suite A3 (Iris↔Iris, same-instance gate path), 2026-09-20, QA federation stack (Iris A `qa-iris-a.luit.ink`)
- **Related:** A3 (follow-request gating). The gate *queues* the request (pending notification + requests endpoint correct), but the **public** followers collection does not withhold the pending edge.

## Symptom

`ii-a1` (A) enabled **"Require approval for follow requests"** (profile edit page). `GET A /ap/v1/u/ii-a1` → `manuallyApprovesFollowers = true`. `ii-a2` (A) pressed **Follow** on `ii-a1`.

**The gate partially works (queue is correct):**
- `ii-a1` notifications: a **pending follow request** from `ii-a2` ("sent you a follow request") with **Accept/Decline** (A3.1 ✓).
- `GET A /local/v1/u/ii-a1/requests` (authed) → `[…/u/ii-a2]` (pending) before accept, `[]` after accept (A3.3 ✓).

**But the public followers collection is wrong (A3.2 ✗):**
- `GET A /ap/v1/u/ii-a1/followers` (public, no auth) → **already includes `ii-a2`** (count 2: `ii-b1@B`, `ii-a2`) **immediately after the follow, BEFORE ii-a1 accepts**.
- So even though `ii-a1` requires manual approval and the request is still pending (not yet accepted), `ii-a2` appears in the **public** followers list. The pending/gated edge is **not withheld** from the public collection.

After `ii-a1` clicks **Accept**: `GET A /local/v1/u/ii-a1/requests` → `[]`; `ii-a1/followers` still `[ii-b1, ii-a2]`; `ii-a2/following` = `[ii-a1]` (edge finalized both sides — A3.3 ✓). The defect is specifically the **pre-accept** exposure.

## Root cause (suspected)

When a `Follow` is received by an account with `manuallyApprovesFollowers = true`, the handler adds the follower to the account's **public `followers` collection** immediately (as if accepted), instead of holding it in the pending-requests store only. The public `followers` endpoint should return only **accepted** followers for a gated account (pending requests should be excluded). No `file:line` yet — needs a code pass on the Follow handler / `followers` collection query (does it filter out pending requests when `manuallyApprovesFollowers` is set?).

## Fix (agreed approach)

- For an account with `manuallyApprovesFollowers = true`, a new `Follow` must be held in the **pending-requests** store only and **excluded from the public `followers` collection** until the owner **Accepts**. On Accept, move it into the public `followers`; on Decline, discard it.

## Re-verify (clean entry)

1. `ii-a1` enables "Require approval for follow requests" (`manuallyApprovesFollowers = true`).
2. `ii-a2` follows `ii-a1`.
3. `GET A /local/v1/u/ii-a1/requests` → `[ii-a2]` (pending); `ii-a1` sees a pending request (A3.1 ✓).
4. `GET A /ap/v1/u/ii-a1/followers` (public) → **does NOT include `ii-a2`** (withheld while pending). ← the fix
5. `ii-a1` Accepts → `requests` = `[]`, `ii-a1/followers` includes `ii-a2`, `ii-a2/following` includes `ii-a1` (A3.3 ✓).

**Re-verification evidence (Interop A3, 2026-09-20, QA stack):** `ii-a1` `manuallyApprovesFollowers` = true; `ii-a2` follows; `ii-a1` notifications show pending request (A3.1 ✓); `GET A /local/v1/u/ii-a1/requests` = `[ii-a2]`; **`GET A /ap/v1/u/ii-a1/followers` = `[ii-b1, ii-a2]` (ii-a2 present BEFORE accept)** (A3.2 ✗); after Accept: `requests` = `[]`, `followers` = `[ii-b1, ii-a2]`, `ii-a2/following` = `[ii-a1]` (A3.3 ✓). **S34 OPEN.**

## Re-test (fresh rebuild, 2026-09-20)

**CONFIRMED — reproduces.** On the fresh stack:
- `ii-a1` (A) enabled "Require approval for follow requests" via profile edit; `GET A /ap/v1/u/ii-a1` → `manuallyApprovesFollowers = true`.
- `ii-a2` (A) pressed Follow on `ii-a1`.
- `GET A /ap/v1/u/ii-a1/followers` (public) **before accept** = `[ii-b1@B, ii-a2]` (count 2) — **`ii-a2` present before acceptance** (A3.2 ✗).

S34 OPEN (reproduces on a fresh build). (A3.1 pending-request + A3.3 post-accept behavior unchanged from the original run.)

## Re-test (interop A3, 2026-09-21, fresh QA cluster)

**CONFIRMED FIXED — not reproduced.** `ii-a1` (A) enabled "Require approval for follow requests" (`manuallyApprovesFollowers = true`); `ii-a2` (A) pressed Follow on `ii-a1`.
- **Pending (before accept):** `GET A /ap/v1/u/ii-a1/followers` (public) = **`[ii-b1@B]` only (count 1)** — `ii-a2` **withheld** while pending. `ii-a2/following` = count 0. ii-a1's profile **Requests tab** shows "ii-a2 wants to follow you" with Accept/Reject (A3.1 ✓).
- **After Accept:** `GET A /ap/v1/u/ii-a1/followers` = `[ii-b1@B, ii-a2]` (count 2); `ii-a2/following` = `[ii-a1]` (A3.3 ✓). (Note: the accept took a few seconds to persist — an immediate re-read right after Accept still showed count 1; a re-read ~5 s later showed count 2.)

The pending/gated follower is now correctly excluded from the public `followers` collection until the owner accepts. **S34: FIXED (not reproduced on the 2026-09-21 fresh cluster).**

## Re-test (Pass 156, 2026-09-21, build `38ae87c`) — S34 re-confirmed FIXED (gated follower withheld pre-accept)

Performed a **fresh gated-follow test** on the current build (`38ae87c`):

1. **Enabled gated follow** on ii-a1 (A) via profile edit ("Require approval for follow requests"); `GET A /ap/v1/u/ii-a1` → `manuallyApprovesFollowers` = **true**.
2. **ii-a2 unfollowed + re-followed** ii-a1 (a fresh follow while gated follow is ON) → created a pending request.
3. **Pending (before accept):** `GET A /ap/v1/u/ii-a1/followers` (public, curl + clean authenticated read as ii-a1) = **`[ii-b1]` only (count 1)** — **`ii-a2` withheld** while pending. `GET A /ap/v1/u/ii-a2/following` = **count 0** (empty). `GET A /local/v1/u/ii-a1/requests` (authenticated) = **`[ii-a2]`** (the fresh follow is correctly pending). ii-a1's profile **Requests tab** shows "ii-a2 wants to follow you" with Accept/Reject (A3.1 ✓).
   - Note: an earlier browser-context read of the followers collection (from a confused tab session) transiently showed ii-a2 — a **stale/incorrect client-side read**; the wire (curl + clean authenticated read as ii-a1) is correct (ii-a2 withheld).
4. **After Accept:** `GET A /local/v1/u/ii-a1/requests` = **`[]`**; `GET A /ap/v1/u/ii-a1/followers` = **`[ii-b1, ii-a2]`** (count 2); `GET A /ap/v1/u/ii-a2/following` = **`[ii-a1]`** (count 1) (A3.3 ✓). (The accept took a few seconds to persist — an immediate re-read right after Accept briefly showed count 1 / following 0; a re-read ~5 s later showed the restored edge.)

**Cleanup:** disabled gated follow on ii-a1 (`manuallyApprovesFollowers` back to false/None); the ii-a2→ii-a1 follow edge is restored (followers = `[ii-b1, ii-a2]`, ii-a2 following = `[ii-a1]`).

**Verdict (build `38ae87c`): S34 FIXED — the pending/gated follower (ii-a2) is correctly withheld from the public `followers` collection until the owner accepts (requests = `[ii-a2]` pre-accept, followers = `[ii-b1]` only; post-accept followers = `[ii-b1, ii-a2]`, ii-a2 following = `[ii-a1]`). No regression.**
