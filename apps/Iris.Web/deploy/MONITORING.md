# Iris — Monitoring & Alerting

## Endpoints

| Endpoint | Purpose | Auth |
|----------|---------|------|
| `GET /ap/v1/health` | Liveness: aggregate status of all registered health checks. 200 when healthy/degraded, 503 when unhealthy. | None |
| `GET /ap/v1/ready` | Readiness: whether the instance can accept traffic. 200 `{ "ready": true }` or 503. | None |
| `GET /local/v1/metrics` | Prometheus text-format: outbound-delivery counters (enqueued, delivered, failed, dead-lettered) + per-activity-type breakdown. | None (internal network only) |

### Health check body

```json
{
  "status": "healthy",
  "checks": {
    "InstanceHealthCheck": { "status": "healthy", "description": "Instance 'my-iris' configured with actor https://iris.luit.ink/ap/v1/u/iris" },
    "DeliveryQueueHealthCheck": { "status": "healthy", "description": "Queue pending: 0 (threshold: 1000)" },
    "PersistenceHealthCheck": { "status": "healthy", "description": "Persistence is reachable." },
    "DeliveryWorkerHealthCheck": { "status": "healthy", "description": "The delivery worker is running." }
  }
}
```

### Metrics body (Prometheus format)

```
# HELP iris_delivery_enqueued_total Total delivery jobs placed on the queue.
# TYPE iris_delivery_enqueued_total counter
iris_delivery_enqueued_total 1523

# HELP iris_delivery_delivered_total Total deliveries that completed with a 2xx response.
# TYPE iris_delivery_delivered_total counter
iris_delivery_delivered_total 1519

# HELP iris_delivery_attempt_failed_total Total single delivery attempts that failed.
# TYPE iris_delivery_attempt_failed_total counter
iris_delivery_attempt_failed_total 8

# HELP iris_delivery_dead_lettered_total Total jobs that exhausted their retry budget.
# TYPE iris_delivery_dead_lettered_total counter
iris_delivery_dead_lettered_total 2

# Per-activity-type breakdown
iris_delivery_by_type{activity_type="Create",direction="enqueued"} 800
iris_delivery_by_type{activity_type="Create",direction="delivered"} 798
...

# Per-failure-kind breakdown
iris_delivery_failure_kind{kind="NonSuccessStatus"} 6
iris_delivery_failure_kind{kind="TransportError"} 4
```

## Prometheus + Grafana (recommended)

### Prometheus scrape config

```yaml
# prometheus.yml (or a file in /etc/prometheus/rules/ via service discovery)
scrape_configs:
  - job_name: iris
    static_configs:
      - targets: ['iris.example.com:443']
    metrics_path: /local/v1/metrics
    scheme: https
    # If the metrics endpoint requires no auth (internal network), no credentials needed.
    # For a public instance, put the metrics endpoint behind a network ACL or
    # add basic auth via the reverse proxy.
```

### Alert rules

```yaml
# iris-alerts.yml
groups:
  - name: iris
    rules:
      # Instance down (health endpoint unreachable or unhealthy).
      - alert: IrisInstanceDown
        expr: up{job="iris"} == 0
        for: 2m
        labels:
          severity: critical
        annotations:
          summary: "Iris instance is down"
          description: "The Iris health endpoint has been unreachable for 2 minutes."

      # Dead-letter spike (delivery pipeline is failing).
      - alert: IrisDeadLetterSpike
        expr: increase(iris_delivery_dead_lettered_total[5m]) > 10
        for: 5m
        labels:
          severity: warning
        annotations:
          summary: "Iris dead-letter spike"
          description: "More than 10 deliveries dead-lettered in the last 5 minutes."

      # High delivery failure rate.
      - alert: IrisHighFailureRate
        expr: |
          (
            increase(iris_delivery_attempt_failed_total[10m])
            /
            (
              increase(iris_delivery_delivered_total[10m])
              +
              increase(iris_delivery_attempt_failed_total[10m])
            )
          ) > 0.5
        for: 10m
        labels:
          severity: warning
        annotations:
          summary: "Iris delivery failure rate > 50%"
          description: "More than half of delivery attempts are failing."

      # Queue backlog (deliveries piling up).
      - alert: IrisQueueBacklog
        expr: iris_delivery_enqueued_total - iris_delivery_delivered_total > 100
        for: 5m
        labels:
          severity: warning
        annotations:
          summary: "Iris delivery queue backlog"
          description: "More than 100 deliveries are pending in the queue."
```

### Grafana dashboard (JSON template)

Create a dashboard with these panels:
1. **Instance Status** — stat panel: `up{job="iris"}` (green/red).
2. **Delivery Rate** — time series: `rate(iris_delivery_delivered_total[5m])` vs `rate(iris_delivery_attempt_failed_total[5m])`.
3. **Dead Letters** — time series: `increase(iris_delivery_dead_lettered_total[5m])`.
4. **Queue Depth** — stat + time series: `iris_delivery_enqueued_total - iris_delivery_delivered_total`.
5. **Per-Activity-Type** — stacked bar: `iris_delivery_by_type` by `activity_type` and `direction`.

## Lightweight monitoring (no Prometheus)

`scripts/monitor-iris.sh` is a cron-able / loop-able script that:
- Polls `/ap/v1/health` every 60s.
- Detects transitions to unhealthy/unreachable and sends alerts (Slack webhook, email).
- Tracks delivery metrics and alerts on dead-letter spikes + high failure rate.

### Cron (every minute)

```cron
* * * * * root /opt/iris/scripts/monitor-iris.sh
```

### Continuous loop (systemd service)

```ini
# /etc/systemd/system/iris-monitor.service
[Unit]
Description=Iris production monitor
After=network.target

[Service]
Type=simple
WorkingDirectory=/opt/iris
ExecStart=/opt/iris/scripts/monitor-iris.sh --loop 60
Restart=always
RestartSec=10
Environment=IRIS_BASE_URL=http://localhost:8088
Environment=IRIS_ALERT_WEBHOOK=https://hooks.slack.com/services/YOUR/WEBHOOK
# Environment=IRIS_ALERT_EMAIL=admin@example.com

[Install]
WantedBy=multi-user.target
```

## Log-based alerting

The app uses structured logging (Phase 30.3). Key log patterns to alert on:

| Pattern | Severity | Meaning |
|---------|----------|---------|
| `DeliveryWorker` + `DeadLetter` | Warning | A delivery exhausted its retry budget. |
| `DeliveryWorker` + `TransportError` | Info | A single attempt failed (network/timeout). |
| `InboxHandler` + `SignatureValidationFailed` | Warning | An unsigned or invalidly-signed inbound activity was rejected. |
| `Exception` (unhandled) | Error | An unhandled exception in a request handler. |

With filebeat/fluentd + ELK, alert on:
- `> 5` `DeadLetter` entries in a 5-minute window.
- Any `Exception` (unhandled) entry.
- `> 20` `SignatureValidationFailed` in a 1-minute window (possible attack).

## Uptime monitoring (external)

For a public instance, add an external uptime check (Better Stack, UptimeRobot, etc.):
- **URL:** `https://iris.example.com/ap/v1/health`
- **Interval:** 60s
- **Timeout:** 5s
- **Alert:** 2 consecutive failures

This catches cases where the host's internal monitoring is also down.

## RPO / RTO (monitoring)

- **Detection time:** 2 minutes (health check interval 60s × 2 consecutive failures).
- **Alert delivery:** < 30 seconds (webhook/email).
- **Total MTTA (Mean Time To Acknowledge):** < 5 minutes (target).
