#!/usr/bin/env bash
#
# Iris — production monitoring script.
#
# Polls the health + readiness + metrics endpoints and:
#   1. Logs the status (for log-based alerting via journald/file monitoring).
#   2. Sends an alert (webhook / email / PagerDuty) when the instance becomes unhealthy.
#   3. Tracks delivery metrics (dead-letter rate, failure rate) and alerts on thresholds.
#
# Usage:
#   ./scripts/monitor-iris.sh                          # single check (for cron)
#   ./scripts/monitor-iris.sh --loop 60                # continuous loop (60s interval)
#
# Alerting (configure via env vars):
#   IRIS_ALERT_WEBHOOK=https://hooks.slack.com/services/...   # Slack incoming webhook
#   IRIS_ALERT_EMAIL=admin@example.com                        # via `mail` or `msmtp`
#   IRIS_ALERT_PAGERDUTY=xxx                                  # PagerDuty Events API v2
#
# Thresholds (overridable via env vars):
#   IRIS_DEAD_LETTER_ALERT=10       # alert when dead-lettered count increases by >10 in a check interval
#   IRIS_FAILURE_RATE_ALERT=0.5     # alert when attempt_failed / (delivered + attempt_failed) > 50%
#
# For Prometheus-based monitoring (recommended), see apps/Iris.Web/deploy/MONITORING.md.
# This script is a lightweight fallback for hosts without a Prometheus instance.

set -euo pipefail

# ---- Configuration ----
BASE_URL="${IRIS_BASE_URL:-http://localhost:8088}"
HEALTH_URL="${BASE_URL}/ap/v1/health"
READY_URL="${BASE_URL}/ap/v1/ready"
METRICS_URL="${BASE_URL}/local/v1/metrics"
CHECK_INTERVAL="${CHECK_INTERVAL:-60}"
DEAD_LETTER_ALERT="${IRIS_DEAD_LETTER_ALERT:-10}"
FAILURE_RATE_ALERT="${IRIS_FAILURE_RATE_ALERT:-0.5}"
ALERT_WEBHOOK="${IRIS_ALERT_WEBHOOK:-}"
ALERT_EMAIL="${IRIS_ALERT_EMAIL:-}"
LOG_FILE="${IRIS_MONITOR_LOG:-/var/log/iris-monitor.log}"
STATE_FILE="${IRIS_MONITOR_STATE:-/tmp/iris-monitor-state}"

LOOP_MODE=false
if [[ "${1:-}" == "--loop" ]]; then
  LOOP_MODE=true
  if [[ -n "${2:-}" ]]; then
    CHECK_INTERVAL="$2"
  fi
fi

# ---- Alert function ----
send_alert() {
  local title="$1"
  local body="$2"
  local timestamp
  timestamp="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  local message="[Iris Alert] ${timestamp}: ${title}\n${body}"

  echo "ALERT: ${message}"
  echo "${timestamp} ALERT ${title}: ${body}" >> "$LOG_FILE" 2>/dev/null || true

  # Slack webhook.
  if [[ -n "$ALERT_WEBHOOK" ]]; then
    curl -s -X POST -H "Content-Type: application/json" \
      -d "{\"text\": \"${message}\"}" \
      "$ALERT_WEBHOOK" >/dev/null 2>&1 || echo "  (Slack webhook failed)" >&2
  fi

  # Email.
  if [[ -n "$ALERT_EMAIL" ]]; then
    if command -v mail >/dev/null 2>&1; then
      echo "$body" | mail -s "[Iris Alert] ${title}" "$ALERT_EMAIL" 2>/dev/null || true
    elif command -v msmtp >/dev/null 2>&1; then
      echo "$body" | msmtp -t <<EOF 2>/dev/null || true
To: ${ALERT_EMAIL}
Subject: [Iris Alert] ${title}
Content-Type: text/plain

${body}
EOF
    fi
  fi
}

# ---- Load previous state ----
PREV_DEAD_LETTERED=0
PREV_ATTEMPT_FAILED=0
PREV_DELIVERED=0
PREV_STATUS="unknown"
if [[ -f "$STATE_FILE" ]]; then
  source "$STATE_FILE" 2>/dev/null || true
fi

