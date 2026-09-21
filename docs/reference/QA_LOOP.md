# Iris — QA Loop

> One of two parallel workstreams driven by [PLAN.md](../../PLAN.md). This is the **QA** loop: it owns the **live app's behavior** and the **QA Queue** (`docs/qa/`). The **dev** loop ([DEV_LOOP.md](DEV_LOOP.md)) owns **code** and the **Dev Queue**. You find problems; dev fixes them. You never edit code.

## Principles

- **You test the live app, not the code.** Every pass is **Playwright-driven** against the QA cluster, primarily `https://qa-iris-a.luit.ink` (with QA peers `https://qa-lemmy.luit.ink` and `https://qa-mastodon.luit.ink`). `https://iris.luit.ink` is the production FQDN and is not used for dev/test work; the dev cluster uses `https://dev-iris-a.luit.ink` / `https://dev-iris-b.luit.ink` (legacy `iris-dev1` / `iris-dev2` names still map to the same ports).
- **You work in an isolated worktree.** All your doc writes (`docs/qa/` + your PLAN.md sections) happen in a git worktree on a `qa/` branch, then **merge back**. This means dev and QA never write the same file at the same time — the two-person conflict problem is solved at the filesystem layer, not by politeness.
- **A finding is a document, not a PLAN.md paragraph.** One doc per finding in `docs/qa/` (template in its [README](../qa/README.md)). PLAN.md carries only a count + top-priority pointer.
- **You verify fixes, you don't just trust them.** A finding flips to `fixed` only after you re-confirm it **from a clean entry** on a container whose **deployed commit is current**. No evidence, no `fixed`.
- **The loop is self-maintaining.** You prune the pass log and keep PLAN.md's QA sections bounded every pass.

## Isolation model (worktrees)

Dev works in the main checkout (`/workspace`) on its branch. QA works in a **worktree** — a second checkout of the same repo on a `qa` branch. Both see the same commits; neither can clobber the other's uncommitted work.

```
/workspace                 ← dev (main checkout, e.g. branch interop-testing)
  └─ code + docs/changes + docs/decisions  (dev-owned)
/workspace/.worktrees/qa   ← QA  (worktree, branch qa)   [gitignored — see note]
  └─ docs/qa/ + PLAN.md QA sections          (QA-owned)
```

> **Prerequisite — the shared docs must be committed.** A worktree is a checkout of a **commit**, not of the working tree. `scripts/qa-worktree.sh create` branches `qa` from the **current HEAD**, so anything that is only in the main checkout's *uncommitted* working tree (e.g. `docs/qa/` before its first commit) **will not appear in the worktree** — it looks like it vanished. Before the first QA pass, make sure `docs/qa/`, the loop docs, and PLAN.md are all committed on the main branch. (The worktree's own files are never tracked in the main repo — git records worktrees in `.git/worktrees/` metadata, not as files — so `.worktrees/` is gitignored only to keep IDE/file-watcher/backup tools from tripping over a second checkout.)

Why a worktree instead of editing in place:

- **No lost work.** If dev commits PLAN.md while you're mid-edit, you don't lose either change — you rebase/merge your `qa` branch on top.
- **Clean audit trail.** Every QA doc change is a commit on `qa`, then a single `Merge qa` into the main branch. Easy to review, easy to revert.
- **You can't touch code.** Your worktree is where you *should* only ever touch `docs/qa/` and your PLAN.md sections — there's no reason to build, so there's no temptation.

**Setup** (once, or via the helper `scripts/qa-worktree.sh`):

```bash
scripts/qa-worktree.sh create     # creates /workspace/.worktrees/qa on branch qa
scripts/qa-worktree.sh status     # show worktree + branch + uncommitted docs
scripts/qa-worktree.sh sync       # rebase qa onto the main branch (before a pass)
scripts/qa-worktree.sh merge      # merge qa into the main branch, then reset qa to main
scripts/qa-worktree.sh destroy    # remove the worktree (only when clean)
```

If the helper isn't present, the raw commands are:

```bash
git worktree add .worktrees/qa -b qa                 # create (from the repo root)
git -C .worktrees/qa commit -am "qa: <what>"          # commit your doc work
git merge qa                                          # from the main checkout: merge back
git worktree remove .worktrees/qa                     # destroy (when clean)
```

