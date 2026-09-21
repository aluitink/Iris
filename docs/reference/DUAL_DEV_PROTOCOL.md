# Iris — Dual-Dev Protocol

> Extension to [DEV_LOOP.md](DEV_LOOP.md) + [QA_LOOP.md](QA_LOOP.md) for **two parallel dev agents** + one QA agent. The existing protocol assumes a single dev; this doc adds the coordination rules for two.

## Topology

```
/workspace                  ← production (primary repo, branch main)
  apps/Iris.Web/.env        ← prod config (git-ignored, port 8088)
  environments/stack/       ← shared compose template (all envs use this)
  environments/dev1/.env    ← dev1 config (committed, port block 10xxx)
  environments/dev2/.env    ← dev2 config (committed, port block 20xxx)
  environments/qa/.env      ← qa config (committed, port block 30xxx)

/workspace/.worktrees/dev1  ← dev1 agent (branch dev1) — builds from here
/workspace/.worktrees/dev2  ← dev2 agent (branch dev2) — builds from here
/workspace/.worktrees/qa    ← QA agent (branch qa) — builds from here

/workspace/.state/          ← shared untracked state: dev1.md, dev2.md, qa.md (one line each)
```

**Key invariant:** each agent builds its Iris image from **its own worktree** (`REPO_ROOT` in the `.env` points to the worktree). Each agent deploys to **its own environment stack** (dev1 → `dev1-*` ports, dev2 → `dev2-*` ports, qa → `qa-*` ports). There is no shared deploy target — no deploy conflicts.

**Production** is always run from the primary repo (`/workspace/apps/Iris.Web`), never from a worktree.

## Port scheme

| Env block | Env | iris-a | iris-b | lemmy | mastodon |
|-----------|-----|--------|--------|-------|----------|
| 10xxx | dev1 | 10081 | 10082 | 10091 | 10092 |
| 20xxx | dev2 | 20081 | 20082 | 20091 | 20092 |
| 30xxx | qa | 30081 | 30082 | 30091 | 30092 |
| 8088 | prod | 8088 | — | — | — |

Service offsets: `01`=iris-a, `02`=iris-b, `11`=lemmy, `12`=mastodon.

## FQDNs

| Env | iris-a | iris-b | lemmy | mastodon |
|-----|--------|--------|-------|----------|
| dev1 | `dev1-iris-a.luit.ink` | `dev1-iris-b.luit.ink` | `dev1-lemmy.luit.ink` | `dev1-mastodon.luit.ink` |
| dev2 | `dev2-iris-a.luit.ink` | `dev2-iris-b.luit.ink` | `dev2-lemmy.luit.ink` | `dev2-mastodon.luit.ink` |
| qa | `qa-iris-a.luit.ink` | `qa-iris-b.luit.ink` | `qa-lemmy.luit.ink` | `qa-mastodon.luit.ink` |
| prod | `iris.luit.ink` | — | — | — |

## Role assignments

| Agent | Worktree | Branch | Env stack | FQDN prefix |
|---|---|---|---|---|
| **dev1** | `.worktrees/dev1` | `dev1` | `-p dev1` (10xxx) | `dev1-*` |
| **dev2** | `.worktrees/dev2` | `dev2` | `-p dev2` (20xxx) | `dev2-*` |
| **QA** | `.worktrees/qa` | `qa` | `-p qa` (30xxx) | `qa-*` |
| **prod** | `/workspace` (primary) | `main` | `irisweb` (8088) | `iris` |

Each agent is the **sole deployer** for its own environment. dev1 deploys to `dev1-*`, dev2 to `dev2-*`, QA to `qa-*`. No agent deploys to another agent's stack.

## Shared state files (`.state/` — untracked, at the workspace root)

`/workspace/.state/` holds **one untracked file per agent** — `dev1.md`, `dev2.md`, `qa.md` — each a **single short line** saying what that agent is working on right now (the in-flight item + its current state, e.g. `dev1: Directory Paging — awaiting live UI verification`).

**Why:** PLAN.md's Active Slice only becomes visible to the other agents after a merge to `main` — mid-turn, the other agents see stale state. `.state/` is the live, zero-merge channel: agents work in separate worktrees but share the filesystem, so these files are instantly readable by everyone.

**Rules:**

