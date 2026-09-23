# AGENT

You are one of two agent threads improving the Iris repo. The prompt that posted you names your thread: `agent-a` or `agent-b`. That name is your identity only — it names your `.state` file. It is not a role and not a worktree; you may act as DEV, QA, or PA and hold whichever worktree the selection rule gives you. Call your name `<you>` and the other's `<other>` below.

If the prompt does not name you `agent-a` or `agent-b`, write nothing and stop. Do not guess an identity.

The loop operates on the branch named in `/workspace/LOOP-CONFIG` (`ACTIVE_BRANCH=...`).
Call it `<active>`. In these instructions, wherever `main` appears, read `<active>`.
You never change which branch is active; that is a human action.

## Every turn, in this order

1. State gate — your FIRST write of the turn, before reading anything else, before touching a worktree:
   rewrite `/workspace/.state/<you>.md` (full file, never append, max 12 lines, format in PROTOCOL.md).
   If you do not yet know your role, write `WORK: idle` and `NEXT: select role`.
   Do not open a worktree, run a test, or edit any PLAN.md until this file exists on disk.
2. Read:
   - `/workspace/docs/PROTOCOL.md`
   - `/workspace/PLAN.md`
   - `/workspace/.state/<other>.md`
3. Pick your role by what is actionable (PROTOCOL.md, Turn step 3):
   - any `OPEN-QA` item in PLAN -> QA   (verify the merged fix live)
   - else any `OPEN` item -> DEV        (fix it)
   - else any `NEW` item -> PA          (triage: accept verified NEW -> OPEN, or reject/merge dupes)
   - else -> QA                          (no actionable item: hunt for new bugs)
   - If the other agent's .state `WORK` line holds the top item of that section, take the next item in it, or the next section.
   - If the worktree the role needs is claimed by the other agent, fall through to the next role in step 3's order (PROTOCOL.md).
   - If both agents select the same role in the same turn (a worktree serves only one),
     the agent that reads the other's `CLAIM: <wt>` first falls through to the NEXT role
     in step 3's order (e.g. both QA -> second takes PA triage; both PA -> second takes QA hunt).
4. Claim: update your `.state/<you>.md` `CLAIM` and `WORK` lines to what you selected.
5. Read `/workspace/docs/persona-<role>.md`. Follow it. Do exactly one unit of work in your claimed worktree.
6. PLAN.md: edit it **in your worktree** only, for items you touched. Commit it in your worktree.
   It reaches root when your branch merges. Never edit `/workspace/PLAN.md` directly.
7. Commit your work in the worktree. Merge to main when your persona says to (DEV: tests green; QA/PA: every turn).
8. Final rewrite of `/workspace/.state/<you>.md` with this turn's `HIST`.
9. Stop. No second item, no extra files.

## Hard rules

- Work only in `/workspace/.worktrees/<claim>`. The root `/workspace` is read-only for you,
  except: `/workspace/.state/<you>.md` (every turn) and the merge command itself.
- Never commit in `/workspace` except the merge of your own branch into main.
- Never take a worktree or PLAN item the other agent has claimed. Check their `.state` file first.
- If the other `.state` file is missing or empty, the other agent is absent: no claims of theirs exist.
  Do not wait for them. Do not search for work outside PLAN.md. Selection runs on PLAN state + visible claims only.
- Idle is a valid turn. If you cannot select an item without overlapping, write an idle `.state` file and stop.
- PLAN.md writes must respect the size caps in PROTOCOL.md. If a write would exceed a cap, delete the oldest
  eligible line first. If nothing is eligible to delete, do not make the write.
- Evidence (repro steps, before/after, build ids) goes in commit messages, never in PLAN.md.
- If blocked: write `BLOCKED: <reason>` in your `.state` file and stop. Do not retry in a loop.
- When in doubt, do less. One item done correctly beats three done badly.

## Files you may write

| file | who |
|---|---|
| `/workspace/.state/<you>.md` | you, every turn (first write and last write) |
| PLAN.md, code, tests — inside your claimed worktree only | per role, per persona file |

Everything else in `/workspace` — all of `docs/`, all source — is read-only for you at the root.
