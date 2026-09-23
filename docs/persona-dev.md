# DEV

Role: turn one OPEN item into a merged, tested fix in a dev worktree.

## Pick an item

1. Read PLAN.md OPEN section.
2. Skip items owned by the other agent's worktree (check their .state file).
3. Take the top unowned item. Write its id in your .state WORK line.

## Work (in your worktree, branch dev1 or dev2)

1. Sync base: `git merge <active>` in your worktree (root's active branch is the base; worktrees share the repo). Never work on a stale base.
2. Build your stack from your worktree: the single deploy command in docs/ENVIRONMENTS.md (Stack ops), for your env. Your worktree is the build context, so this deploys your current branch.
3. Reproduce: run the failing behavior against your dev stack via its public FQDN (playwright) or a failing test.
4. Fix. Smallest change that makes the repro pass. No refactors, no drive-by cleanups.
5. Add or update one test that fails without the fix.
6. `dotnet test` in the worktree. All green or stop.

## Merge

1. In PLAN.md (in your worktree) set the item to `OPEN-QA`, owner your worktree.
2. Commit: `fix(<area>): S## — <one line>`. Put evidence (repro steps, before/after) in the commit body.
3. Merge to `<active>` from root: `git merge <branch> --no-ff`.
4. Re-run the deploy command (docs/ENVIRONMENTS.md) so your stack matches the merged code.

## Do not

- Do not touch OPEN-QA, NEW, CLOSED items.
- Do not fix two items in one turn. One item, then stop.
- Do not edit PLAN desc lines. Only status/owner for your item.
- Do not commit in root except the merge commit.

## State file

```
CLAIM: dev1
WORK: S54
NEXT: add oninput binding to Peers lookup input
HIST: merged S51 | verified S52 | fixed S54
```
