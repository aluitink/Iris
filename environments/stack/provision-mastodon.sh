#!/usr/bin/env bash
#
# provision-mastodon.sh — create the Mastodon accounts the interop suites need (idempotent).
#
# Works against the SHARED federation stack (environments/stack/docker-compose.yml) for either the
# dev or the QA environment. The DB bootstrap (schema + migrations + seeds) is handled by the
# `mastodon-dbinit` one-shot service in docker-compose.yml, so a plain `up -d` already yields a
# usable, migrated instance. This script only (re)creates the *accounts*, using the stock
# `tootctl accounts create` command.
#
# Why two env vars on the tootctl run:
#   EMAIL_DOMAIN_ALLOWLIST  — skips the MX/DNS "reachability" probe for our domain, so
#                             account creation works with no external DNS.
#   SMTP_DELIVERY_METHOD=:test — makes Action Mailer a no-op (no mail relay exists in the stack),
#                             so Devise's email checks pass.
# `tootctl` generates a *random* password (no --password flag), so we reset it to a known
# value afterwards for the suites.
#
# Usage:
#   ./provision-mastodon.sh qa                 # create the default suite accounts in the QA env
#   ./provision-mastodon.sh dev                # ... in the dev env
#   ./provision-mastodon.sh qa imuser2 foo     # also create extra account(s) (default role)
#   ./provision-mastodon.sh qa "imuser:Admin"  # create with an explicit role
#
#   The first positional arg is the ENVIRONMENT (dev|qa); it selects environments/<env>/.env and
#   the compose project. Remaining args are extra "username[:role]" accounts.
#
# Env overrides (all optional):
#   COMPOSE_FILE     default: environments/stack/docker-compose.yml
#   ENV_FILE         default: environments/<env>/.env
#   PROJECT          default: <env>  (the -p flag; must match how the stack was brought up)
#   USER_PASSWORD    default: Password1              (known password for every account)

set -euo pipefail

# Resolve the repo root (this script lives in <root>/environments/stack/).
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/../.." && pwd)"

ENV_NAME="${1:-}"
if [[ "$ENV_NAME" == "dev" || "$ENV_NAME" == "qa" ]]; then
  shift
else
  echo "usage: $0 <dev|qa> [username[:role] ...]" >&2
  exit 2
fi

COMPOSE_FILE="${COMPOSE_FILE:-$ROOT_DIR/environments/stack/docker-compose.yml}"
ENV_FILE="${ENV_FILE:-$ROOT_DIR/environments/${ENV_NAME}/.env}"
PROJECT="${PROJECT:-$ENV_NAME}"
PASSWORD="${USER_PASSWORD:-Password1}"

# FQDN_MASTODON comes from the environment .env (email domain + allowlist).
DOMAIN="$(grep -E '^FQDN_MASTODON=' "$ENV_FILE" | cut -d= -f2-)"
if [[ -z "$DOMAIN" ]]; then
  echo "error: FQDN_MASTODON not found in $ENV_FILE" >&2
  exit 2
fi

# Accounts the interop suites expect: "username:role". imuser is the admin.
DEFAULT_ACCOUNTS=("imuser:Admin")

EXTRA=()
for arg in "$@"; do
  case "$arg" in
    -h|--help) sed -n '2,30p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) EXTRA+=("$arg") ;;
  esac
done

DC() { docker compose -f "$COMPOSE_FILE" --env-file "$ENV_FILE" -p "$PROJECT" "$@"; }

# Run a Ruby snippet in the web container WITHOUT inline-shell quoting (which mangles `?`/`!`).
# Writes the snippet to a temp file via printf, runs `rails runner` on it, cleans up.
run_ruby() {
  local snippet="$1"
  DC run --rm mastodon-web sh -c \
    "printf '%s' \"$snippet\" > /tmp/pv.rb && bundle exec rails runner /tmp/pv.rb && rm -f /tmp/pv.rb" \
    >/dev/null 2>&1
}

create_account() {
  local username="$1" role="$2"      # role may be empty → use the instance default role
  local email="${username}@${DOMAIN}"

  # tootctl --role looks up a role by NAME. The instance's default ("User") role has a BLANK
  # name in v4.7, so it cannot be addressed by name — pass --role only for real named roles
  # (Owner/Admin/Moderator) and let tootctl assign the default role otherwise.
  local role_arg=()
  if [[ -n "$role" ]]; then
    role_arg=(--role="$role")
  fi

  # Idempotency: exit 0 if the account already exists.
  local exists=1
  if run_ruby "exit!(User.where(email: '${email}').exists? ? 0 : 1)"; then
    exists=0
  fi
  if [[ "$exists" -eq 0 ]]; then
    echo "    - $username already exists (skipping create)"
  else
    echo "    - creating $username${role:+ (role=$role)}"
    DC run --rm \
      -e EMAIL_DOMAIN_ALLOWLIST="$DOMAIN" \
      -e SMTP_DELIVERY_METHOD=":test" \
      mastodon-web bundle exec tootctl accounts create "$username" \
        --email="$email" --confirmed "${role_arg[@]}" >/dev/null
  fi

  # Reset to a KNOWN password (tootctl generates a random one). Idempotent.
  run_ruby "u = User.find_by(email: '${email}'); u.password = '${PASSWORD}'; u.password_confirmation = '${PASSWORD}'; u.save!"
  echo "      password -> ${PASSWORD}"
}

echo "==> Environment  : $ENV_NAME (project: $PROJECT)"
echo "==> Compose file : $COMPOSE_FILE"
echo "==> Domain       : $DOMAIN"
echo "==> Password     : $PASSWORD"
echo "==> Accounts:"

for entry in "${DEFAULT_ACCOUNTS[@]}" "${EXTRA[@]}"; do
  username="${entry%%:*}"
  role="${entry#*:}"
  [[ "$role" == "$entry" ]] && role=""      # no ':' given → default role (empty)
  create_account "$username" "$role"
done

echo "==> Done. Log in at https://${DOMAIN}/auth/sign_in"
echo "    (email: <user>@${DOMAIN}, password: ${PASSWORD}; imuser is admin)"
