# Iris — Change Docs

One document per **change** (a slice, a gap closure, a notable fix). This directory replaces the former append-only `CHANGELOG.md` (retired) and holds the per-slice build notes that used to live there: what was built, key types, test counts, and any lightweight decision.

## Why one doc per change (not a single changelog)

- A single append-only changelog grows unbounded and is hard to navigate. One file per change keeps each change self-contained and easy to link to.
- A change doc is the natural place to link a [decision doc](../decisions/) (for the substantial ones) and to record the lightweight decisions inline.

## When to create a change doc

Create one for every vertical slice that lands a commit — a feature, a gap closure (F-xx), a bug fix, or a refactor that changes observable behavior. Research-only or docs-only turns that produce no code change do not need one (note them in the phase status instead).

## Conventions

- **Filename:** `NNN-slug.md`, where `NNN` is a monotonically increasing change number that continues the project-wide numbering shared with [decisions/](../decisions/) (e.g. `054-f12-replies-threading.md`). The number is the slice's identifier — it is referenced from PLAN.md's Recently Completed list and, once archived, from ROADMAP.md's ledger.
- **Template:**

  ```markdown
  # NNN — <Change title>

  > <date> · <slice id, e.g. "Slice 12.9"> · <phase>

  ## What was built

  Plain-language summary of the change and the user-visible / wire-level effect.

  ## Key types & files

  The new/changed types and the files that hold them.

  ## Tests

  Test count before → after, and the new test files/classes.

  ## Decisions

  Lightweight decisions recorded inline. For a substantial decision, a one-line
  summary + a link to the decision doc in [../decisions/](../decisions/).
  ```

- **Append-only:** once a change is recorded, don't rewrite it. If the change is later reverted, write a *new* change doc that supersedes it and link both.

## Full rules

The doc-maintenance rules that govern this directory (and PLAN/ROADMAP/decisions) are in [AUTONOMOUS_LOOP.md — Keeping the docs lean](../reference/AUTONOMOUS_LOOP.md#keeping-the-docs-lean).
