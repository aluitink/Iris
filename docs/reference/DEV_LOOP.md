# Iris — Dev Loop

> One of two parallel workstreams driven by [PLAN.md](../../PLAN.md). This is the **developer** loop: it owns **code** (source + tests) and the **Dev Queue**. The **QA** loop ([QA_LOOP.md](QA_LOOP.md)) owns the live app's behavior and the **QA Queue** (`docs/qa/`). Read both; you are the dev one.

## Principles

- **One turn = one coherent, vertically complete slice of work** — implementation *and* its tests.
- **Red state ends the turn.** A broken build is never papered over with new work.
- **"Done" is defined, not felt.** The Definition of Done below is the only exit from step 3.
- **The docs are binding.** [CODING_STYLE.md](CODING_STYLE.md) wins over habit; [TESTING.md](TESTING.md) defines what coverage a phase needs before it's checkable.
- **[PLAN.md](../../PLAN.md) is the single live document**, but the two workstreams own **different parts of it**: dev owns **Now / Active Slice / Dev Queue / Recently Completed**; QA owns **QA Queue / last-pass / re-verify checkpoints** (see [Ownership map](#ownership-map)).
- **You deploy.** When you commit a web change you also rebuild + redeploy the live container so the QA loop is testing *your* code, not a stale build. Deploying is part of your turn, not a chore for someone else.
- **The loop is self-maintaining.** PLAN.md's bounded lists are pruned every turn as part of step 5, not left to balloon until a human intervenes.

## The Loop

### 1. Confirm good state

- Run `dotnet build` and the **fast** test set (`dotnet test --filter "Category!=Slow"` — see PLAN.md *Test runs*). A SubAgent may run and summarize; fixes happen in the main loop.
- **If failures:** fix *only* the breakage, re-run until green (max 2 repair attempts), commit as `fix: repair broken state from previous turn`, then **end this turn**. No new work.
- **If still failing after 2 attempts:** write a `BLOCKED` note in PLAN.md's Active Slice describing the failure, commit, and end this turn.

### 2. Select the next work item (in this order)

1. **Inbox first.** If PLAN.md's Inbox has an unactioned entry and no slice is in progress, action the oldest entry before pulling anything else. (If a slice *is* in progress, finish it first — the Inbox entry waits one more turn; it stays in the Inbox until actioned.)
2. **QA re-verify debt first (after Inbox).** If PLAN.md's **Re-verify** list is non-empty, work it *before* new feature scope — these are already-committed fixes the QA loop is waiting on. Rebuild + redeploy, flip the finding's status with the QA loop, and clear the list.
3. **Then the Dev Queue**, top to bottom (it is kept sorted: blockers → S2-sev QA fixes → feature scope).
4. **Replenish before selecting:** if the Dev Queue has fewer than ~3 items, pull the next slice(s) from the relevant [docs/plans/](../plans/) deep-dive doc, or expand the next phase from [ROADMAP.md](../ROADMAP.md) into concrete slices. Do this *before* selecting, not as an afterthought.
5. **If everything is exhausted:** define the next phase (later phases are expected to start as a one-line placeholder), add it to ROADMAP.md, seed the Dev Queue with its first slices, commit, end the turn.

A slice must be **vertically complete**: implementation + its tests. Coverage expectations are part of the item, not a follow-up.

### 3. Work on the item

- **Before coding**, re-read [CODING_STYLE.md](CODING_STYLE.md) — especially the [3rd-Party ActivityStreams rules](CODING_STYLE.md#3rd-party-activitystreams-types):
  - Deserialize into `IObjectOrLink` (or `IObject`/`ILink`), then cast — never into a concrete type.
  - Construct with object initializers; let the constructor set `Type`.
  - Collection expressions for multi-valued properties; expect `IEnumerable<T>?` when reading.
  - `Id` is `string?` in the library — convert to `Iri` at the Iris boundary.
- **Definition of done** (all must hold before committing):
  - `dotnet build` clean — `TreatWarningsAsErrors` is on, so a warning is a failure.
  - `dotnet test` green, **including new tests for this item** (integration-first per [TESTING.md](TESTING.md)). *Exception — web-UI work: done = live Playwright-verified; existing web tests stay or are deleted per the [web test policy](#web-test-policy); no new coded tests.*
  - XML doc comments on all public API; `CancellationToken ct` is the last parameter; file-scoped namespaces.
  - No dependency-direction violations (`Iris.Core` never references `Iris.Client`/`Iris.Server`; no upward dependencies).
  - No new NuGet packages without a note in PLAN.md's Active Slice (or the change doc) and a justification.
- **Deploy if it's a web change** (so QA tests current code):
  - `cd /workspace && dotnet build apps/Iris.Web/Iris.Web.csproj -c Release`
  - `cd /workspace/apps/Iris.Web && docker compose build iris-web && docker compose up -d --force-recreate iris-web`
  - **Avoid `--no-cache`** (it fills the host disk; on `No space left on device`, run `docker builder prune -af` first).
  - Record the **deployed commit** + container uptime in PLAN.md's **Live state** (this is the number the QA loop compares against).
- **Open Questions (autonomous default):** if you hit a design fork you can reasonably decide yourself, make the call, record it in the slice's change doc, and continue. Don't stall.
- **Genuine blockers / anything you'd otherwise ask the user about:** **do not pause and do not ask.** Log it in PLAN.md's **Paused Questions** (question + context + the item it blocks), then **move on to another item** (stash the in-flight work first if it's not at a safe commit point). The loop keeps moving; a human clears Paused Questions when convenient. See [Blocking without stopping](#blocking-without-stopping).
- **Mid-turn user injections:** see [Handling mid-loop user input](#handling-mid-loop-user-input).
- **If the slice won't finish this turn:** commit coherent progress, update PLAN.md's Active Slice with a `remaining:` note, and end the turn.

### 4. Commit

- Commit implementation + tests **together** (conventional message, e.g. `feat(server): route remote Join through server-side endpoint`).
- A feature without its tests is a red flag for the next turn's step 1 — never split them. *Exception — web-UI work: verification is manual via MCP Playwright, not coded tests.*

### Web test policy (binding while web-UI work is active)

The production app's web tests (`tests/Iris.Web.Tests`) are **expendable** during UI stabilization:

- **No new coded tests** of any kind; verification is manual via MCP Playwright (live Docker app, real browser).
- A change that **breaks an existing web test**: **delete that test** (log the deletion in the change doc). Never fix the app to satisfy a test, never write a replacement.
- **15-second rule:** any single test taking longer than 15 s is **skipped**. Find offenders via per-test timings: `dotnet test tests/Iris.Web.Tests -v n --logger "console;verbosity=detailed"`, then comment them out (or `[Fact(Skip = "slow >15s — <date>")]`).
- Passing tests stay; a green (or pruned) suite is still required before commit. **Every deleted or skipped test is logged** (test name, action, reason, restore-by) — no silent deletions; the phase's closeout reviews the ledger.

### 5. Update PLAN.md and prune

- Move the finished item out of Active Slice into **Recently Completed** with a one-line summary + link to its change doc.
- **Prune as you go:** if Recently Completed exceeds ~5 entries, move the oldest one-liner into [ROADMAP.md](../ROADMAP.md)'s ledger (append a line, don't rewrite) and remove it from PLAN.md. Do it every turn it applies.
- Remove the actioned entry from Inbox if this turn worked an injected request.
- **Update Live state** with the deployed commit + container uptime (if you deployed).
- **Write the detail doc:** what was built, key types, test counts, lightweight decision → a **per-change doc** in [changes/](../changes/) (one file per slice). A substantial decision (trade-offs, alternatives, spec references) → [decisions/](../decisions/), linked from the change doc. PLAN.md gets only the one-line pointer.
- Commit doc updates with a `docs: ...` commit (separate from the implementation commit).

### 6. End this turn

## When QA reports an error (or you see one) that may be redeploy-related

The single most common class of "mystery" finding is **stale-deploy**: QA is testing a container that predates your last commit. Resolve the ambiguity *before* spending a turn fixing the wrong thing.

**Order of checks (always, in this order):**

1. **Is the deployed commit == HEAD?** Compare PLAN.md's **Live state** `deployed:` against `git log -1 --oneline`.
   - **They differ** → QA tested a stale build. **Rebuild + redeploy (step 3), update Live state, end the turn.** Do *not* "fix" the reported symptom — it may not exist in current code. Tell the QA loop to re-verify from a clean entry.
   - **They match** → the finding is against current code. Proceed to triage.
2. **Triage the finding against `docs/qa/`:**
   - **Known + already fixed but not yet re-verified** → it's on the **Re-verify** list. Rebuild/redeploy and confirm; flip the finding to `fixed` with evidence (with the QA loop).
   - **Known + open** → work it per the Dev Queue (it's already a doc; no re-triage needed).
   - **New** → the QA loop has already written its `docs/qa/` doc. Add it to the **Dev Queue** at the right priority (by class + severity), then work it.
3. **If the error is environmental, not a defect** (remote instance down, rate-limit, transient network): note it in the finding doc as `environmental` and **do not** queue it. The proxy is expected to relay upstream 5xx/404s faithfully — a remote being down is not an Iris bug.

**Rule of thumb:** a fix is only "verified" once QA re-confirms it **from a clean entry** on a container whose deployed commit is current. "The code looks right" is not done.

## Ownership map (PLAN.md)

| PLAN.md section | Owner | The other loop may… |
|---|---|---|
| Now / Active Slice | Dev | read |
| Dev Queue (incl. Inbox) | Dev | read (it reflects QA findings) |
| Re-verify list | Dev (clears it) | adds to it when a fix lands |
| QA Queue (count + top-priority) | QA | — |
| Last pass / Resume checkpoint | QA | — |
| Live state (deployed commit, container) | Dev (writes on deploy) | reads to do staleness checks |
| Recently Completed | Dev | read |
| Paused Questions | either (whoever is blocked) | — |

**File-level rule:** dev writes only PLAN.md's dev-owned sections + `docs/changes/` + `docs/decisions/` + code. QA writes only `docs/qa/` + PLAN.md's QA-owned sections. **Neither edits the other's files.** When a fix is confirmed, the *QA* loop updates the finding doc's status (it owns `docs/qa/`); dev clears the Re-verify line.

## Handling mid-loop user input

- **Don't derail the current turn.** Finish the current step (or reach a safe stop per step 3's "won't finish" rule).
- **Note it, don't solve it inline** (unless genuinely trivial). Add it verbatim (or a faithful short summary) to PLAN.md's **Inbox** with the date.
- **Next turn picks it up first** — step 2 checks the Inbox before pulling from the Dev Queue.

## Blocking without stopping

The loop **never blocks on a question.** There is no "ask the user and wait" step — that would stall the whole workstream. When you hit something you genuinely can't decide yourself (a product-shape fork with no safe reversible default, conflicting priorities, a destructive/irreversible action, or an Inbox note that's truly ambiguous):

1. **Stash the in-flight work** if it isn't at a safe commit point: `git stash push -m "wip: <item>"` (or commit coherent progress first — prefer that). You must leave the tree in a state the next item can build on.
2. **Log it in PLAN.md's Paused Questions** as one bounded entry: the question, the context, and *which item it blocks*. Keep it short — it's a ticket, not an essay.
3. **Move on to another item** (per [step 2](#the-loop)'s ordering). Do not sit on the blocked item, do not guess, do not ask.
4. **A human clears Paused Questions** when convenient. When an entry is answered, clear it, fold the answer into the relevant slice/change doc, and the blocked item becomes selectable again (it was never removed from the Dev Queue — only parked behind the question).

This keeps the loop autonomous and always forward-moving: a question changes *what's parked*, never *whether the loop runs*.

## Keeping the docs lean

| Information | Home |
|---|---|
| Now / Active Slice / Dev Queue / Inbox / Re-verify / Recently Completed | `PLAN.md` (dev-owned sections) — bounded lists only |
| QA findings, one per defect, + pass log | `docs/qa/` (QA-owned; dev links, never inlines) |
| What was built per slice, key types, test counts | `docs/changes/NNN-slug.md` |
| Substantial design decisions | `docs/decisions/NNN-slug.md` |
| Multi-turn forward-looking scope | `docs/plans/slug.md` |
| Append-only record of closed phases | `docs/ROADMAP.md` |
| Architecture / projects / testing / coding rules | the `docs/reference/` docs |

**Rules:** bounded lists in PLAN.md (Dev Queue ~5–7, Recently Completed ~5); append-only ROADMAP; one file per change/decision; **when in doubt, link instead of copy.**

## Failure Modes to Avoid

| Failure mode | Guard |
|---|---|
| Half-finished feature, orphaned interfaces | Step 2: slices are vertically complete (impl + tests) |
| Previous turn's breakage buried under new work | Step 1: red state ends the turn; repair is its own commit |
| Infinite repair loop | Step 1: max 2 attempts, then `BLOCKED` note |
| "Done" meaning something different each turn | Step 3: explicit Definition of Done |
| Drift from the ActivityStreams interop rules | Step 3: re-read CODING_STYLE.md before coding |
| A 40-minute turn that hits a wall mid-migration | Step 3: bounded turn — commit progress, note `remaining:`, end |
| **QA tests a stale build and "finds" a fixed bug** | Step 3 deploy + Live state; the [redeploy-error rule](#when-qa-reports-an-error-or-you-see-one-that-may-be-redeploy-related) resolves the ambiguity first |
| Dev and QA both edit the same PLAN.md section | [Ownership map](#ownership-map): each owns distinct sections; neither edits the other's files |
| Stalling on undecided design questions | Step 3: decide, record in the change doc, continue |
| A mid-loop user request gets dropped or derails work | "Handling mid-loop user input": Inbox, finish current, action next turn |
| Loop stalls waiting on a user answer | [Blocking without stopping](#blocking-without-stopping): log in Paused Questions, stash, move to another item — never ask-and-wait |
| Agent guesses on something it shouldn't | Same — park it in Paused Questions rather than guessing; the loop keeps moving |
| PLAN.md growing back into a sprawling document | Step 5: prune every turn; detail → `changes/`/`decisions/`/`docs/qa/` |
