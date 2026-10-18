#!/usr/bin/env bash
# Levanta el entorno de DESARROLLO completo: PostgreSQL + Mailpit (Docker), la API (.NET), el Worker (recordatorios y envío de avisos) y la web (Next.js).
#
#   ./dev.sh            arranca todo; Ctrl+C detiene la API y la web
#   ./dev.sh --seed     además crea los datos de demostración (ficticios) la primera vez
#   ./dev.sh --stop     apaga PostgreSQL, Mailpit y Gotenberg (los datos se conservan)
#
# Si un puerto está ocupado (por ejemplo el 3000 o el 5432 por otro proyecto) elige el siguiente libre y te lo dice.
set -euo pipefail
cd "$(dirname "$0")"

SEED=0
for arg in "$@"; do
  case "$arg" in
    --seed) SEED=1 ;;
    --stop) docker compose down && echo "PostgreSQL, Mailpit y Gotenberg apagados (los datos siguen en el volumen)." && exit 0 ;;
    -h|--help) sed -n '2,9p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "Opción desconocida: $arg (usa --help)" >&2; exit 1 ;;
  esac
done

for cmd in docker dotnet pnpm lsof curl; do
  command -v "$cmd" > /dev/null || { echo "Falta '$cmd'. Instálalo y vuelve a intentar." >&2; exit 1; }
done
docker info > /dev/null 2>&1 || { echo "Docker no está corriendo. Abre Docker Desktop y vuelve a intentar." >&2; exit 1; }

[ -f .env ] || { cp .env.example .env; echo "Creé .env desde .env.example (contraseña de desarrollo)."; }
set -a; . ./.env; set +a

free_port() { local p=$1; while lsof -iTCP:"$p" -sTCP:LISTEN > /dev/null 2>&1; do p=$((p + 1)); done; echo "$p"; }
say() { printf '\033[1m%s\033[0m\n' "$*"; }

# --- PostgreSQL + Mailpit -------------------------------------------------------------------------------------------
if [ -z "$(docker compose ps -q postgres 2> /dev/null)" ]; then
  PG_PORT="$(free_port "${POSTGRES_PORT:-5432}")"
  [ "$PG_PORT" != "${POSTGRES_PORT:-5432}" ] && echo "El puerto ${POSTGRES_PORT:-5432} está ocupado: uso el $PG_PORT para PostgreSQL."
  export POSTGRES_PORT="$PG_PORT"
else
  PG_PORT="$(docker compose port postgres 5432 2> /dev/null | sed 's/.*://' || true)"; PG_PORT="${PG_PORT:-${POSTGRES_PORT:-5432}}"
fi
# Gotenberg (PDF de contratos y recibos): la primera vez descarga su imagen (es grande).
if [ -z "$(docker compose ps -q gotenberg 2> /dev/null)" ]; then
  GOTENBERG_PORT="$(free_port "${GOTENBERG_PORT:-3010}")"
else
  GOTENBERG_PORT="$(docker compose port gotenberg 3000 2> /dev/null | sed 's/.*://' || true)"; GOTENBERG_PORT="${GOTENBERG_PORT:-3010}"
fi
export GOTENBERG_PORT
say "Levantando PostgreSQL, Mailpit y Gotenberg (la primera vez descarga imágenes)…"
docker compose up -d > /dev/null
for _ in $(seq 1 60); do
  docker compose exec -T postgres pg_isready -U "${POSTGRES_USER:-habita}" -d "${POSTGRES_DB:-habita}" > /dev/null 2>&1 && break
  sleep 1
done
docker compose exec -T postgres pg_isready -U "${POSTGRES_USER:-habita}" -d "${POSTGRES_DB:-habita}" > /dev/null 2>&1 || { echo "PostgreSQL no respondió a tiempo." >&2; exit 1; }

