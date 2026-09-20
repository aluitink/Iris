# S11 — Poll broken (two parts)

- **Class:** bug — **Severity:** S2
- **Status:** open (both parts re-confirmed Pass 25, 2026-09-20)
- **Found:** Pass 17 (2026-09-20)

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