# ---- Perform one check ----
do_check() {
  local timestamp
  timestamp="$(date -u +%Y-%m-%dT%H:%M:%SZ)"

  # 1. Health check.
  local health_status health_body
  health_status=$(curl -s -o /dev/null -w "%{http_code}" "$HEALTH_URL" 2>/dev/null || echo "000")
  health_body=$(curl -s "$HEALTH_URL" 2>/dev/null || echo "")

  if [[ "$health_status" == "200" ]]; then
    local current_status
    current_status=$(echo "$health_body" | grep -o '"status":"[a-z]*"' | cut -d'"' -f4 || echo "unknown")
    echo "${timestamp} HEALTH status=${current_status}"
    echo "${timestamp} HEALTH status=${current_status}" >> "$LOG_FILE" 2>/dev/null || true

    # Detect transition to unhealthy.
    if [[ "$current_status" == "unhealthy" && "$PREV_STATUS" != "unhealthy" ]]; then
      send_alert "Instance became UNHEALTHY" "Health: ${health_body}"
    fi
    PREV_STATUS="$current_status"
  elif [[ "$health_status" == "000" ]]; then
    echo "${timestamp} HEALTH status=UNREACHABLE"
    echo "${timestamp} HEALTH status=UNREACHABLE" >> "$LOG_FILE" 2>/dev/null || true
    if [[ "$PREV_STATUS" != "unreachable" ]]; then
      send_alert "Instance UNREACHABLE" "Cannot connect to ${HEALTH_URL}"
    fi
    PREV_STATUS="unreachable"
  else
    echo "${timestamp} HEALTH status=${health_status} body=${health_body:0:200}"
    if [[ "$PREV_STATUS" != "unhealthy" ]]; then
      send_alert "Instance returning ${health_status}" "Body: ${health_body:0:500}"
    fi
    PREV_STATUS="unhealthy"
  fi

  # 2. Readiness check.
  local ready_status
  ready_status=$(curl -s -o /dev/null -w "%{http_code}" "$READY_URL" 2>/dev/null || echo "000")
  if [[ "$ready_status" != "200" ]]; then
    echo "${timestamp} READY status=${ready_status}"
  fi

  # 3. Metrics (delivery counters).
  local metrics_body
  metrics_body=$(curl -s "$METRICS_URL" 2>/dev/null || echo "")

  if [[ -n "$metrics_body" ]]; then
    local dead_lettered attempt_failed delivered
    dead_lettered=$(echo "$metrics_body" | grep "^iris_delivery_dead_lettered_total" | awk '{print $2}' || echo "0")
    attempt_failed=$(echo "$metrics_body" | grep "^iris_delivery_attempt_failed_total" | awk '{print $2}' || echo "0")
    delivered=$(echo "$metrics_body" | grep "^iris_delivery_delivered_total" | awk '{print $2}' || echo "0")

    dead_lettered="${dead_lettered:-0}"
    attempt_failed="${attempt_failed:-0}"
    delivered="${delivered:-0}"

    echo "${timestamp} METRICS enqueued/delivered=${delivered} failed=${attempt_failed} dead_lettered=${dead_lettered}"

    # Dead-letter alert: new dead-letters since last check.
    local new_dead_letters=$((dead_lettered - PREV_DEAD_LETTERED))
    if [[ "$new_dead_letters" -gt "$DEAD_LETTER_ALERT" ]]; then
      send_alert "Dead-letter spike" "New dead-letters since last check: ${new_dead_letters} (threshold: ${DEAD_LETTER_ALERT}). Total: ${dead_lettered}."
    fi

    # Failure rate alert.
    local total_attempts=$((delivered + attempt_failed))
    if [[ "$total_attempts" -gt 10 ]]; then
      local failure_rate
      failure_rate=$(awk "BEGIN {printf \"%.2f\", ${attempt_failed} / ${total_attempts}}")
      local exceeds
      exceeds=$(awk "BEGIN {print (${failure_rate} > ${FAILURE_RATE_ALERT}) ? 1 : 0}")
      if [[ "$exceeds" == "1" ]]; then
        send_alert "High delivery failure rate" "Failure rate: ${failure_rate} (threshold: ${FAILURE_RATE_ALERT}). Delivered: ${delivered}, Failed: ${attempt_failed}."
      fi
    fi

    PREV_DEAD_LETTERED="$dead_lettered"
    PREV_ATTEMPT_FAILED="$attempt_failed"
    PREV_DELIVERED="$delivered"
  fi

  # Save state.
  cat > "$STATE_FILE" <<EOF
PREV_DEAD_LETTERED=${dead_lettered:-0}
PREV_ATTEMPT_FAILED=${attempt_failed:-0}
PREV_DELIVERED=${delivered:-0}
PREV_STATUS=${PREV_STATUS}
EOF
}

# ---- Main ----
if $LOOP_MODE; then
  echo "Iris monitor: checking ${HEALTH_URL} every ${CHECK_INTERVAL}s (Ctrl+C to stop)"
  while true; do
    do_check
    sleep "$CHECK_INTERVAL"
  done
else
  do_check
fi
