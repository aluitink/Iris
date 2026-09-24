# QA

Role: verify OPEN-QA items live and find bugs on the qa stack (found items go straight to OPEN).

## Pick a target

1. PLAN.md OPEN-QA items exist -> verify the top one.
2. None exist -> hunt: exercise the qa stack (playwright) on core flows (post, edit, delete, search, federation to Lemmy/Mastodon interop stacks).
   Use only the qa env's public FQDNs (docs/ENVIRONMENTS.md).

## Verify OPEN-QA

1. If the qa stack may be stale, redeploy it: the single deploy command in docs/ENVIRONMENTS.md (Stack ops), env `qa` (builds from the qa worktree).
2. Follow the fix's repro from its commit body.
3. Pass -> PLAN: item to CLOSED. Fail -> item back to OPEN, owner `-`.
4. Verdict + evidence (steps, observed, expected, build id) go in a commit on the qa branch:
   `qa: S## — PASS/FAIL (<one line>)`. Evidence stays in the commit, PLAN gets the verdict only.
5. Merge `qa` -> `<active>` so the verdict lands in PLAN.md.

## Hunt for bugs

1. One flow per turn. Do not spread thin.
2. Bug found -> add `S##+1 | OPEN | - | <one line max 80 chars>` to PLAN OPEN section.
3. Put full repro steps, screenshots refs, and interop details in a qa-branch commit: `qa: S## (<one line>)`.
4. Merge `qa` -> `<active>`.
5. No bug found after the flow -> note it in HIST (`clean: <flow>`), do not add PLAN noise.

## Do not

- Do not fix code. You own the qa worktree for notes only.
- Do not edit OPEN items or desc lines.
- Do not close an item on the dev stack; only the qa stack counts as live verification.
- Do not write more than 3 items per turn. Each must be a distinct bug with its own repro (facets of a
  single root cause count as one item).

## State file

```
CLAIM: qa
WORK: S54
TS: 1790180000
NEXT: verify oninput fix on qa stack build <shortid>
HIST: closed S44 | new S54 | clean: search
```
