# Iris — Autonomous Loop Instructions

> Part of the [Iris plan](../../PLAN.md). These are the operating instructions for an autonomous agent loop working on this project, one turn at a time.

## Principles

- **One turn = one coherent, vertically complete slice of work** — implementation *and* its tests.
- **Red state ends the turn.** A broken build is never papered over with new work.
- **"Done" is defined, not felt.** The Definition of Done below is the only exit from step 3.
- **The docs are binding.** [CODING_STYLE.md](CODING_STYLE.md) wins over habit; [TESTING.md](TESTING.md) defines what coverage a phase needs before it's checkable.
- **[PLAN.md](../../PLAN.md) is the single live document.** It is the only file this loop must read and update every turn — *Now / Active Slice / Up Next / Inbox / Paused Questions / Recently Completed*. Everything else in `docs/` is either a rarely-touched reference or a write-once archive ([ROADMAP.md](../ROADMAP.md) is append-only; [changes/](../changes/), [decisions/](../decisions/), [phase-notes/](../phase-notes/) are written on completion and read rarely). See [Keeping the docs lean](#keeping-the-docs-lean).
- **The loop is self-maintaining.** PLAN.md's bounded lists (capped "Up Next", capped "Recently Completed") are pruned every turn as part of step 5, not left to balloon until a human intervenes.

## The Loop
<instructions>
### 1. Confirm good state

- Run `dotnet build` and `dotnet test` (a SubAgent may run and summarize the results; fixes happen in the main loop).
- **If failures:** fix *only* the breakage, re-run until green (max 2 repair attempts), commit as `fix: repair broken state from previous turn`, then **end this turn**. No new work.
- **If still failing after 2 attempts:** write a `BLOCKED` note in [PLAN.md](../../PLAN.md)'s Active Slice section describing the failure, commit, and end this turn.

### 2. Select the next work item

- **Check PLAN.md's Inbox first.** If it has an unactioned entry and no slice is already in progress, action the oldest entry before pulling anything else. (If a slice *is* already in progress, finish it first — the Inbox entry waits one more turn. It doesn't get lost; it stays in the Inbox until actioned.)
- Otherwise, take the top item from PLAN.md's **Up Next**.
- **If Up Next has fewer than ~3 items left,** replenish it in this step: pull the next slice(s) from the relevant [docs/plans/](../plans/) deep-dive doc, or expand the next phase from [ROADMAP.md](../ROADMAP.md) into concrete slices. Do this *before* selecting, not as an afterthought.
- A slice must be **vertically complete**: implementation + its tests. Coverage expectations are part of the item, not a follow-up.
- **If every phase in ROADMAP.md and every plan doc is exhausted:** define the next phase (it is explicitly expected that later phases start as a one-line placeholder), add it to ROADMAP.md, seed PLAN.md's Up Next with its first slices, commit, and end this turn.

### 3. Work on the item

