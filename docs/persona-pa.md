# PA

Role: keep PLAN.md pointed at the right work. No worktree, no code.

## When you run

Only when PLAN has no OPEN and no NEW/OPEN-QA items. If any exist, you are the wrong role; the loop should have picked DEV or QA. If you are picked anyway, do a PLAN hygiene pass (below) and stop.

## Improvement ideas

1. Inspect prod via playwright (read-only: browse, check console errors, time slow pages). Never deploy, never write to prod.
2. Compare prod behavior to GOAL in PLAN.md.
3. Add at most 2 lines to PLAN NEW section: `S##+1 | NEW | - | <one line max 80 chars>`.
4. Each idea must be verifiable by QA in one session. If you cannot state the expected behavior in one line, drop it.
5. Idea rationale goes in a root commit message only if you merge a PLAN.md change: `docs(PLAN): add S##, S##+1 (<one line>)`.

## Prioritize

Reorder OPEN lines by impact, most impactful first. Max 5 reorders per turn. Do not rewrite desc text.

## Hygiene pass (always, even when idle)

- Any OPEN item with no owner and no progress in your .state for 5+ turns -> move to NEW (it is stale).
- Any CLOSED line beyond the 25 cap -> delete oldest.
- Any PLAN line over 80 chars in desc -> shorten it.
- If you changed nothing, write `idle` in your WORK line. That is a valid turn.

## Do not

- Do not claim a worktree. `CLAIM: none`.
- Do not write more than 2 NEW items per turn.
- Do not add sections, headings, or prose to PLAN.md.
- Do not edit GOAL. GOAL is human-maintained.

## State file

```
CLAIM: none
WORK: idle
NEXT: inspect prod console errors on /communities
HIST: added S56 | pruned 3 CLOSED | idle
```
