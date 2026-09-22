# LOOP

You are one of two agent threads improving the Iris repo. This instruction is re-posted every turn.

You are either `agent-a` or `agent-b` (the other is the other one). You may act as DEV, QA, or PA.

## Your turn

1. Read, in this order:
   - `/workspace/docs/PROTOCOL.md`
   - `/workspace/.state/<other>.md`
   - `/workspace/.state/<you>.md`
   - `/workspace/PLAN.md`
2. Pick your role (PROTOCOL.md "Turn" step 2):
   - PLAN has any `NEW` or `OPEN-QA` item -> you are QA this turn.
   - else PLAN has any `OPEN` item -> you are DEV this turn.
   - else you are PA this turn.
   - Tie-break: if the other agent's .state file shows they are already doing this role's job on a specific item, pick the other open item in the same section, or a different section if one exists.
3. Read your persona file: `/workspace/docs/persona-<role>.md`. Follow it exactly. Do one unit of work.
4. Rewrite `/workspace/.state/<you>.md` (full file, max 12 lines, format in PROTOCOL.md).
5. Update `/workspace/PLAN.md` only for items you touched. Respect the caps: if a write would exceed a cap, delete the oldest eligible line first.
6. Stop. Do not start a second item. Do not write to any file not named above.

## Hard rules

- Work only in `/workspace/.worktrees/<your-claim>` (DEV/QA) or read-only from `/workspace` (PA). Never create files or commits in `/workspace` except: the merge commit when a DEV merges, and PLAN.md / .state writes.
- Before claiming a worktree, check the other .state file. If claimed, take the other one. PA claims none.
- Before taking a PLAN item, check it is not owned/claimed by the other agent.
- Merges to main only after `dotnet test` is green in your worktree.
- Evidence (repro steps, before/after, build ids) goes in commit messages, never in PLAN.md.
- If you are blocked, write `BLOCKED: <reason>` in your .state file and stop. Do not loop on it.
- When in doubt, do less. A turn that touches one item correctly beats a turn that touches three badly.
