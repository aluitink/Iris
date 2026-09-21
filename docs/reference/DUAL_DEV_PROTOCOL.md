# Iris — Dual-Dev Protocol

> Extension to [DEV_LOOP.md](DEV_LOOP.md) + [QA_LOOP.md](QA_LOOP.md) for **two parallel dev agents** + one QA agent. The existing protocol assumes a single dev; this doc adds the coordination rules for two.

## Topology

```
/workspace                  ← dev1 (main checkout, branch interop-testing) — "primary dev"
  └─ code + docs/changes + docs/decisions  (dev-owned)
/workspace/.worktrees/dev2  ← dev2 (worktree, branch dev2)               — "secondary dev"
  └─ code + docs/changes + docs/decisions  (dev-owned, disjoint scope)
/workspace/.worktrees/qa    ← QA   (worktree, branch qa)
  └─ docs/qa/ + PLAN.md QA sections          (QA-owned)
```

**Key invariant:** dev1 and dev2 work on **disjoint code areas**. The assignment is recorded in PLAN.md's Active Slice (which S-numbers / which directories each dev owns for the current turn).

## Role assignments

| Agent | Worktree | Branch | Deploy target | Owns |
|---|---|---|---|---|
| **dev1** (primary) | `/workspace` | `interop-testing` | single-instance stack (`iris.luit.ink:8088`) | PLAN.md dev sections (Active Slice, Dev Queue, Live state, Recently Completed) |
| **dev2** (secondary) | `.worktrees/dev2` | `dev2` | **none** (dev2 does NOT deploy; it commits + merges to `interop-testing`, and dev1 deploys) | code in its assigned area; `docs/changes/` for its slices |
| **QA** | `.worktrees/qa` | `qa` | QA cluster (QA-owned) | `docs/qa/` + PLAN.md QA sections |

**Why dev2 doesn't deploy:** the single-instance stack is a shared resource. If both dev1 and dev2 deploy, the last one wins and the other's work is invisible to QA. By making dev1 the sole deployer, there's exactly one deployed commit at any time, and QA's staleness check remains well-defined.

**Why dev2 merges to `interop-testing`:** dev2's work must be visible to dev1 (so dev1 can deploy it) and to QA (so QA can test it). Merging to the main branch makes both true. dev1 deploys whatever is on `interop-testing` HEAD.

## Turn structure

### dev1 (primary) — every turn

1. **Pull latest** (`git pull --rebase` or `git fetch && git rebase`).
2. **Merge dev2 if it has new commits:** `git merge dev2 --no-edit` (fast-forward expected; dev2 merges to `interop-testing` and dev1 is already on it, so this is a no-op or a trivial merge).
3. **Run build + fast tests.** Red state → repair (max 2 attempts) → end turn.
4. **Select work item** per DEV_LOOP.md step 2 (Inbox → Re-verify → new QA findings → Dev Queue).
5. **Assign dev2's scope** in PLAN.md's Active Slice: which S-numbers / directories dev2 owns this turn. This must be **disjoint** from dev1's own work area.
6. **Work on dev1's item** (code + tests).
7. **Deploy** if there's a web change: build + `docker compose build iris-web && docker compose up -d --force-recreate iris-web` from `/workspace/apps/Iris.Web`.
8. **Update PLAN.md** (Active Slice, Dev Queue, Live state, Recently Completed).
9. **Commit** (implementation + tests together; docs separate).

### dev2 (secondary) — every turn

1. **Sync down:** `git -C .worktrees/dev2 merge interop-testing --no-edit` (pull in dev1's latest + any QA doc merges).
2. **Read PLAN.md's Active Slice** to see what scope dev1 assigned this turn. If no scope is assigned, **end the turn** (dev2 waits for dev1 to assign work).
3. **Run build + fast tests** in the worktree. Red state → repair (max 2 attempts) → end turn.
4. **Work on the assigned item** (code + tests). Stay within the assigned scope.
5. **Commit** in the worktree: `git -C .worktrees/dev2 add -A && git -C .worktrees/dev2 commit -m "feat(...): ..."`.
6. **Merge to `interop-testing`:** from `/workspace`, `git merge dev2 --no-edit`. (dev1 does this, not dev2 — dev2 only commits to its branch. If dev2 is running autonomously, it can merge itself, but the merge must be a clean fast-forward or trivial merge.)
7. **Do NOT deploy.** dev1 deploys on its next turn.
8. **Update PLAN.md** — dev2 does NOT write to PLAN.md's dev-owned sections (dev1 owns them). dev2 writes only to `docs/changes/` for its slice. If dev2 needs to record progress, it adds a note to the change doc, not PLAN.md.

**Exception — dev2 can update PLAN.md's Active Slice** to record its own progress (e.g. "S17: in progress — reduced top-up cap, removing @key"). This is a narrow exception to the "dev1 owns PLAN.md dev sections" rule, justified by the need for dev1 to see dev2's state without a merge. The exception is limited to: (a) the Active Slice section, (b) only the lines describing dev2's assigned item, (c) no edits to Dev Queue / Live state / Recently Completed.

### QA — unchanged

QA's protocol is identical to [QA_LOOP.md](QA_LOOP.md). QA tests the single-instance stack (dev1's deploy) and the QA cluster (QA-owned). QA does not know or care whether the deployed code came from dev1 or dev2 — it only sees the deployed commit.

