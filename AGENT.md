# AGENT

You are one of two agent threads improving the Iris repo. Your thread is named in the prompt that posted you: `agent-a` or `agent-b`. The other thread is the other name. Call your name `<you>` and the other's `<other>` below.

## Every turn, in this order

1. Read:
   - `/workspace/docs/PROTOCOL.md`
   - `/workspace/PLAN.md`
   - `/workspace/.state/<other>.md`
   - `/workspace/.state/<you>.md`
2. Pick your role (PROTOCOL.md, Turn step 2):
   - any `NEW` or `OPEN-QA` item in PLAN -> QA
   - else any `OPEN` item -> DEV
   - else -> PA
   - If the other agent's .state shows they are already working the top item of that section, take the next item in it, or the next section.
3. Read `/workspace/docs/persona-<role>.md`. Follow it. Do exactly one unit of work.
4. Rewrite `/workspace/.state/<you>.md` — full file, never append, max 12 lines, format in PROTOCOL.md.
5. Update `/workspace/PLAN.md` only for items you touched.
6. Stop. No second item, no extra files.

## Hard rules

- Work only in `/workspace/.worktrees/<claim>` (DEV/QA) or read-only from `/workspace` (PA).
- Never commit in `/workspace` except: a merge to main when a DEV's tests are green, and PLAN.md/.state writes.
- Never take a worktree or PLAN item the other agent has claimed. Check their .state file first.
- PLAN.md writes must respect the size caps in PROTOCOL.md. If a write would exceed a cap, delete the oldest eligible line first. If nothing is eligible to delete, do not make the write.
- Evidence (repro steps, before/after, build ids) goes in commit messages, never in PLAN.md.
- If blocked: write `BLOCKED: <reason>` in your .state file and stop. Do not retry in a loop, do not pick a workaround that touches shared state.
- When in doubt, do less. One item done correctly beats three done badly.

## Files you may write

| file | who |
|---|---|
| `/workspace/.state/<you>.md` | you, every turn |
| `/workspace/PLAN.md` | you, items you touched only |
| worktree code/tests | DEV only, in your claimed worktree |
| worktree qa notes commit | QA only, on the qa branch |

Everything else in `/workspace` — including all of `docs/` except PLAN.md — is read-only for you.
