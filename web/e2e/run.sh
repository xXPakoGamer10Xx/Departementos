#!/usr/bin/env bash
# Runs the end-to-end tests on a throwaway stack: a scratch database on the dev PostgreSQL (docker compose), the API, the worker (fast reminder checks)
# and the web, all on spare ports, then removes everything it started. Nothing is committed or published; it only needs Docker, dotnet, pnpm and Chrome.
#
#   ./web/e2e/run.sh                 all tests
#   ./web/e2e/run.sh claim.spec.ts   only that file (any playwright arguments work)
set -euo pipefail
cd "$(dirname "$0")/../.."
ROOT="$(pwd)"
set -a; . ./.env; set +a
PG_PORT="${POSTGRES_PORT:-5434}"; DB="habita_e2e"; API_PORT=5334; WEB_PORT=3101; LOGS="$(mktemp -d)"
PG="$(docker compose ps -q postgres)"; [ -n "$PG" ] || { echo "Levanta PostgreSQL primero (docker compose up -d postgres)." >&2; exit 1; }
PIDS=()
cleanup() {
  for p in "${PIDS[@]:-}"; do [ -n "$p" ] && kill "$p" 2> /dev/null || true; done
  for port in "$API_PORT" "$WEB_PORT"; do lsof -tiTCP:"$port" -sTCP:LISTEN 2> /dev/null | xargs -r kill 2> /dev/null || true; done
  docker exec "$PG" psql -U "${POSTGRES_USER:-habita}" -d postgres -qc "DROP DATABASE IF EXISTS $DB WITH (FORCE)" > /dev/null 2>&1 || true
  echo "(logs de la corrida en $LOGS)"
}
trap cleanup EXIT
docker exec "$PG" psql -U "${POSTGRES_USER:-habita}" -d postgres -qc "DROP DATABASE IF EXISTS $DB WITH (FORCE)" -c "CREATE DATABASE $DB"

export ASPNETCORE_ENVIRONMENT=Development DOTNET_ENVIRONMENT=Development RateLimiting__Enabled=false
export ConnectionStrings__Default="Host=localhost;Port=${PG_PORT};Database=${DB};Username=${POSTGRES_USER:-habita};Password=${POSTGRES_PASSWORD:-change-me}"
dotnet build Habita.sln -v q --nologo 2>&1 | tail -1
dotnet run --no-build --project src/Habita.Api --no-launch-profile --urls "http://localhost:${API_PORT}" > "$LOGS/api.log" 2>&1 & PIDS+=($!)
Worker__ReminderCheckMinutes=0.05 Worker__DispatchSeconds=3 dotnet run --no-build --project src/Habita.Worker --no-launch-profile > "$LOGS/worker.log" 2>&1 & PIDS+=($!)
for _ in $(seq 1 90); do curl -fs "http://localhost:${API_PORT}/health" > /dev/null 2>&1 && break; sleep 1; done
curl -fs "http://localhost:${API_PORT}/health" > /dev/null || { echo "La API no arrancó: $LOGS/api.log" >&2; exit 1; }
(cd web && API_URL="http://localhost:${API_PORT}" pnpm dev -p "$WEB_PORT" > "$LOGS/web.log" 2>&1) & PIDS+=($!)
for _ in $(seq 1 90); do curl -fs "http://localhost:${WEB_PORT}/login" > /dev/null 2>&1 && break; sleep 1; done

cd web && E2E_BASE_URL="http://localhost:${WEB_PORT}" pnpm exec playwright test "$@"
