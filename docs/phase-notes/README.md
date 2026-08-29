# Phase Notes

This directory holds the heavy implementation detail that used to live inline in the changelog. The changelog now keeps only a pointer and the current phase status summary.

## Index

- [PHASE_10_TEST_REVIEW.md](PHASE_10_TEST_REVIEW.md) — test-audit pass, dead-code sweep, cache/page/perf consolidation, and harness cleanup.
- [PHASE_11_IMPLEMENTATION_GAPS.md](PHASE_11_IMPLEMENTATION_GAPS.md) — user-journey gaps, discovery/follow/post APIs, and the remaining write-path fixes.
- [PHASE_12_SPEC_CONFORMANCE.md](PHASE_12_SPEC_CONFORMANCE.md) — spec-conformance audit, gap closure, and conformance regressions.
- [TEST_COUNT_HISTORY.md](TEST_COUNT_HISTORY.md) — per-slice `dotnet test` totals for the main suite.

## Maintenance rule

When a phase or slice is complete, the changelog should keep a short status entry and a pointer to the detail here. The detailed rationale remains in the phase note, not in the roadmap or plan index.
