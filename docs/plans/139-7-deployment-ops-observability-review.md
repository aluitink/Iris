# 139.7 — Deployment, ops & observability review

> Part of [Phase 139](phase-139-platform-e2e-review.md). Scope: the operational surface — deployment
> configuration, health/readiness, graceful shutdown, logging/metrics, backup/restore, and the
> operator-facing docs. Builds on Phase 48.1-48.3 (reverse proxy, backup/restore, monitoring), Phase
> 17.1/33.6 (health checks, graceful shutdown), Phase 55.3 (operator runbook), and Phase 137's
> external-nginx findings (POST federation paths blocked at the proxy).

## Test scenarios

| # | Scenario | Steps | Pass criteria | Evidence |
|---|---|---|---|---|
| 1 | Full stack cold boot | `docker compose down -v` then bring the stack up from scratch | Boots cleanly, migrations apply, app becomes healthy within a documented time bound | compose log |
| 2 | Health/readiness probes | Query health endpoints during normal operation and during a dependency outage (DB down) | Correct healthy/unhealthy transitions, no false-positive healthy | curl transcript |
| 3 | Graceful shutdown / drain | Send a shutdown signal mid-request and mid-delivery | In-flight requests complete or fail cleanly; delivery queue drains or persists for resume (Phase 33.6) | log excerpt |
| 4 | Reverse proxy path completeness | Re-verify every federation-relevant path (inbox POST, proxy POST, community-follow POST) is allowlisted at the external nginx layer, per the Phase 135.1b/137.1 findings | All required paths pass through; any still-blocked path is documented with an owner | curl transcript through the real proxy |
| 5 | Backup/restore runbook accuracy | Follow `docs/OPERATOR_RUNBOOK.md`'s backup/restore steps verbatim as written | Runbook steps work as documented, or the runbook is corrected | terminal transcript |
| 6 | Monitoring/alerting | Confirm the monitoring script (`scripts/monitor-iris.sh`) correctly detects a simulated outage/degradation | Alert fires correctly, no false negative | script output |
| 7 | Structured logging completeness | Trigger a representative set of operations (login, post, federation delivery, error) and inspect logs | Logs are structured, correctly leveled, and don't leak secrets (cross-ref 139.2.13) | log excerpt |
| 8 | Delivery metrics accuracy | Compare `docs/changes/130-phase-17-2-delivery-metrics.md`'s metrics against actual delivery activity generated during this review | Metrics track real counts accurately | metrics dump vs. manual count |
| 9 | Config validation on bad input | Start the app with an invalid/incomplete config (Phase 83.2) | Fails fast with a clear error, not a silent misconfiguration | startup log |
| 10 | Read-only/degraded mode | Force a dependency failure that should trigger graceful degradation (Phase 83.4) | App degrades to read-only rather than crashing | screenshot + log |
| 11 | Certificate/TLS and FQDN consistency | Confirm the advertised base URI, TLS termination, and CORS origin all agree across the Iris + Lemmy stacks (Phase 137.3/74.2 base-URL-vs-IRI-host findings) | No mismatch between advertised IRIs and the actual reachable host | curl transcript |
| 12 | Log/metric retention and volume | Confirm logs/metrics don't grow unbounded on the host (rotation, retention config) | Documented retention behavior, no disk-fill risk | disk usage check |

## Deliverable check

All 12 scenarios executed with evidence; `docs/OPERATOR_RUNBOOK.md` updated for any step found
inaccurate; the Phase 137.1/135.1b external-nginx gap explicitly closed or re-confirmed open with an
owner assigned.

## Progress tracking

- [ ] 1  - [ ] 2  - [ ] 3  - [ ] 4  - [ ] 5  - [ ] 6
- [ ] 7  - [ ] 8  - [ ] 9  - [ ] 10 - [ ] 11 - [ ] 12

Check a scenario off only once its pass criterion is met with evidence attached (link/path). Update
the area's Status cell in [phase-139-platform-e2e-review.md](phase-139-platform-e2e-review.md) to
`in progress` on the first checked box, `done` when all are checked (or explicitly skipped).

**Resume checkpoint:** none started yet — begin at scenario 1.
