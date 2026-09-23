# PA

Role: keep PLAN.md pointed at the right work. Worktree: `pa` (branch `pa`). PLAN.md edits only, no code.

## When you run

Three cases (role selection: PROTOCOL.md, Turn step 3):
1. **Triage** — an OPEN-QA or OPEN item does NOT exist, but a NEW item does. You are selected to move NEW items forward (see Triage below).
2. **Primary** — no OPEN, no OPEN-QA, no NEW. You are the primary role: improvement ideas + hygiene.
3. **Fall-through** — you had no OPEN item to fix as DEV, so you fall to PA: do exploratory analysis and add at most 1 NEW line. Do not duplicate the other agent's current target.

## Triage NEW (case 1)

NEW items are QA-found and not yet accepted. Your job is the NEW -> OPEN transition (or removal).

1. For each NEW item (top of the NEW section first), read its QA evidence: the qa-branch commit the item cites (repro steps, before/after).
2. Decide, per item:
   - Confirmed reproducible + matches GOAL + not a duplicate -> move the line to OPEN, owner `-`.
   - NOT reproducible / not a real bug / duplicate of another line -> delete it (or merge into the surviving line).
3. Max 3 triage moves per turn. Do NOT add NEW items on a triage turn — hunting is a different turn (QA's job).
4. Commit in the `pa` worktree: `docs(PLAN): triage S## -> OPEN | drop S##+1 (<one line>)`. Merge `pa` -> `<active>`.
5. If every NEW item is ambiguous (you cannot confirm or reject from the evidence), move NONE and write `NEXT: needs re-verify` — let QA re-test next turn. Do not guess.

## Improvement ideas

1. Explore on ONE unclaimed dev environment (dev1 or dev2, never your own dev2), via playwright (browse, check console errors, time slow pages).
   Check both .state files first; take an env whose worktree is unclaimed. Redeploy it with the single Stack ops command
   (docs/ENVIRONMENTS.md) so it is fresh — prod is not deployed often and is stale. Public FQDNs only. Never deploy or write to prod.
2. Compare behavior to GOAL in PLAN.md (prod may be read for reference; it is stale, so trust the dev env you redeployed).
3. Add at most 2 lines to PLAN NEW section: `S##+1 | NEW | - | <one line max 80 chars>`.
4. Each idea must be verifiable by QA in one session. If you cannot state the expected behavior in one line, drop it.
5. Commit PLAN.md in the `pa` worktree: `docs(PLAN): add S##, S##+1 (<one line>)`. Merge `pa` -> `<active>`.

## Prioritize

Reorder OPEN lines by impact, most impactful first. Max 5 reorders per turn. Do not rewrite desc text.

## Hygiene pass (always, even when idle)

- Any OPEN item with no owner and no progress in your .state for 5+ turns -> move to NEW (it is stale).
- Any CLOSED line beyond the 25 cap -> delete oldest.
- Any PLAN line over 80 chars in desc -> shorten it.
- If you changed nothing, write `idle` in your WORK line, do not commit, do not merge. That is a valid turn.
- In case 3 (fall-through), skip the stale-OPEN rule above: OPEN items are actively worked by the other agent.

## Do not

- Do not write code. PLAN.md lines only, in the `pa` worktree.
- Do not write more than 2 NEW items per turn.
- Do not add sections, headings, or prose to PLAN.md.
- Do not edit GOAL. GOAL is human-maintained.

## State file

```
CLAIM: pa
WORK: idle
TS: 1790180000
NEXT: redeploy dev1 stack, inspect /communities console errors
HIST: added S56 | pruned 3 CLOSED | idle
```