**Conflict policy (the whole point):** you and dev write **disjoint file sets** (see [Ownership map](#ownership-map)). A PLAN.md conflict can only happen if both touched the *same section* — which the ownership map forbids. If a merge conflict ever does appear, **don't guess and don't block the loop**: log it in PLAN.md's **Paused Questions** (which section, both sides' intent), abort this merge, and continue with the rest of the pass (or end the pass cleanly). A conflict means the ownership rule was broken — a human resolves it.

## The Loop

> **CRITICAL RUNTIME RULE FOR WEBPAGE ACCESS:** prevent stale data / frontend caching on every action. For every new task, navigation, or data-refresh step, do a **hard reload / cache bypass** *before* reading any data — navigate with the `networkidle` wait and don't proceed until fresh server requests have settled. Never rely on previously opened states, in-memory page references, or a cached (content-hash) Blazor WASM. **When re-verifying after a rebuild, use a fresh browser context (close/reopen).**

### 0. Worktree sync + staleness pre-flight (every pass, before anything else)

> **The sync is the #1 discipline.** QA has drifted before (71 ahead / 19 behind) because the down-sync and the merge-back were skipped for many passes. A pass that doesn't start from a synced `qa` is testing a stale codebase and its findings may be wrong; a pass that doesn't merge back means dev never sees the work. **Both are mandatory, every pass, no exceptions.**

1. **Sync `qa` DOWN onto the main branch first (mandatory).** `git -C .worktrees/qa merge <main-branch>` (or `scripts/qa-worktree.sh sync`). This pulls in dev's new commits (code + change docs + PLAN.md dev sections) so your `qa` branch is a **strict superset** of main: it contains everything dev has *plus* your committed QA docs. Verify after: `git rev-list --count <main-branch>..qa` should be **0 behind** (i.e. `git rev-list --count qa..<main-branch>` = 0). **If it's behind, you did not sync — stop and fix it before testing.** A clean merge is expected (disjoint file sets); a conflict → [Conflict policy](#conflict-policy).
   - **If `docs/qa/` is missing from the worktree, the shared docs aren't committed on the main branch yet — stop and get them committed first** (see the [prerequisite note](#isolation-model-worktrees)).
2. **Staleness check — the #1 false-finding source (two-part).**
   - **(a) Code staleness:** compare the **deployed build** (PLAN.md **Live state** `deployed:`, or the QA cluster's current image commit) to the synced `qa` HEAD. **If `src/` changed** since the deployed build (`git log --oneline <deployed>..HEAD -- src/` is non-empty) → the live cluster is **stale**: **rebuild + redeploy the QA cluster to HEAD** (or wait for dev to), then re-check. **Never log findings against a build you know is behind.**
   - **(b) Build unchanged:** if `src/` is unchanged since the deployed build, **no rebuild is needed** — proceed. Record the deployed commit in the pass log ("build `<commit>` (== HEAD? y/n)").
3. **Restart the MCP Playwright service** if it looks down/stale: `bash scripts/start-playwright.sh` (recreates the `playwright-mcp-service` container, caching disabled, `--isolated`, host port 8931).

### 1. Plan the pass

- Pick the area: continue from the **Resume checkpoint** in `docs/qa/passes.md` (never restart from scratch), or target a specific open finding to **re-verify** if dev just deployed a fix.
- **Clean entry (every pass, every re-verification):** close the browser entirely, clear cookies + storage, reopen, enter the app fresh. Never carry state between passes or between a defect and its re-verification.

### 2. Drive the app (MCP Playwright) on the QA cluster

- **Primary QA app:** `https://qa-iris-a.luit.ink` (use `https://qa-lemmy.luit.ink` and `https://qa-mastodon.luit.ink` for peer tests). Do not use `https://iris.luit.ink` for development or QA, as it is the production/public FQDN.
- **Primary account: `andrew` / `Password1`** (real content + external contacts — use it to evaluate every page).
- **Secondary accounts:** `bob`, `carol`, `dave` (register as needed) for multi-account flows (follows, communities, moderation, notifications).
- **Authless pass:** every page visited signed-out — verify gating (302 to login), no data leaks, no console errors, sensible signed-out UI.
- **Work the page inventory, not "every page":** the **Page coverage** table (in `docs/qa/passes.md`) lists all routes. Work it top-to-bottom; mark each route's signed-in + authless boxes done or `skipped(<reason>)`. Update the **Resume checkpoint** at the end so the next pass continues where this one stopped.
- **Deep dive per page:** exercise every control, every state (empty/populated/error), deep links + hard refresh on each route.
- **Capture console errors** (`browser_console_messages`). For screenshots, call the screenshot tool **with no `filename`** (it can't write to an arbitrary path); it auto-saves to `tmp/.playwright-mcp/page-<ts>.png` and returns that path — cite the returned path, don't promise to attach a file.
- **Network watch — count calls, not bytes (always on):** catch **request spam** (duplicate/redundant calls on load: same fetch twice on mount, refetch on re-render, N+1 fan-out). For each distinct request pattern record method+path, status, **how many times it fired** (the count is the spam signal), and *what triggered it*.

### 3. Triage → write/update the finding doc

For every finding:

1. **Is it new, or a re-confirmation of an existing finding?**
   - **Existing** → update that doc's *Status* / *Found* (add this pass) — don't create a new file.
   - **New** → create `docs/qa/sNN-<slug>.md` from the [README template](../qa/README.md). `NN` continues the S-numbering.
2. **Fill in:** page, repro, expected vs actual, **class** (blocker / bug / UX / perf / data-integrity / feature-gap) + **severity** (S1/S2/S3), root cause *if you can see it from behavior*, and the **Re-verify** steps.
   - Class is the routing axis (blocker → dev's blocker slice, bug → dev's fix slice, UX → a later UX slice, perf → a later efficiency slice). Severity sets priority *within* the class.
3. **Environment check before you call it a defect:** is this a remote instance being down / rate-limited / transient? The proxy relays upstream 5xx/404s faithfully — a remote being down is **not** an Iris bug. Mark it `environmental` in the doc, don't queue it.
4. **Update PLAN.md's QA sections** (in your worktree): the **QA Queue** count + top-priority pointer, and the **Last pass** line. **Do not** write finding detail into PLAN.md — one-line pointer only.

### 4. Re-verify fixes (when dev has deployed a fix)

- For each finding dev says is fixed (PLAN.md's **Re-verify** list, or a finding doc marked `fix committed, not yet live`):
  1. Confirm **Live state deployed == HEAD** (the staleness check). If not, the fix isn't live yet — leave it, don't mark it.
  2. **Clean entry**, then run the finding's **Re-verify** steps.
  3. **Pass** → set the doc's Status to `fixed (<date>, <commit>)` + record evidence (`console-clean + <control/state> works`, optionally the screenshot path). Add the finding to PLAN.md's **Re-verify → done** (dev clears it).
  4. **Fail** → set Status back to `open`, note *what* still fails + new repro. This goes back to dev via the Dev Queue.
- **No evidence, no `fixed`.**

### 5. Commit + merge back (mandatory — this is how dev sees your work)

> **A pass is not done until it's merged back.** If you only commit on `qa` and don't merge, dev never sees the finding and the Re-verify contract can't start. This is the single point where QA work becomes visible — **it is not optional.**

- In your worktree: `git -C .worktrees/qa add docs/qa/ PLAN.md && git -C .worktrees/qa commit -m "qa: pass NN — <summary>"`.
- **Merge back** to the main branch: from `/workspace`, `git checkout <main-branch> && git merge qa` (or `scripts/qa-worktree.sh merge`). Because Step 0 already synced `qa` down onto main, `qa` is now a **strict superset** of main → this merge is a **fast-forward** (or a trivial merge) and **cannot conflict on code** (QA never touches `src/`/`tests/`).
- **Verify the merge landed:** `git -C /workspace log --oneline -1 <main-branch>` should show your pass commit (or the merge commit). **If you can't see it on main, the merge didn't happen — redo it before ending the pass.**
- If a conflict *does* appear (it shouldn't), **stop** → [Conflict policy](#conflict-policy).

### 6. Prune + checkpoint

- Append this pass to `docs/qa/passes.md` **as a brief entry, not a narrative.** Passes can run fast, so the log is the one QA artifact that grows every single pass — keep each entry to a few lines so the file stays small even after many passes:

  ```
  ## Pass NN (<date>) — <area>
  - **Build/Live:** deployed `<commit>` (== HEAD? y/n)
  - **Explored:** <1 line: what was exercised>
  - **Result:** <N> passed clean; <N> re-confirmed (list IDs); <N> new (list IDs → doc)
  - **Checkpoint:** next pass continues at <area/route>
  ```

  No repro detail, no root cause, no console dumps here — that all lives in the **finding docs** (`sNN-*.md`). The pass log is an index, not a record of the findings.
- **Hard cap:** if `passes.md` exceeds ~40 entries (or ~400 lines), archive the oldest half into `docs/qa/passes-archive.md` and keep only the recent ones. Check the size at the start of every pass, not just when it feels big.
- Update the **Resume checkpoint** (where to continue next pass).
- Commit the prune with the pass commit.

## Re-verify etiquette (the dev↔QA contract)

- **Dev** commits a fix + rebuilds + redeploys + updates **Live state**, and adds the finding to PLAN.md's **Re-verify** list.
- **QA** only flips a finding to `fixed` after a clean-entry re-verify **on a current build**. QA updates the finding doc's Status (QA owns `docs/qa/`); dev clears the Re-verify line (dev owns PLAN.md's Re-verify).
- A finding is **not** "fixed" because the code looks right. It's fixed when QA sees it work, on the deployed commit, from a clean entry.

## Ownership map (files + PLAN.md sections)

| Asset | Owner | The other loop may… |
|---|---|---|
| `docs/qa/**` (findings, pass log) | **QA** | read; link |
| PLAN.md **QA Queue / Last pass / Resume checkpoint** | **QA** | read |
| PLAN.md **Re-verify list** | dev clears / QA adds | — |
| `docs/changes/**`, `docs/decisions/**`, all code | **Dev** | read |
| PLAN.md **Now / Active Slice / Dev Queue / Inbox / Live state / Recently Completed** | **Dev** | read |
| PLAN.md **Paused Questions** | either (whoever is blocked) | — |

**File-level rule:** QA writes only `docs/qa/` + PLAN.md's QA sections. Dev writes only code + `docs/changes/` + `docs/decisions/` + PLAN.md's dev sections. **Neither edits the other's files.**

## Conflict policy

Because the two loops write **disjoint file sets**, a merge conflict should be impossible. If one appears:

1. **Don't resolve by guessing, and don't block the loop.** Abort the merge (`git merge --abort`).
2. Record it in PLAN.md's **Paused Questions** (which section, both sides' intent) — a short ticket, not an essay.
3. **Continue** with the rest of the pass (or end the pass cleanly). A human resolves the conflict by hand; the loop keeps moving.

A conflict is a signal the ownership rule was broken — fix the rule (or the process), don't just the file.

## Keeping the QA docs lean

- **PLAN.md** holds only: the QA Queue count + top-priority pointer, the Last-pass line, the Resume checkpoint, and the Re-verify list. No finding detail.
- **`docs/qa/README.md`** is the index (one row per finding: ID, title, class, severity, status, link) + the template + the process.
- **One file per finding** (`sNN-<slug>.md`): Symptom / Root cause / Fix / Re-verify. Status lives in the file's header, updated as it moves.
- **`passes.md`** is the append-only pass log — **brief entries only** (a few lines each: build/live, explored, result IDs, checkpoint). Finding detail lives in the `sNN-*.md` docs, never here. Hard cap ~40 entries; archive the oldest half to `passes-archive.md`.

## Failure Modes to Avoid

| Failure mode | Guard |
|---|---|
| **`qa` drifts behind main (dev's fixes never pulled down) → findings on a stale codebase** | Step 0.1: **mandatory down-sync** (`merge <main-branch>` into `qa`) every pass; verify `qa` is 0 behind before testing |
| **QA work never merged back → dev never sees the findings, Re-verify can't start** | Step 5: **mandatory merge-back** every pass; verify the pass commit is on main before ending |
| **Cluster not redeployed after a `src/` change → testing the old build** | Step 0.2(a): if `src/` changed since the deployed build, **rebuild + redeploy** (or wait) before logging findings |
| **Testing a stale build → "finding" an already-fixed bug** | Step 0 staleness pre-flight: never start a pass if Live state ≠ HEAD |
| **Worktree starts without `docs/qa/` (docs not committed yet)** | [Prerequisite note](#isolation-model-worktrees): a worktree checks out a commit, not the working tree — get the shared docs committed on the main branch before the first pass |
| QA and dev clobber the same file | Worktree isolation + [ownership map](#ownership-map) + [conflict policy](#conflict-policy) |
| PLAN.md bloats with finding detail again | Findings live in `docs/qa/`; PLAN.md is count + pointer only |
| A finding is marked `fixed` without proof | Step 4: clean-entry re-verify on a current build; no evidence, no `fixed` |
| Restarting the whole page inventory every pass | Resume checkpoint in `passes.md`; continue where you stopped |
| Calling a remote outage an Iris bug | Step 3 environment check: mark `environmental`, don't queue |
| Missing request spam | Step 2 network watch: count calls, not bytes |
| **The pass log ballooning (passes run fast)** | Step 6: each entry is a brief 4-line index (no detail — that's in the finding docs); hard cap ~40 entries, archive the oldest half |
