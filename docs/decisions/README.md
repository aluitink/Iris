# Iris — Design Decisions

One document per **substantial** design decision. This directory exists to keep [PLAN.md](../../PLAN.md) and [ROADMAP.md](../ROADMAP.md) lean: heavy rationale lives here, and the per-change doc in [changes/](../changes/) links to it.

## When to create a decision doc

Create one when a decision has real weight:

- Multiple viable alternatives were considered, or
- It has spec/interop implications (ActivityPub, ActivityStreams, RFC 8410, HTTP signatures), or
- It will be referenced repeatedly by later phases.

Lightweight decisions (a naming choice, a default value) stay inline in the slice's [change doc](../changes/) — no decision doc needed.

## Conventions

- **Filename:** `NNN-slug.md`, where `NNN` is a monotonically increasing decision number (e.g. decision #39 → `039-bearer-auth-upgrade.md`).
- **Link from the change doc:** the change doc's entry for the decision is a one-line summary + a link to this document. The doc holds the detail.
- **Template:**

  ```markdown
  # NNN — <Decision title>

  > Resolved <date>. See the change doc that introduced it.

  ## Context

  What problem or question forced the decision.

  ## Decision

  What was decided, stated plainly.

  ## Alternatives considered

  What else was on the table, and why it was rejected.

  ## Consequences

  What this enables, what it costs, what it constrains for later phases.
  ```

- **Append-only:** once a decision is recorded, don't rewrite it. If it is later reversed, write a *new* decision doc that supersedes it and link both.

## Full rules

The doc-maintenance rules that govern this directory (and PLAN/ROADMAP/changes) are in [AUTONOMOUS_LOOP.md — Keeping the docs lean](../reference/AUTONOMOUS_LOOP.md#keeping-the-docs-lean).