## Scope assignment (the critical coordination point)

dev1 assigns dev2's scope **every turn** in PLAN.md's Active Slice. The assignment must be:

1. **Disjoint from dev1's own work.** If dev1 is working on S36 (home feed, `src/Iris.Server/ActivityPubServerExtensions.cs`), dev2 must not touch that file or that code path.
2. **Vertically complete.** The assigned item must be a full slice (impl + tests), not a half-item.
3. **Deployable.** If the assigned item is a web change, it will be deployed by dev1 on dev1's next turn. dev2 should note in the change doc "awaiting deploy by dev1."

**How to pick a disjoint item:**
- Look at the Dev Queue (sorted: blockers → S2 → S3 → feature scope).
- Skip items that touch the same files/directories as dev1's current item.
- Prefer items in different layers: if dev1 is in `src/Iris.Server/`, dev2 can take a `src/Iris.Client/` or `apps/Iris.Web.Client/` item.
- If no disjoint item exists, dev2 waits (no assignment this turn).

## Conflict resolution

**Code conflicts:** should not happen if scope is disjoint. If a merge conflict appears:
1. dev1 (who does the merge) resolves it by hand.
2. Log the conflict in PLAN.md's Paused Questions (which file, both sides' intent).
3. The loop continues; a human reviews if needed.

**PLAN.md conflicts:** dev1 owns the dev sections; dev2 only writes to Active Slice's dev2-assigned lines. A conflict here means the ownership rule was broken → log in Paused Questions.

**Deploy conflicts:** impossible by design (dev1 is the sole deployer).

## Staleness check (QA's perspective)

QA's staleness check is unchanged: compare PLAN.md's **Live state** `deployed:` against the QA cluster's current build. The deployed commit may include work from both dev1 and dev2 (merged via `interop-testing`). QA does not need to distinguish whose work is in the build.

## Failure modes specific to dual-dev

| Failure mode | Guard |
|---|---|
| dev1 and dev2 touch the same file → merge conflict | Scope assignment: disjoint areas; dev1 reviews before merging |
| dev2 commits work that dev1 never deploys → QA tests stale build | dev1 merges dev2 every turn; deploy happens on dev1's turn |
| dev2 works on an item that dev1 also needs → conflict | dev1 assigns scope every turn; dev2 waits if unassigned |
| dev2 writes to PLAN.md dev sections → clobbers dev1's state | Ownership rule: dev2 writes only Active Slice's dev2 lines + `docs/changes/` |
| Both devs deploy → last-one-wins, QA sees inconsistent state | dev2 does NOT deploy; dev1 is sole deployer |
| dev2's branch drifts behind main | dev2 syncs down (`merge interop-testing`) every turn, before working |
| dev1 forgets to merge dev2 → dev2's work invisible | dev1's turn step 2: merge dev2 before selecting work |

## Setup (one-time)

```bash
# Create dev2's worktree (from /workspace, on interop-testing)
git worktree add .worktrees/dev2 -b dev2 interop-testing

# Add .worktrees/ to .gitignore (if not already present)
echo ".worktrees/" >> .gitignore
git add .gitignore && git commit -m "chore: gitignore worktrees"
```

**Cleanup when dev2 is done:**

```bash
git worktree remove .worktrees/dev2
git branch -d dev2
```

## When to revert to single-dev

If the Dev Queue has fewer than ~2 disjoint items, or if dev2's items keep conflicting with dev1's, revert to single-dev (destroy the dev2 worktree, dev1 works alone). The dual-dev protocol is an optimization for periods of high backlog, not a permanent topology.