- **Location & tracking:** the files live at the **workspace root** (`/workspace/.state/`), **not** inside a worktree. They are **untracked** (`.state/` is gitignored) and **never committed** — they are ephemeral coordination state, not document.
- **Sole owner per file:** each agent writes **only its own file** (`dev1.md`, `dev2.md`, or `qa.md`). **No agent ever edits another agent's state file.** This is the **only** thing any agent may touch in the workspace root.
- **Format:** exactly one line per file. `<agent>: <current item or state> — <what's next / where it stands>`. Keep it short; if it needs more than one line, the detail belongs in PLAN.md / the change doc, not here.
- **Read before picking work:** a dev **must read `.state/dev1.md` and `.state/dev2.md` before taking an item from the PLAN** (dev2's disjointness check uses this first, then the Active Slice as the durable record). A stale or missing file (e.g. an agent that has been idle) means "no known in-flight work" — proceed, but treat the Active Slice as the tie-breaker.
- **Write at:** (1) turn/pass start — declare the item you picked; (2) on any state change — repair, blocked, deploy, remaining; (3) turn/pass end — final status or `idle`. Update it in the same step where the corresponding PLAN.md section is updated.
- **Relationship to PLAN.md:** PLAN.md's **Active Slice is the durable record**; `.state/` is the live scratch mirror of it. On a mismatch, PLAN.md wins (it's committed); the agent whose state file disagrees updates its file on its next turn.
- **Bootstrap:** if `.state/` or your own file is missing, create it (one line) before your first action — missing files are never an excuse to skip the read-before-pick step for the *other* agents' files (a missing file just reads as "unknown").

## Bring up / tear down

```bash
# dev1
docker compose -f environments/stack/docker-compose.yml --env-file environments/dev1/.env -p dev1 up -d --build
docker compose -f environments/stack/docker-compose.yml --env-file environments/dev1/.env -p dev1 down -v

# dev2
docker compose -f environments/stack/docker-compose.yml --env-file environments/dev2/.env -p dev2 up -d --build
docker compose -f environments/stack/docker-compose.yml --env-file environments/dev2/.env -p dev2 down -v

# qa
docker compose -f environments/stack/docker-compose.yml --env-file environments/qa/.env -p qa up -d --build
docker compose -f environments/stack/docker-compose.yml --env-file environments/qa/.env -p qa down -v

# prod (primary repo only)
cd apps/Iris.Web && docker compose up -d --build
```

The `REPO_ROOT` in each `.env` points to that agent's worktree, so `--build` uses the correct source code.

## Turn structure

### dev1 — every turn

