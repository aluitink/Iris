# Phase 139 — Whole-platform end-to-end review

> Referenced from [PLAN.md](../../PLAN.md)'s "Up Next". This is the index for Phase 139 — a
> full, cross-cutting review of every aspect of Iris (federation, security, data lifecycle, UI/UX,
> performance, moderation, deployment/ops, and the extension/API surface), each with a detailed
> test-scenario catalog. Like [Phase 138](phase-138-lemmy-community-integration.md), the phase is
> too broad for one document — each area below has its own deep-dive doc; this index tracks
> phase-level status and the cross-cutting rules every area's scenarios follow. Update slice status
> in the area doc, roll the summary up here and into PLAN.md / [docs/ROADMAP.md](../ROADMAP.md) as
> areas close.

## Why this phase, and what "done" looks like

Iris has grown through ~138 phases of mostly forward slices — new features, targeted bug fixes, and
narrow live-verification passes (each phase's own change docs). What hasn't happened in one sweep is
a **single, deliberate pass across the whole platform** that treats every subsystem as a test
surface: not "does the new feature work" but "does everything still cohere, end to end, under
realistic and adversarial scenarios." Phase 138 supplies fresh real-world federation surface (a live
non-Iris peer under sustained two-way load) that earlier reviews (22, 29, 45, 54, 57, 60, 61, 79.3,
115.x, 136.x) didn't have simultaneously with everything else — this phase is the point to fold that
in and re-verify the whole platform holds together.

End state for this phase:

1. Every major subsystem has a **written test-scenario catalog** (not just "click around") that can
   be re-run by a future agent without re-deriving the scenarios from scratch.
2. Each scenario has been **executed at least once** against the live stack (Docker + Playwright per
   the Loop protocol, or the appropriate test tool for non-UI areas) with recorded evidence.
3. Every finding is **triaged** using the existing class/severity model (blocker/bug/UX/perf ×
   S1/S2/S3, per PLAN.md's Loop protocol step 5) and routed to a fix slice, not left implicit.
4. A **living reference artifact** exists per area (where one doesn't already exist — e.g. the
   Phase 138.28 interop conformance matrix, the page-coverage table, an OWASP checklist) so the next
   review doesn't start from zero.
5. No area is skipped for being "boring" — deployment/ops and the extension/API surface get the same
   scenario-catalog rigor as the user-facing UI.

## Cross-cutting rules (apply to every area doc)

- **Every scenario states:** actor/precondition, steps, expected result, and the evidence to capture
  (a screenshot path, a test name + pass/fail, a curl transcript, a log excerpt). No scenario is
  "done" without evidence attached.
- **Findings use the Loop protocol's triage model:** class (blocker/bug/UX/perf) + severity
  (S1/S2/S3) on every row, logged in the area's own tracker section, routed to a fix slice.
- **Fixes get re-verified from a clean entry** (fresh browser/container state) before being marked
  `fixed` — no exceptions, per the existing Loop protocol.
- **No new coded tests for UI-only findings** (WASM manual-test policy, Phase 45+); non-UI areas
  (server, client, core) may add coded tests where a scenario is naturally a unit/integration test.
- **Reuse, don't duplicate:** where a phase already produced a definitive artifact (e.g. Phase 57.2's
  WCAG audit, Phase 136's cross-instance integration suite, Phase 138's interop matrix), the area doc
  links to it and scopes its own scenarios to what's *not* already covered, plus a spot-check that the
  prior finding still holds.

## Areas

| # | Area | Doc | Status |
|---|---|---|---|
| 139.1 | Federation & interop conformance | [139-1-federation-interop-review.md](139-1-federation-interop-review.md) | done (all 12 scenarios; F-3 + F-5 + F-7 fixed; F-4/F-6/F-8 documented) |
| 139.2 | Security & trust boundary | [139-2-security-trust-boundary-review.md](139-2-security-trust-boundary-review.md) | not started |
| 139.3 | Data lifecycle & persistence | [139-3-data-lifecycle-persistence-review.md](139-3-data-lifecycle-persistence-review.md) | not started |
| 139.4 | UI/UX & accessibility | [139-4-ui-ux-accessibility-review.md](139-4-ui-ux-accessibility-review.md) | not started |
| 139.5 | Performance & scalability | [139-5-performance-scalability-review.md](139-5-performance-scalability-review.md) | not started |
| 139.6 | Moderation & governance | [139-6-moderation-governance-review.md](139-6-moderation-governance-review.md) | not started |
| 139.7 | Deployment, ops & observability | [139-7-deployment-ops-observability-review.md](139-7-deployment-ops-observability-review.md) | not started |
| 139.8 | Extension/API surface & documentation | [139-8-extension-api-documentation-review.md](139-8-extension-api-documentation-review.md) | not started |
| 139.9 | Closeout & synthesis | *(this doc, below)* | not started |

Suggested order: 139.1 and 139.2 first (federation + security are the highest-blast-radius areas and
the most likely to surface a blocker that reshapes later slices), then 139.3–139.8 in any order —
they're largely independent. 139.4 (UI/UX) should run after 139.1 lands any federation-surfaced UI
changes (e.g. Phase 138's Lemmy metadata badges), so the UI pass reviews the final surface, not a
moving target.

## Progress tracking

The **Status** column above is the phase-level tracker: `not started` → `in progress` → `done` (or
`skipped(<reason>)`). Update it here whenever an area's status changes — this table is the one
thing to check to know where Phase 139 stands overall.

Per-scenario progress lives inside each area doc's own **Progress tracking** section (a checkbox per
scenario number + a resume checkpoint line), not here — this index only tracks whole areas, not
individual scenarios, to keep it scannable.

## 139.9 — Closeout

Mark 139.9 in the Areas table above `done` only once every area's own Progress-tracking checklist is
fully checked (or explicitly `skipped(<reason>)`).

- Roll up every area's findings into one synthesis: cross-area patterns (e.g. the same gap showing up
  in both the security and data-lifecycle passes), a prioritized fix backlog, and an updated set of
  living reference docs.
- Full regression pass: `dotnet test` (fast, then full) green.
- Update PLAN.md's Recently Completed and add one [docs/ROADMAP.md](../ROADMAP.md) line per closed
  area (or one line for the whole phase if the areas closed together).
- Decide what Phase 140 is, based on what this review actually found — do not pre-commit to a next
  phase before the review is done.