# --- Puertos y configuración ----------------------------------------------------------------------------------------
WANT_API="${API_PORT:-5222}"; WANT_WEB="${WEB_PORT:-3000}"
API_PORT="$(free_port "$WANT_API")"; WEB_PORT="$(free_port "$WANT_WEB")"
[ "$API_PORT" != "$WANT_API" ] && echo "El puerto $WANT_API está ocupado: la API usará el $API_PORT."
[ "$WEB_PORT" != "$WANT_WEB" ] && echo "El puerto $WANT_WEB está ocupado: la web usará el $WEB_PORT."
export ASPNETCORE_ENVIRONMENT=Development
export ConnectionStrings__Default="Host=localhost;Port=${PG_PORT};Database=${POSTGRES_DB:-habita};Username=${POSTGRES_USER:-habita};Password=${POSTGRES_PASSWORD:-change-me}"
export Pdf__GotenbergUrl="http://localhost:${GOTENBERG_PORT}"
export Web__BaseUrl="http://localhost:${WEB_PORT}"
export Security__AllowedOrigins="http://localhost:${WEB_PORT}"

[ -d web/node_modules ] || { say "Instalando dependencias de la web (solo la primera vez)…"; (cd web && pnpm install); }

# --- Procesos (cada uno en su propio grupo para poder detenerlos limpios con Ctrl+C) -------------------------------
set -m
PIDS=()
cleanup() { trap - INT TERM EXIT; echo; say "Deteniendo API, worker y web…"; for p in "${PIDS[@]:-}"; do [ -n "$p" ] && kill -- -"$p" 2> /dev/null || true; done; echo "Listo. PostgreSQL, Mailpit y Gotenberg siguen arriba (./dev.sh --stop para apagarlos)."; }
trap 'cleanup; exit 0' INT TERM
trap cleanup EXIT

prefix() { awk -v tag="$1" '{ print "[" tag "] " $0; fflush() }'; }

dotnet run --project src/Habita.Api --no-launch-profile --urls "http://localhost:${API_PORT}" 2>&1 | prefix api &
PIDS+=($!)
say "Esperando a la API (compila y aplica migraciones)…"
for _ in $(seq 1 120); do curl -fs "http://localhost:${API_PORT}/health" > /dev/null 2>&1 && break; sleep 1; done
curl -fs "http://localhost:${API_PORT}/health" > /dev/null 2>&1 || { echo "La API no arrancó. Revisa los mensajes [api] de arriba." >&2; exit 1; }

# El Worker produce los recordatorios diarios y envía por correo (Mailpit) y push los avisos ya guardados.
DOTNET_ENVIRONMENT=Development dotnet run --project src/Habita.Worker --no-launch-profile 2>&1 | prefix worker &
PIDS+=($!)

if [ "$SEED" = 1 ]; then
  say "Creando datos de demostración…"
  RateLimiting__Enabled=false Database__MigrateOnStartup=false dotnet run --no-build --project src/Habita.Api --no-launch-profile -- --seed-demo 2>&1 \
    | grep -E "Datos de demostración|ya existe|Contraseña|Residencial Demo \(|Exception" || true
fi

(cd web && API_URL="http://localhost:${API_PORT}" pnpm dev -p "$WEB_PORT" 2>&1 | prefix web) &
PIDS+=($!)
for _ in $(seq 1 90); do curl -fs "http://localhost:${WEB_PORT}/login" > /dev/null 2>&1 && break; sleep 1; done

echo
say "Habita está listo"
echo "  Web (usa esta):  http://localhost:${WEB_PORT}"
echo "  API:             http://localhost:${API_PORT}   (salud: /health, OpenAPI: /openapi/v1.json)"
echo "  Correos (Mailpit): http://localhost:8025"
echo "  PDF (Gotenberg):   http://localhost:${GOTENBERG_PORT}"
echo "  PostgreSQL:      localhost:${PG_PORT}  (usuario ${POSTGRES_USER:-habita})"
[ "$SEED" = 1 ] && echo "  Demo: propietario@demo.example  ·  contraseña Habita-Demo-2026!  (también admin@, finanzas@, mantenimiento@, lectura@)"
echo "  Ctrl+C para detener."
wait
