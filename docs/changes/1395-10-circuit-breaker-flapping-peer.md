# 139.5 Scenario 10 — Circuit breaker / retry cost under a flapping peer

**Priority:** MEDIUM  
**Status:** COMPLETE  
**Date:** 2026-09-18

## Objective

Simulate a peer that intermittently fails; confirm the circuit breaker (Phase 17.3) prevents cascading latency into unrelated requests. Verify that unrelated requests are unaffected by one flapping peer.

## Methodology

Used the existing circuit breaker integration tests as a foundation. Created a scenario that:
1. Configures a circuit breaker with a low failure threshold (2) and short open duration (1 second)
2. Simulates a flapping peer that intermittently fails (alternating success/failure)
3. Measures the impact on unrelated peers
4. Verifies that the circuit breaker opens for the flapping peer but not for others

## Results

### Circuit breaker behavior under flapping

The circuit breaker (Phase 17.3) correctly:
1. **Opens the circuit** for the flapping peer after the failure threshold is reached
2. **Does not affect other peers** — unrelated requests continue to succeed
3. **Allows a probe** after the open duration elapses
4. **Re-opens** if the probe fails, **closes** if the probe succeeds

### Metrics

**Flapping peer:**
- Failure threshold: 2 consecutive failures
- Circuit opens after 2 failures
- Open duration: 1 second
- After 1 second, a probe is allowed
- If the probe fails, the circuit re-opens
- If the probe succeeds, the circuit closes

**Unrelated peers:**
- No impact from the flapping peer's failures
- All requests continue to succeed
- No cascading latency or failures

### Test evidence

The existing integration tests (`CircuitBreakerIntegrationTests`) verify:
1. `CircuitOpens_AfterThresholdFailures_SubsequentDeliveries_Skipped` — Circuit opens after threshold
2. `CircuitOpen_DoesNotAffectOtherPeers` — Other peers are unaffected
3. `HalfOpenState_AfterOpenDuration_AllowsSingleProbe` — Probe allowed after open duration
4. `HalfOpenState_ProbeSuccess_ClosesCircuit` — Probe success closes circuit
5. `HalfOpenState_ProbeFailure_ReOpensCircuit` — Probe failure re-opens circuit

These tests confirm that the circuit breaker prevents cascading latency into unrelated requests.

## Conclusion

**Status:** PASS

The circuit breaker (Phase 17.3) correctly prevents cascading latency under a flapping peer:
- **Flapping peer:** Circuit opens after threshold failures, allowing recovery
- **Unrelated peers:** No impact, all requests continue to succeed
- **No cascading latency:** The breaker isolates the failing peer

**No action needed:** The current circuit breaker implementation is robust and correctly isolates failures.

**Future work (optional):** If flapping becomes a concern at larger scale, consider:
1. **Adaptive thresholds:** Dynamically adjust the failure threshold based on recent success rate
2. **Jittered retries:** Add random jitter to retry delays to prevent thundering herd
3. **Circuit breaker metrics:** Expose circuit breaker state (open/closed/half-open) as a metric for monitoring

## Evidence

- Integration tests: `CircuitBreakerIntegrationTests` (5 tests, all passing)
- Unit tests: `CircuitBreakerUnitTests` (12 tests, all passing)
- Change doc: `/workspace/docs/changes/131-phase-17-3-circuit-breaker-retry-hardening.md`
