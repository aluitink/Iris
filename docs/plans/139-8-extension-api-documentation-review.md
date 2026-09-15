# 139.8 — Extension/API surface & documentation review

> Part of [Phase 139](phase-139-platform-e2e-review.md). Scope: re-run the spirit of Phase 22.7's
> extension-API-surface conformance audit against everything added since (Phases 23–138, including
> all the new `iris:` terms Phase 138.24/138.25 will introduce for Lemmy metadata), and verify the
> operator/developer-facing documentation (`docs/reference/`, `docs/OPERATOR_RUNBOOK.md`,
> `CHANGELOG.md`, the Phase 138.28 interop matrix) is accurate and complete.

## Test scenarios

| # | Scenario | Steps | Pass criteria | Evidence |
|---|---|---|---|---|
| 1 | Full `iris:` term inventory | Enumerate every constant in `IrisExtensionTerms` and confirm each still has an accurate XML-doc description matching current behavior | No stale/inaccurate term docs | code review notes |
| 2 | Term classification audit | Classify every non-core-AP wire property as core AP / ecosystem convention (e.g. `likes`/`shares`) / Iris extension, per the Phase 22.7 model | Every property classified, none ambiguous | classification table |
| 3 | `@context`/namespace document accuracy | Fetch the live namespace document (`{BaseUri}/ns`, Phase 31.8) and confirm every advertised term matches `IrisExtensionTerms` | No drift between advertised and actual terms | curl transcript |
| 4 | New Lemmy-metadata terms conformance (post-138) | Confirm the Phase 138.24/138.25 terms (locked/featured/nsfw/etc.) follow the same authorization/lifecycle rigor as existing terms | Consistent with the established pattern | code review notes |
| 5 | API reference accuracy | Cross-check `docs/changes/356-49.3-api-documentation.md` and any operator API reference against the actual current endpoint set | No missing/renamed/removed endpoints undocumented | diff table |
| 6 | Operator runbook accuracy | Follow `docs/OPERATOR_RUNBOOK.md` end to end for a first-time-operator scenario (not just backup/restore, covered in 139.7) | Runbook is sufficient and correct standalone | terminal transcript |
| 7 | CHANGELOG completeness | Confirm `CHANGELOG.md` reflects all user-visible changes through the current phase | No unlogged user-visible change | diff against recent change docs |
| 8 | Interop conformance matrix currency | Confirm the Phase 138.28 matrix (or its living home in `docs/reference/`) reflects the findings from 139.1 | Matrix updated, not stale | diff |
| 9 | `docs/reference/` cross-consistency | Read `ARCHITECTURE.md`, `PROJECTS.md`, `TESTING.md`, `CODING_STYLE.md` end to end; confirm none contradict current code (e.g. project layout, testing philosophy) | No contradictions found, or each is filed as a doc-fix finding | review notes |
| 10 | Sample apps documentation | Confirm `samples/` READMEs (Phase 70-72, 82.2) still match the current sample behavior | Accurate, runnable as documented | terminal transcript |

## Deliverable check

All 10 scenarios executed; every doc-accuracy finding results in an actual doc fix (not just a
logged finding) since these are typically low-cost, low-risk corrections — this area doc is one of
the few in Phase 139 where "found it" and "fixed it" should usually happen in the same pass.

## Progress tracking

- [ ] 1  - [ ] 2  - [ ] 3  - [ ] 4  - [ ] 5
- [ ] 6  - [ ] 7  - [ ] 8  - [ ] 9  - [ ] 10

Check a scenario off only once its pass criterion is met with evidence attached (link/path). Update
the area's Status cell in [phase-139-platform-e2e-review.md](phase-139-platform-e2e-review.md) to
`in progress` on the first checked box, `done` when all are checked (or explicitly skipped).

**Resume checkpoint:** none started yet — begin at scenario 1.
