# 139 — Phase 19.0.5: Evaluation checklist scaffold

## What was built

Created `docs/reference/LIVE_EVALUATION_CHECKLIST.md` — the standing manual checklist the Playwright
sessions (and the operator) execute between turns. It is the operator's repeatable routine: run it
after any code change, after a `down`/`up` recreation, or before declaring a phase done.

### Structure

1. **Prerequisites** — compose stack up, FQDNs resolve, smoke test passes.
2. **Standing checklist** (every session): logon (4 checks), explore (5), switch instance (2),
   cross-instance write (3), moderate (3), external instance (3). Each row has a UI path / wire check
   and a pass criteria.
3. **Phase 19 waypoints** (19.1–19.8) — every waypoint mapped to a concrete UI path or wire check,
   with notes on what to verify and what a "stuck" state means (19.7 Threads probe).

### Key decisions

- The checklist is a **standalone doc** (not an extension of INTEROP_TEST_HARNESS §4a) because it
  serves a different audience (operator / Playwright agent) and a different cadence (every session,
  not just Phase 13 live interop). The harness doc remains the design for the opt-in C# test project.
- Each checkpoint is expressed as a **UI path or wire check** — not a code assertion — so it can be
  executed by a browser session without reading source.
- The "stuck state is a valid outcome" convention from 19.7.4 is carried into the checklist so the
  operator knows when to stop and record rather than block.

## Files changed

- `docs/reference/LIVE_EVALUATION_CHECKLIST.md` (new, ~220 lines)
- `docs/ROADMAP.md` (19.0.5 checkbox ticked)

## Test counts

No code changes; no new tests. Existing suite: 1183 passing.