- **Before coding**, re-read [CODING_STYLE.md](CODING_STYLE.md) — especially the [3rd-Party ActivityStreams rules](CODING_STYLE.md#3rd-party-activitystreams-types):
  - Deserialize into `IObjectOrLink` (or `IObject`/`ILink`), then cast — never into a concrete type.
  - Construct with object initializers; let the constructor set `Type`.
  - Collection expressions for multi-valued properties; expect `IEnumerable<T>?` when reading.
  - `Id` is `string?` in the library — convert to `Iri` at the Iris boundary.
- **Definition of done** (all must hold before committing):
  - `dotnet build` clean — `TreatWarningsAsErrors` is on, so a warning is a failure.
  - `dotnet test` green, **including new tests for this item** (integration-first per [TESTING.md](TESTING.md)).
  - XML doc comments on all public API; `CancellationToken ct` is the last parameter; file-scoped namespaces.
  - No dependency-direction violations (`Iris.Core` never references `Iris.Client`/`Iris.Server`; no upward dependencies).
  - No new NuGet packages without a note in PLAN.md's Active Slice (or the change doc) and a justification.
- **Open Questions (autonomous default):** if you hit a design fork you can reasonably decide yourself, make the call, record it in the slice's change doc, and continue. Don't stall, don't leave it undecided. This is the default path — most questions get decided, not asked.
- **Genuine blockers (rare — pause instead of guessing):** see [Pausing the loop with a question](#pausing-the-loop-with-a-question) below.
- **Mid-turn user injections:** see [Handling mid-loop user input](#handling-mid-loop-user-input) below.
- **If the slice won't finish this turn:** commit coherent progress, update PLAN.md's Active Slice with a `remaining:` note describing what's left, and end the turn.

### 4. Commit

- Commit implementation + tests **together** (conventional commit message, e.g. `feat(core): add Iri value type with inbox/outbox derivation`).
- A feature without its tests is a red flag for the next turn's step 1 — never split them.

### 5. Update PLAN.md and prune

- Move the finished item out of Active Slice into **Recently Completed** with a one-line summary + link to its change doc.
- **Prune as you go:** if Recently Completed now has more than ~5 entries, move the oldest one's one-liner into [ROADMAP.md](../ROADMAP.md)'s ledger (append a line, don't rewrite existing ones) and remove it from PLAN.md. This is what keeps PLAN.md from growing back into a sprawling document — do it every turn it applies, not occasionally.
- Remove the actioned entry from Inbox if this turn worked an injected request.
- Write the detail doc:
  - What was built, key types, test counts, and any lightweight decision → a **per-change doc** in [changes/](../changes/) (one file per slice/change).
  - A substantial decision (trade-offs, alternatives, spec references) → a full document in [decisions/](../decisions/), linked from the change doc.
  - PLAN.md gets only the one-line pointer; the detail lives in the linked doc.
- Commit the doc updates with the `docs: ...` commit (separate from the implementation commit).

### 6. End this turn
</instructions>

## Handling mid-loop user input

Occasionally a real user message arrives mid-workstream instead of the standard reprompt — a new request, a correction, a priority change.

- **Don't derail the current turn.** Finish whatever step you're already in (or at least reach a safe stopping point per step 3's "won't finish this turn" rule).
- **Note it, don't solve it inline**, unless it's genuinely trivial (a quick clarifying answer with no code impact). Add it verbatim (or a faithful short summary) to PLAN.md's **Inbox** with the date.
- **Next turn picks it up first** — step 2 checks the Inbox before pulling from Up Next.
- This keeps the loop predictable: an injection changes *what's next*, not *what's happening right now*.

## Pausing the loop with a question

The loop is designed to run without questions by default — see "Open Questions (autonomous default)" above. Pausing is the exception, reserved for cases where guessing wrong would be costly or irreversible, or where the user's own Inbox note is genuinely ambiguous (not just under-specified).

When that happens:

1. Record the question and its context in PLAN.md's **Paused Questions** section, so the state survives even if the answer takes a while.
2. Ask the question directly as the final action of the turn (using the question-asking tool, not just narration). This is the mechanism that actually suspends the loop — it waits for a real reply instead of the mechanical reprompt.
3. Do not invent an answer or push past the blocked item in the same turn.
4. Once answered, clear the entry from Paused Questions, fold the answer into the relevant slice or change doc, and resume normally.

Reserve this for genuine forks — product-shape calls with no safe reversible default, conflicting priorities between an Inbox item and in-flight work, or destructive/irreversible actions. It should be rare; most open questions are still decided autonomously per step 3.

## Failure Modes to Avoid

| Failure mode | Guard |
|---|---|
| Half-finished feature, orphaned interfaces | Step 2: slices are vertically complete (impl + tests) |
| Previous turn's breakage silently repaired, then buried under new work | Step 1: red state ends the turn; repair is its own commit |
| Infinite repair loop | Step 1: max 2 attempts, then `BLOCKED` note |
| "Done" meaning something different each turn | Step 3: explicit Definition of Done |
| Drift from the ActivityStreams interop rules | Step 3: re-read CODING_STYLE.md before coding |
| 40-minute turn that hits a wall mid-migration | Step 3: bounded turn — commit progress, note `remaining:`, end |
| PLAN.md growing back into a sprawling document | Step 5: prune Recently Completed to ~5 entries every turn, archive the rest to ROADMAP.md's ledger |
| Stalling on undecided design questions | Step 3: decide, record in the slice's change doc, continue |
| A mid-loop user request gets dropped or derails current work | "Handling mid-loop user input": note in Inbox, finish current work, action next turn |
| Agent guesses on something that really needed the user's call | "Pausing the loop with a question": ask via the question tool, log in Paused Questions |
| Docs growing wild with detail | "Keeping the docs lean": detail → `changes/` or `decisions/`; PLAN.md stays bounded lists only |

## Keeping the docs lean

[PLAN.md](../../PLAN.md) is the document an agent re-reads and updates every turn, so it must stay small and bounded. Everything else is either reference material (rarely changes) or an archive (written once, read rarely).

**Where each kind of information lives:**

| Information | Home |
|---|---|
| What the project is, doc index, conventions, current Now/Active Slice/Up Next/Inbox/Paused Questions/Recently Completed | `PLAN.md` — the single live document |
| Append-only record of fully closed phases | `docs/ROADMAP.md` (write when a phase closes; otherwise don't touch) |
| What was built per slice, key types, test counts, lightweight decisions | `docs/changes/NNN-slug.md` (one file per change) |
| Substantial design decisions (trade-offs, alternatives, spec references) | `docs/decisions/NNN-slug.md` (one file per decision), linked from the change doc |
| Multi-turn forward-looking scope for a workstream (e.g. a phase closeout) | `docs/plans/slug.md`, linked as one-line bullets from PLAN.md's Up Next |
| Architecture, per-project detail, testing strategy, coding rules | `ARCHITECTURE.md`, `PROJECTS.md`, `TESTING.md`, `CODING_STYLE.md` |

**Rules:**

- **PLAN.md** holds only bounded lists: a short Now/Active Slice, an Up Next capped around 5-7 items, an Inbox, Paused Questions, and Recently Completed capped around 5 entries. Nothing here grows unbounded — step 5 prunes it every turn.
- **ROADMAP.md** is append-only and low-churn. Add a line when a phase closes; never rewrite or expand it into active tracking — that's PLAN.md's job.
- **Change docs** (`docs/changes/`): one file per slice/change. Build notes, key types, test counts, and lightweight decisions land here — not in PLAN.md or ROADMAP.md.
- **Decision docs** (`docs/decisions/`): create one when a decision has real weight (multiple viable alternatives, spec implications, or it will be referenced repeatedly). The change doc keeps a one-line summary + link; the decision doc holds the detail.
- **Plan docs** (`docs/plans/`): create one for a workstream that will span multiple turns and needs scope beyond a one-line bullet (e.g. a phase closeout, a component deep-dive). PLAN.md's Up Next links to it instead of inlining its content.
- **When in doubt, link instead of copy.** A pointer in PLAN.md beats a duplicated paragraph.


## Failure Modes to Avoid

| Failure mode | Guard |
|---|---|
| Half-finished feature, orphaned interfaces | Step 2: slices are vertically complete (impl + tests) |
| Previous turn's breakage silently repaired, then buried under new work | Step 1: red state ends the turn; repair is its own commit |
| Infinite repair loop | Step 1: max 2 attempts, then `BLOCKED` note |
| "Done" meaning something different each turn | Step 3: explicit Definition of Done |
| Drift from the ActivityStreams interop rules | Step 3: re-read CODING_STYLE.md before coding |
| 40-minute turn that hits a wall mid-migration | Step 3: bounded turn — commit progress, note `remaining:`, end |
| PLAN.md history lost to a wholesale rewrite | Step 2: expand ROADMAP.md, update PLAN.md status only |
| Stalling on undecided design questions | Step 3: decide, record in the slice's change doc, continue |
| PLAN.md / ROADMAP.md growing wild with detail | "Keeping the docs lean": detail → `changes/` or `decisions/`; PLAN/ROADMAP stay index + waypoints |
