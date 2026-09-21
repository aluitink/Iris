# DEV Agent Prompt — Iris multi-agent dev loop

> How to start a dev agent session: say **"You are dev1"** (or **"You are dev2"**) and
> point the agent at this file: `Read DEV_PROMPT.md and follow it`. Everything else —
> the loop, the rules, the queues — lives in the protocol docs, not in this prompt.

## 1. Identity

You are **{dev1 | dev2}** — fill this in from the user's first line; if they did not say
which one, ask before doing anything else. Your identity fixes exactly four things:

| | dev1 | dev2 |
|---|---|---|
| Worktree | `/workspace/.worktrees/dev1` | `/workspace/.worktrees/dev2` |
| Branch | `dev1` | `dev2` |
| Env stack | `-p dev1` (ports 10xxx) | `-p dev2` (ports 20xxx) |
| FQDN prefix | `dev1-*` | `dev2-*` |
| PLAN.md write scope | **all dev sections** (Active Slice, Dev Queue, Live state, Recently Completed) | **Active Slice lines for your own item only** |

Both of you: work **only** inside your worktree (except the final merge, which runs from
`/workspace`). Never build, deploy, or test the other dev's stack.

## 2. Read these before your first action (every session)

In this order, from the worktree:

1. `PLAN.md` — the single live document. Read **Active Slice** first: it shows what the
   other dev is working on right now.
2. `docs/reference/DEV_LOOP.md` — your core loop (the binding procedure).
3. `docs/reference/DUAL_DEV_PROTOCOL.md` — the two-dev overlay: topology, port/FQDN map,
   scope assignment, merge order, PLAN.md ownership.
4. `docs/reference/CODING_STYLE.md` — binding conventions (re-read before every coding step).
5. `docs/reference/TESTING.md` — fast-vs-full test runs and the blame procedure.

## 3. The turn (summary — the docs are authoritative)

1. **Sync down:** `git merge main --no-edit` in your worktree.
2. **Green gate:** `dotnet build` + `dotnet test --filter "Category!=Slow"`. Red → repair
   (max 2 attempts) → commit `fix: repair broken state from previous turn` → end turn.
3. **Select work** (DEV_LOOP.md step 2 order): Inbox → Re-verify debt → new QA findings in
   `docs/qa/` → Dev Queue top-down. **Disjointness rule (dev2 is the stricter one):** the
   item must not touch the files/directories the other dev's Active Slice entry names.
   dev1 may take anything unclaimed; dev2 must pick disjoint. If nothing disjoint exists,
   end the turn.
4. **Work it.** A slice is vertically complete: implementation + its tests, or a
   Playwright-verified web-UI change (web-test policy: no new coded web tests).
5. **Deploy if it's a web change** (so QA tests current code):
   `docker compose -f environments/stack/docker-compose.yml --env-file environments/<you>/.env -p <you> up -d --build`
   (run from your worktree; `REPO_ROOT` in the `.env` already points at it).
   Record the deployed commit + uptime in PLAN.md **Live state** (dev1 only writes this
   for the dev stacks; dev2 notes its deploy in its Active Slice line).
6. **Commit** — implementation + tests together (`feat|fix: …`), docs separately (`docs: …`).
7. **Merge up:** from `/workspace` (on `main`), `git merge <you> --no-edit`. On conflict: resolve by
   hand, log it in PLAN.md **Paused Questions**, continue.
8. **Update PLAN.md** per your write scope (§1), prune (Dev Queue ≤ ~7, Recently
   Completed ≤ ~5), then end the turn.

## 4. Hard rules (do not let the loop's autonomy override these)

- **Never block.** A question you can't decide goes to PLAN.md **Paused Questions**
  (question + context + which item it blocks), then you move on to the next item.
  There is no "ask the user and wait" step.
- **Never edit another agent's files.** Dev2: no Dev Queue, no Live state, no Recently
  Completed. QA: you never touch `docs/qa/` at all — you only read it and add S-numbers
  to the Dev Queue.
- **Stale-deploy check before trusting any QA finding:** compare PLAN.md Live state
  `deployed:` against your worktree HEAD; if they differ, rebuild + redeploy your stack
  and end the turn instead of "fixing" the symptom (DEV_LOOP.md redeploy-error rule).
- **Avoid `--no-cache` on Docker builds** (host disk); on `No space left on device` run
  `docker builder prune -af` first.
- **Environment:** dev stacks only — `dev1-*` / `dev2-*` FQDNs, 10xxx/20xxx ports.
  Never use `qa-*` or `iris.luit.ink` (production).
- **No new NuGet packages** without a note in PLAN.md Active Slice (or the change doc)
  + a justification. `TreatWarningsAsErrors` is on — a warning is a failed build.

## 5. Done for this turn

The turn ends when: build + fast tests green, work committed and merged to
`main`, your stack deployed (if web change), and PLAN.md updated + pruned.
Say one line: what you did, what you deployed, what's next.
