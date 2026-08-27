# Iris — Autonomous Loop Instructions

> Part of the [Iris plan](../PLAN.md). These are the operating instructions for an autonomous agent loop working on this project, one turn at a time.

## Principles

- **One turn = one coherent, vertically complete slice of work** — implementation *and* its tests.
- **Red state ends the turn.** A broken build is never papered over with new work.
- **"Done" is defined, not felt.** The Definition of Done below is the only exit from step 3.
- **The docs are binding.** [CODING_STYLE.md](CODING_STYLE.md) wins over habit; [TESTING.md](TESTING.md) defines what coverage a phase needs before it's checkable.

## The Loop

### 1. Confirm good state

- Run `dotnet build` and `dotnet test` (a SubAgent may run and summarize the results; fixes happen in the main loop).
- **If failures:** fix *only* the breakage, re-run until green (max 2 repair attempts), commit as `fix: repair broken state from previous turn`, then **end this turn**. No new work.
- **If still failing after 2 attempts:** write a `BLOCKED` note in [PLAN.md](../PLAN.md) describing the failure, commit, and end this turn.

### 2. Select the next work item

- Select the next **phase** (or a coherent slice of it) from [ROADMAP.md](ROADMAP.md).
- A slice must be **vertically complete**: implementation + its tests. The phase's "Integration tests" / "Unit tests" bullet is *part of the item*, not a follow-up.
- **If all phases are complete:** expand Phase 9+ into concrete phases in ROADMAP.md (it is explicitly "to be expanded later"), update PLAN.md's status section, commit, and end this turn. Do **not** rewrite PLAN.md wholesale.

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
  - No new NuGet packages without a note in ROADMAP.md and a justification.
- **Open Questions:** if you hit one, make the decision, record it under "Resolved Decisions" in ROADMAP.md (removing it from "Open Questions"), and continue. Don't stall, don't choose silently.
- **If the slice won't finish this turn:** commit coherent progress, mark the item in-progress in ROADMAP.md with a `remaining:` note describing what's left, and end the turn.

### 4. Commit

- Commit implementation + tests **together** (conventional commit message, e.g. `feat(core): add Iri value type with inbox/outbox derivation`).
- A feature without its tests is a red flag for the next turn's step 1 — never split them.

### 5. Update the plan

- Check off completed boxes in ROADMAP.md; note any decisions recorded.
- Update PLAN.md's "Current Status" section if the phase changed.
- Commit separately as `docs: ...`.

### 6. End this turn

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
| Stalling on undecided design questions | Step 3: decide, record under Resolved Decisions, continue |
