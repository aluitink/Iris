# S11 — Poll broken (two parts)

- **Class:** bug — **Severity:** S2
- **Status:** open (both parts re-confirmed Pass 36, 2026-09-20, on deployed `bb28dcf`)
- **Found:** Pass 17 (2026-09-20) — re-confirmed Passes 27, 34, 36

## S11a — Poll without a body is a silent no-op

**Symptom:** posting a poll that uses the **Question** field **without** the main Content body (question + 2 options) → **no "Posted" confirmation, 0 errors, and NO object created** (DB: no new `Question` object).

**Root cause:** `PostAsync`'s empty-`Content` guard (`Compose.razor:1330`) silently discards it — but a poll's question lives in the dedicated **Question** field (`PostPollAsync` uses `PollQuestion`, `:1820`), so a valid poll with no main body never posts.

**Fix:** exempt Poll from the empty-`Content` guard (validate `PollQuestion` / options instead) and/or surface an error instead of silently returning.

## S11b — Posted poll invisible in "Your posts"

**Symptom:** a poll posted **with** a body (202, object stored at `…/objects/06GBSGQTYCMVSXEYC9XCMK6MPR`, `type=["Question","Object"]`) is **NOT listed in the profile "Your posts"** / actor "Posts" — though its own object page renders fine.

**Root cause:** `OutboxFilter.IsContentItem` (`OutboxFilter.cs:42`) accepts only `Note || Article` and excludes `Question`.

**Fix:** include `Question` in the content-item check.

## Re-verify

1. Post a poll with only a question + options (no body) → it posts (202), a confirmation shows, and the `Question` object exists in the store.
2. The poll appears on the author's Profile "Your posts" and the object detail page renders with options.

**Re-verification evidence (Pass 27, 2026-09-20, clean entry, andrew):** (a) a body-less poll (question + 2 options, no Content) → Post → **no confirmation, 0 console errors, no new `Question` object in the DB** — S11a still open. (b) the Pass-25 poll's `Create` activity (`…/creates/06GBSGQTYCMVSXEYC9XCMK6MPM`) **is present** in `GET /ap/v1/u/QAUser1/outbox` (20 items), yet the actor "Posts (8)" list still omits the poll — S11b still open (the UI's content-item filter still excludes `Question`).

**Re-verification evidence (Pass 34, 2026-09-20, andrew, deployed `456b0d9`):** (a) body-less poll (question "QA Pass 34 poll test" + 2 options, no Content) → Post → **no confirmation, 0 console errors** — S11a still open. (b) poll WITH body ("Poll test with body") → 202, object at `…/creates/06GBV3RTQ8T7RAEBA7V3TP7EZ4`, `type=["Question","Object"]` in outbox (first item), but **NOT visible** in Profile "Your posts" — S11b still open.

**Re-verification evidence (Pass 36, 2026-09-20, andrew, deployed `bb28dcf`):** (a) body-less poll (question "S11 body-less poll test" + 2 options A/B, no Content) → Post → **no confirmation, 0 console errors, no network request fired** (no outbox POST) — S11a still open. (b) poll WITH body ("S11 poll with body test" + question "S11 test poll pass 36" + options Yes/No) → Post → **no confirmation, 0 console errors, no network request fired** — the post was NOT sent at all this time (previously 202'd). S11b cannot be re-confirmed independently since no object was created, but the filter issue is unchanged in code.
