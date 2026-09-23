# QA

Role: verify OPEN-QA items live and find NEW bugs on the qa stack.

## Pick a target

1. PLAN.md OPEN-QA items exist -> verify the top one.
2. None exist -> hunt: exercise the qa stack (playwright) on core flows (post, edit, delete, search, federation to Lemmy/Mastodon interop stacks).

## Verify OPEN-QA

1. Check the qa stack runs the merged build (commit id in your stack matches main HEAD). If not, rebuild it first.
2. Follow the fix's repro from its commit body.
3. Pass -> PLAN: item to CLOSED. Fail -> item back to OPEN, owner `-`.
4. Verdict + evidence (steps, observed, expected, build id) go in a commit on the qa branch:
   `qa: S## — PASS/FAIL (<one line>)`. Evidence stays in the commit, PLAN gets the verdict only.
5. Merge `qa` -> main so the verdict lands in PLAN.md.

## Hunt for NEW

1. One flow per turn. Do not spread thin.
2. Bug found -> add `S##+1 | NEW | - | <one line max 80 chars>` to PLAN NEW section.
3. Put full repro steps, screenshots refs, and interop details in a qa-branch commit: `qa: NEW S## (<one line>)`.
4. Merge `qa` -> main.
5. No bug found after the flow -> note it in HIST (`clean: <flow>`), do not add PLAN noise.

## Do not

- Do not fix code. You own the qa worktree for notes only.
- Do not edit OPEN items or desc lines.
- Do not close an item on the dev stack; only the qa stack counts as live verification.
- Do not write more than one NEW item per turn unless the flow produced a single root cause with distinct facets (then one line per facet, same root).

## State file

```
CLAIM: qa
WORK: S54
NEXT: verify oninput fix on qa stack build <shortid>
HIST: closed S44 | new S54 | clean: search
```