1. **Sync down:** `git -C .worktrees/dev1 merge main --no-edit` (pull in dev2's merged work + QA doc merges).
2. **Run build + fast tests** in the worktree. Red state → repair (max 2 attempts) → end turn.
3. **Read `.state/`** (`/workspace/.state/dev1.md` + `dev2.md`) — the live status of the other agent (see [Shared state files](#shared-state-files-state--untracked-at-the-workspace-root)).
4. **Select work item** per DEV_LOOP.md step 2 (Inbox → Re-verify → new QA findings → Dev Queue), disjoint from what `.state/dev2.md` says dev2 is on.
5. **Work on the item** (code + tests).
6. **Deploy to dev1 stack:** `docker compose -f environments/stack/docker-compose.yml --env-file environments/dev1/.env -p dev1 up -d --build` (from the dev1 worktree, so `REPO_ROOT` is correct).
7. **Commit** (implementation + tests together; docs separate).
8. **Merge to `main`:** from `/workspace`, `git merge dev1 --no-edit`.
9. **Update PLAN.md** (Active Slice, Dev Queue, Live state, Recently Completed) **and `/workspace/.state/dev1.md`** (one line: final status or `idle`).

### dev2 — every turn

1. **Sync down:** `git -C .worktrees/dev2 merge main --no-edit`.
2. **Run build + fast tests** in the worktree. Red state → repair (max 2 attempts) → end turn.
3. **Read `.state/`** (`/workspace/.state/dev1.md` + `dev2.md`) — the live status of the other agent (see [Shared state files](#shared-state-files-state--untracked-at-the-workspace-root)).
4. **Select work item** from the Dev Queue, **disjoint** from dev1's current item (see Scope assignment).
5. **Work on the item** (code + tests).
6. **Deploy to dev2 stack:** `docker compose -f environments/stack/docker-compose.yml --env-file environments/dev2/.env -p dev2 up -d --build` (from the dev2 worktree).
7. **Commit** in the worktree.
8. **Merge to `main`:** from `/workspace`, `git merge dev2 --no-edit`.
9. **Update PLAN.md's Active Slice** — dev2 records its own progress (narrow exception, see below) — **and `/workspace/.state/dev2.md`** (one line: final status or `idle`).

**PLAN.md ownership:** dev1 owns the dev sections (Active Slice, Dev Queue, Live state, Recently Completed). dev2 may write to the Active Slice section **only** for lines describing its own assigned item. dev2 does NOT edit Dev Queue, Live state, or Recently Completed. Both agents write to `docs/changes/` for their own slices.

### QA — every turn

QA's protocol is per [QA_LOOP.md](QA_LOOP.md). QA:
- Tests the **qa stack** (`qa-*` FQDNs, 30xxx ports) — its own deploy target.
- Builds from `.worktrees/qa` (its own worktree).
- Does NOT test dev1 or dev2 stacks (those are the dev agents' own validation environments).
- Staleness check: compare PLAN.md's **Live state** `deployed:` (the qa stack's deployed commit) against the qa cluster's current build.
- Maintains `/workspace/.state/qa.md` (one line: current pass focus / re-verify target / `idle`) and may **read** the dev state files for context — see [Shared state files](#shared-state-files-state--untracked-at-the-workspace-root).

## Scope assignment (the critical coordination point)

dev1 and dev2 **coordinate two ways:** the **live** channel is the [`.state/` files](#shared-state-files-state--untracked-at-the-workspace-root) (read before picking, updated as work progresses — visible mid-turn, no merge needed); the **durable** record is PLAN.md's Active Slice (committed, survives restarts). At the start of a turn, each agent reads `.state/` to see what the other is working on *right now*, and picks a **disjoint** item from the Dev Queue.

Rules:
1. **Disjoint code areas.** If dev1 is working on `src/Iris.Server/`, dev2 should pick a `src/Iris.Client/` or `apps/Iris.Web.Client/` item. Same-file edits are the conflict source — avoid them.
2. **Vertically complete.** Each agent's item is a full slice (impl + tests).
3. **Independent deploy.** Each agent deploys to its own stack. QA tests the qa stack only.
4. **If no disjoint item exists**, the agent waits (picks a lower-priority item in a different layer, or ends the turn).

**How to pick:** read `.state/dev1.md` + `.state/dev2.md` (live) → PLAN.md's Active Slice (durable; tie-breaker) → pick the next Dev Queue item that doesn't touch the same files/directories → **write your pick to your own `.state/<you>.md` line before starting to code**.

## Conflict resolution

**Code conflicts (merge to main):** should not happen if scope is disjoint. If a merge conflict appears:
1. The merging agent resolves it by hand.
2. Log the conflict in PLAN.md's Paused Questions (which file, both sides' intent).
3. The loop continues; a human reviews if needed.

**PLAN.md conflicts:** dev1 owns the dev sections; dev2 only writes to Active Slice's own-item lines. A conflict here means the ownership rule was broken → log in Paused Questions.

**Deploy conflicts:** impossible by design (each agent deploys to its own stack).

## Staleness check (QA's perspective)

QA's staleness check is unchanged: compare PLAN.md's **Live state** `deployed:` against the qa stack's current build. The deployed commit may include work from both dev1 and dev2 (merged via `main`). QA does not need to distinguish whose work is in the build.

**Dev agents' staleness check:** each dev agent compares its own stack's deployed commit against its worktree HEAD. If they differ, rebuild + redeploy its own stack.

## Failure modes specific to dual-dev

| Failure mode | Guard |
|---|---|
| dev1 and dev2 touch the same file → merge conflict | Scope assignment: disjoint areas; each agent reads `.state/` (+ Active Slice) before picking |
| dev2 commits work that's never deployed → QA never sees it | dev2 deploys to its own dev2 stack; QA tests the qa stack (which builds from the qa worktree, merged from main) |
| Both devs work on the same S-number → conflict | `.state/` + Active Slice coordination: each agent sees the other's current item before picking |
| An agent's state file is stale/missing → other dev picks its item | `.state/` is best-effort live status; PLAN.md's Active Slice is the durable tie-breaker; a missing file reads as "unknown", not "free" |
| dev2 writes to PLAN.md dev sections → clobbers dev1's state | Ownership rule: dev2 writes only Active Slice's own-item lines + `docs/changes/` |
| dev2's branch drifts behind main | dev2 syncs down (`merge main`) every turn, before working |
| Worktree missing .env files | .env files are committed in `environments/` (shared, not per-worktree) |
| Agent builds from wrong worktree | `REPO_ROOT` in each `.env` points to the correct worktree path |

## Setup (one-time)

```bash
# Create worktrees (from /workspace, on main)
git worktree add .worktrees/dev1 -b dev1 main
git worktree add .worktrees/dev2 -b dev2 main
git worktree add .worktrees/qa   -b qa   main

# Bring up each agent's environment stack
docker compose -f environments/stack/docker-compose.yml --env-file environments/dev1/.env -p dev1 up -d --build
docker compose -f environments/stack/docker-compose.yml --env-file environments/dev2/.env -p dev2 up -d --build
docker compose -f environments/stack/docker-compose.yml --env-file environments/qa/.env   -p qa   up -d --build
```

**Cleanup when an agent is done:**

```bash
# Tear down the agent's stack
docker compose -f environments/stack/docker-compose.yml --env-file environments/dev2/.env -p dev2 down -v

# Remove the worktree + branch
git worktree remove .worktrees/dev2
git branch -d dev2

# Retire its state file (optional — an idle line is harmless; removing it is cleaner)
rm /workspace/.state/dev2.md
```

## When to revert to single-dev

If the Dev Queue has fewer than ~2 disjoint items, or if dev1 and dev2 keep picking the same files, revert to single-dev: tear down dev2's stack, remove the dev2 worktree, and have dev1 work alone. The dual-dev protocol is an optimization for periods of high backlog, not a permanent topology.
