# PROTOCOL

Two agent threads run a loop. Each turn, the same loop instruction (docs/LOOP.md) is re-posted.
Agents act as DEV, QA, or PA. One agent may hold any worktree per turn.

## Roles

| role | worktree | stack | may write |
|------|----------|-------|-----------|
| DEV  | dev1 or dev2 | dev env | code, tests, own .state file, PLAN status |
| QA   | qa       | qa env  | qa notes in PLAN, own .state file |
| PA   | none     | prod    | PLAN ideas, own .state file |

## Turn (exactly this, in order)

1. Read: `.state/other-agent.md`, `.state/you.md`, `PLAN.md`.
2. Pick role:
   - PLAN has open NEW or OPEN-QA items -> QA
   - else PLAN has open OPEN items -> DEV
   - else -> PA
3. Do ONE unit of work for that role (docs/persona-<role>.md).
4. Rewrite `.state/<you>.md` (full rewrite, never append).
5. Update PLAN.md only for items you touched.
6. Stop. No extra work, no extra writing.

## Claims (anti-overlap)

- A worktree is claimed by writing `CLAIM: <worktree>` in your .state file.
- Before claiming, read the other .state file. If that worktree is claimed, pick the other.
- Claiming a PLAN item = writing its id on line 2 of your .state file. Never take an id claimed by the other.
- A claim is valid only while it appears in the other agent's latest read. Stale claims (you have not seen the other's file change in 3 of your turns) may be taken over: note `TOOK: S##` in your .state file.
- Both agents must never edit PLAN.md lines for the same item in the same turn. If unsure, skip the item this turn.

## .state file format (hard)

Exactly this shape, max 12 lines, rewritten every turn:

```
CLAIM: dev1
WORK: S54
NEXT: re-test input binding on dev1 stack
HIST: merged S51 | verified S52 | fixed S54
```

- `CLAIM` — worktree you hold this turn (`none` for PA).
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
- Never edit docs/*.md except PLAN.md. Persona files and PROTOCOL.md are human-maintained.

## Environments

- dev1 stack, dev2 stack, qa stack: public, isolated, built from worktrees.
- prod: built from root (main). PA may inspect prod via playwright. Never deploy to prod from an agent.
- Merges: worktree branch -> main (root) only after `dotnet test` passes in the worktree.
- Agents never commit in root. Root moves only by merge.

## Failure handling

- Stack down: restart it. If still down after 2 tries, write `BLOCKED: <reason>` on your .state file line 2 (replaces WORK) and stop.
- Test failing for a reason outside your item: leave the item, note `BLOCKED: <reason>`, pick the next item.
- Other agent appears stuck (its .state unchanged and you have taken over its claim): proceed; it will resync on its next read.
