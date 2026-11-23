#!/usr/bin/env bash
# Runs ON THE SERVER after the new code has been copied to ~/realestate (done by .github/workflows/deploy.yaml).
# Data safety: backs up first, never removes volumes (no "down -v"), keeps .env, database and documents.
set -euo pipefail

cd "$(dirname "$0")/.."
COMPOSE="docker compose -f docker-compose.prod.yml"

[ -f .env ] || { echo "ERROR: .env is missing in $(pwd). Create it first (see docs/deployment-linux-docker.md step 4)." >&2; exit 1; }
env_value() { grep -E "^$1=" .env | head -n1 | cut -d= -f2-; }

# 1. Safety backup of existing data (skipped on the very first deploy when nothing is running yet)
if [ -n "$($COMPOSE ps -q mysql 2>/dev/null)" ]; then
  echo "== Pre-deploy backup"
  ./deploy/backup.sh pre-deploy
else
  echo "== First deploy: no running database, skipping backup"
fi

# 2. Build and (re)start. Volumes (mysql-data, document-storage, web-dataprotection, caddy-*) are kept.
echo "== Build and start"
$COMPOSE up -d --build --remove-orphans

# 3. Wait until the API is healthy
echo "== Waiting for API health"
api_id="$($COMPOSE ps -q api)"
for i in $(seq 1 60); do
  status="$(docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "$api_id")"
  [ "$status" = "healthy" ] && break
  sleep 5
done
[ "$status" = "healthy" ] || { echo "ERROR: api is '$status'"; $COMPOSE logs --tail=60 api; exit 1; }

# 4. Smoke test through Caddy/HTTPS (resolved locally, so it works even before DNS caches update)
web_domain="$(env_value WEB_DOMAIN)"; api_domain="$(env_value API_DOMAIN)"
check() {
  for i in $(seq 1 24); do
    code="$(curl -sS -m 10 -o /dev/null -w '%{http_code}' --resolve "$2:443:127.0.0.1" "$1" 2>/dev/null || true)"
    [ "$code" = "200" ] && { echo "OK  $1"; return 0; }
    sleep 5
  done
  echo "FAIL $1 (last status: $code)"; return 1
}
ok=0
check "https://$api_domain/api/v1/health" "$api_domain" || ok=1
check "https://$web_domain/Account/Login" "$web_domain" || ok=1
if [ "$ok" -ne 0 ]; then
  $COMPOSE ps; $COMPOSE logs --tail=60 web api caddy
  echo "ERROR: smoke test failed. Data is untouched; the pre-deploy backup is in ~/backups." >&2
  exit 1
fi

docker image prune -f >/dev/null
echo "== Deploy finished"
$COMPOSE ps --format 'table {{.Service}}\t{{.Status}}'
