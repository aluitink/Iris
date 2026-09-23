# PROTOCOL

Two agent threads run a loop: `agent-a` and `agent-b`. Each turn, the loop prompt
points the agent at /workspace/AGENT.md, which points at this file.
Agents act as DEV, QA, or PA. An agent may hold any worktree per turn.
Thread name is identity only; it never fixes a role or a worktree.

## Roles

| role | worktree | branch | stack | merges |
|------|----------|--------|-------|--------|
| DEV  | dev1 or dev2 | dev1 / dev2 | dev env | after `dotnet test` green |
| QA   | qa         | qa        | qa env  | every turn |
| PA   | pa         | pa        | prod (read-only) | every non-idle turn |

The loop operates on `ACTIVE_BRANCH` from /workspace/LOOP-CONFIG (call it `<active>`).
All `main` in this file means `<active>`. Humans set it; agents never do.

## Turn (exactly this, in order)

1. State gate: rewrite `.state/<you>.md` FIRST (format below). `WORK: idle` if role not yet selected.
   No worktree, test, or PLAN.md access before this file exists on disk.
2. Read: `LOOP-CONFIG`, `PLAN.md`, `.state/<other>.md`.
3. Pick role by PLAN state:
   - any NEW or OPEN-QA item -> QA
   - else any OPEN item -> DEV
   - else -> PA
4. Acquire the worktree the role needs:
   - QA -> `qa`. DEV -> `dev1` else `dev2`. PA -> `pa`.
   - Take it if the other agent's `.state` does not claim it.
   - If claimed: DEV takes the other dev worktree; QA and PA have no fallback.
   - If no worktree is available for the role, fall through to the next role in step 3's order.
   - If no role is selectable, write an idle `.state` file and stop. Idle is a valid turn.
5. Update `.state/<you>.md` `CLAIM` and `WORK` lines to your selection.
6. Do ONE unit of work (docs/persona-<role>.md) in your claimed worktree.
7. Edit PLAN.md **in your worktree** for items you touched. Commit in the worktree.
8. Merge to `<active>` when your role's merge rule above is met.
9. Final rewrite of `.state/<you>.md` with this turn's `HIST`.
10. Stop. No extra work, no extra writing.

## Claims (anti-overlap)

- `.state/` is gitignored and lives on the shared filesystem at `/workspace/.state/`. It is the only
  same-turn coordination channel. Visibility is read-time: you see what is on disk when you read.
- A worktree is claimed by writing `CLAIM: <worktree>` in your `.state` file.
- Before claiming, read the other `.state` file. If it claims that worktree, do not take it.
- If the other `.state` file is missing or empty, the other agent is absent: no claims of theirs exist.
  Do not wait. Do not look for work outside PLAN.md.
- Same-instant race (both read empty, both want one worktree): last writer wins on disk; the agent that
  finds the other's claim on its NEXT turn re-runs step 4 and takes the fallback. No compensation needed.
- Claiming a PLAN item = writing its id on the `WORK` line. Never take an id in the other's `WORK` line.
- Stale claims: if the other agent's `.state` is unchanged for 3 of your turns and you are blocked on its
  claim, take it over and note `TOOK: S##` in your `.state` file.
- Both agents must never edit the same PLAN.md item line in the same turn. If unsure, skip the item.

## .state file format (hard)

Exactly this shape, max 12 lines, rewritten every turn:

```
CLAIM: dev1
WORK: S54
NEXT: re-test input binding on dev1 stack
HIST: merged S51 | verified S52 | fixed S54
```

- `CLAIM` — worktree you hold this turn (`none` when idle).
- `WORK` — one PLAN id (or `idle`).
- `NEXT` — one line: the single next action.
- `HIST` — last 3 completed actions, `|` separated, each <= 6 words. Older than 3 is deleted.

## PLAN.md format (hard)

Sections, in order: `## GOAL`, `## OPEN`, `## NEW`, `## CLOSED`.

Every item is one line:

```
S55 | OPEN | dev1 | post edit UI stale + delete silent no-op
```

- `id` — S + number, never reused, never renumbered.
- `status` — one of: NEW, OPEN, OPEN-QA, CLOSED.
  - NEW = found by QA or proposed by PA, not yet accepted.
  - OPEN = accepted, ready for a dev.
  - OPEN-QA = fix merged to root, awaiting live verification.
  - CLOSED = verified live.
- `owner` — worktree id, or `-`.
- `desc` — one line, max 80 chars, no sentences.

### Status flow

QA finds bug -> `NEW`. PA accepts (or dev claims) -> `OPEN`. Dev fixes in worktree, merges to root -> `OPEN-QA`. QA verifies live -> `CLOSED` (moved to CLOSED section).

## Size caps (self-cleaning)

- PLAN.md: max 120 lines total.
- OPEN + NEW: max 15 open items. If adding an item exceeds 15, the oldest CLOSED line is deleted first; if still over, the item is not added.
- CLOSED: max 25 lines. When a new item is closed, the oldest CLOSED line is deleted.
- `.state/<agent>.md`: max 12 lines (see format).
- Rule: a write that would exceed a cap MUST delete something first. No exceptions, no archives, no "keep for reference".

## Writing rules (anti-fluff)

- One line = one fact. No paragraphs, no prose, no lists inside PLAN items.
- No justification, no history, no "notes:", no "TODO:", no speculation in PLAN.
- QA evidence belongs in the commit message, not PLAN. PLAN carries the verdict only.
- PA ideas are one line each. If it takes more than one line, it is not ready; do not add it.
- Never edit docs/*.md. PLAN.md, persona files, and PROTOCOL.md are human-maintained at the root;
  agents edit PLAN.md only inside their worktree.

## Startup (human, before each loop session)

Run from root, in order:

```
git checkout <active>
git pull --ff-only origin <active>
for b in dev1 dev2 qa pa; do git -C .worktrees/$b merge <active> --no-edit; done
```

Then confirm `.state/agent-a.md` and `.state/agent-b.md` exist (create idle ones if not)
and that root's working tree is clean. If any worktree merge conflicts, resolve before
starting the loop — agents never resolve conflicts.

Switching the active branch: edit LOOP-CONFIG, then re-run startup. The old branch's
commits stay on its worktree branches; nothing is lost.

## Environments

- dev1, dev2, qa, pa worktrees: `/workspace/.worktrees/<name>`, branches of the same name.
- Stacks (dev1, dev2, qa) are built from their worktrees. prod is built from root (`<active>`).
- URLs, ports, and role->environment binding: docs/ENVIRONMENTS.md. Read it when you need to dial
  a stack. Dial public FQDNs only — never localhost, container names, or host ports.
- An agent dials only its bound environment (plus prod for PA). Cross-environment dials are forbidden.
- An agent may build/deploy only its bound environment's stack, via the single command in
  docs/ENVIRONMENTS.md (Stack ops). No compose commands of any other kind, ever.
- Merges: worktree branch -> `<active>` (root) per the role's merge rule in the Roles table.
  Root moves only by merge. Agents never commit in root except the merge command itself.

## Failure handling

- Stack down: restart it. If still down after 2 tries, write `BLOCKED: <reason>` on your .state file line 2 (replaces WORK) and stop.
- Test failing for a reason outside your item: leave the item, note `BLOCKED: <reason>`, pick the next item.
- Other agent appears stuck (its .state unchanged and you have taken over its claim): proceed; it will resync on its next read.
