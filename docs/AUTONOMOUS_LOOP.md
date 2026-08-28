# Iris — Autonomous Loop Instructions

> Part of the [Iris plan](../PLAN.md). These are the operating instructions for an autonomous agent loop working on this project, one turn at a time.

## Principles

- **One turn = one coherent, vertically complete slice of work** — implementation *and* its tests.
- **Red state ends the turn.** A broken build is never papered over with new work.
- **"Done" is defined, not felt.** The Definition of Done below is the only exit from step 3.
- **The docs are binding.** [CODING_STYLE.md](CODING_STYLE.md) wins over habit; [TESTING.md](TESTING.md) defines what coverage a phase needs before it's checkable.
- **Docs stay lean.** PLAN.md and ROADMAP.md are *indexes and waypoints* — they must not accumulate detail. Heavy information (build notes, test counts, design rationale) belongs in [CHANGELOG.md](CHANGELOG.md) or a design-decision document in [decisions/](decisions/). See [Keeping the docs lean](#keeping-the-docs-lean).

## The Loop
<instructions>
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
- **Open Questions:** if you hit one, make the decision, record it in [CHANGELOG.md](CHANGELOG.md) under "Resolved Decisions" (removing it from "Open Questions" in ROADMAP.md), and continue. Don't stall, don't choose silently.
- **If the slice won't finish this turn:** commit coherent progress, mark the item in-progress in ROADMAP.md with a `remaining:` note describing what's left, and end the turn.

### 4. Commit

- Commit implementation + tests **together** (conventional commit message, e.g. `feat(core): add Iri value type with inbox/outbox derivation`).
- A feature without its tests is a red flag for the next turn's step 1 — never split them.

### 5. Update the plan

- Check off completed boxes in ROADMAP.md; note any decisions recorded.
- Update PLAN.md's "Current Status" section if the phase changed.
- **Route the detail correctly** (see [Keeping the docs lean](#keeping-the-docs-lean)):
  - What was built, key types, test counts → append to the phase's slice log in [CHANGELOG.md](CHANGELOG.md).
  - Design decisions → append to "Resolved Decisions" in [CHANGELOG.md](CHANGELOG.md); if the decision is substantial (trade-offs, alternatives considered, spec references), write a full document in [decisions/](decisions/) and link it from the changelog entry.
  - ROADMAP.md gets only the checkbox tick and a one-line status; PLAN.md gets only the status-table row.
- Commit separately as `docs: ...`.

### 6. End this turn
</instructions>

## Keeping the docs lean

PLAN.md and ROADMAP.md are the documents an agent re-reads every turn, so they must stay small and stable. Detail that is written once and read rarely does not belong in them.

**Where each kind of information lives:**

| Information | Home |
|---|---|
| What the project is, doc index, conventions summary, current status table | `PLAN.md` (index only) |
| Phases, waypoints, checkboxes, open questions, carried-forward items | `ROADMAP.md` (brief bullets only) |
| What was built per slice, key types, test counts, build notes | `CHANGELOG.md` (append-only) |
| Resolved design decisions (numbered, one entry each) | `CHANGELOG.md` → "Resolved Decisions" |
| Substantial design decisions (trade-offs, alternatives, spec references) | `docs/decisions/NNN-slug.md` (one file per decision), linked from the changelog entry |
| Architecture, per-project detail, testing strategy, coding rules | `ARCHITECTURE.md`, `PROJECTS.md`, `TESTING.md`, `CODING_STYLE.md` |

**Rules:**

- **PLAN.md** is an index. It may contain the status table and a short "carried forward" list — nothing else grows over time. Never paste build notes, decision rationale, or slice detail into it.
- **ROADMAP.md** entries are waypoints: a phase gets a short description blockquote and checkbox bullets. A bullet is one line; if it needs a paragraph, the paragraph goes in CHANGELOG.md or a decision doc.
- **CHANGELOG.md** is append-only history. When a slice completes, its detail lands here — not in PLAN.md or ROADMAP.md.
- **Decision docs** (`docs/decisions/`): create one when a decision has real weight (multiple viable alternatives, spec implications, or it will be referenced repeatedly). Keep the changelog entry to a one-line summary + link. Lightweight decisions stay as a single changelog entry.
- **When in doubt, link instead of copy.** A pointer in PLAN/ROADMAP beats a duplicated paragraph.

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
| PLAN.md / ROADMAP.md growing wild with detail | "Keeping the docs lean": detail → CHANGELOG.md or `decisions/`; PLAN/ROADMAP stay index + waypoints |
