#!/usr/bin/env bash
# Arranca la versión de PRODUCCIÓN con Docker Compose: PostgreSQL, API, web, proxy con HTTPS y respaldos automáticos.
#
#   ./start.sh          construye y arranca todo (aplica las migraciones solo)
#   ./start.sh --logs   sigue los logs de los servicios
#   ./start.sh --stop   apaga todo (los datos se conservan en volúmenes)
#
# Usa .env.production. Si no existe, crea uno LOCAL de prueba (contraseña aleatoria, https://localhost con un certificado de prueba, sin correo real).
# Para un servidor real copia .env.production.example, completa TODOS los valores (dominio, SMTP, contraseña) y vuelve a correr este script.
set -euo pipefail
cd "$(dirname "$0")"

COMPOSE=(docker compose -f docker-compose.prod.yml --env-file .env.production)
case "${1:-}" in
  --stop) "${COMPOSE[@]}" down && echo "Apagado. Los datos siguen en los volúmenes (docker compose ... down -v los borraría)."; exit 0 ;;
  --logs) exec "${COMPOSE[@]}" logs -f --tail=100 ;;
  -h|--help) sed -n '2,10p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
  "") ;;
  *) echo "Opción desconocida: $1 (usa --help)" >&2; exit 1 ;;
esac

for cmd in docker lsof curl openssl; do
  command -v "$cmd" > /dev/null || { echo "Falta '$cmd'." >&2; exit 1; }
done
docker info > /dev/null 2>&1 || { echo "Docker no está corriendo. Abre Docker Desktop y vuelve a intentar." >&2; exit 1; }
say() { printf '\033[1m%s\033[0m\n' "$*"; }
free_port() { local p=$1; while lsof -iTCP:"$p" -sTCP:LISTEN > /dev/null 2>&1; do p=$((p + 1)); done; echo "$p"; }

LOCAL_TRIAL=0
if [ ! -f .env.production ]; then
  LOCAL_TRIAL=1
  HTTP_PORT="$(free_port 8081)"; HTTPS_PORT="$(free_port 8443)"
  umask 077
  cat > .env.production <<ENV
# Generado por start.sh para una PRUEBA LOCAL. Para producción real: dominio, SMTP y contraseñas propias (ver .env.production.example).
DOMAIN=localhost
PUBLIC_ORIGIN=https://localhost:${HTTPS_PORT}
ACME_EMAIL=admin@example.com
POSTGRES_USER=habita
POSTGRES_DB=habita
POSTGRES_PASSWORD=$(openssl rand -hex 24)
SMTP_HOST=smtp.invalid
SMTP_PORT=587
SMTP_SSL=true
SMTP_FROM=no-reply@localhost
BACKUP_PATH=./backups
BACKUP_INTERVAL_HOURS=24
BACKUP_RETENTION_DAYS=14
HTTP_PORT=${HTTP_PORT}
HTTPS_PORT=${HTTPS_PORT}
ENV
  echo "Creé .env.production para una prueba local (puertos ${HTTP_PORT}/${HTTPS_PORT}, correo deshabilitado)."
fi
set -a; . ./.env.production; set +a

say "Construyendo y arrancando (la primera vez tarda unos minutos)…"
"${COMPOSE[@]}" up -d --build

say "Esperando a que todo esté sano…"
for _ in $(seq 1 120); do
  web="$("${COMPOSE[@]}" ps --format '{{.Status}}' web 2> /dev/null || true)"
  api="$("${COMPOSE[@]}" ps --format '{{.Status}}' api 2> /dev/null || true)"
  case "$web$api" in *"(healthy)"*"(healthy)"*) break ;; esac
  sleep 2
done
"${COMPOSE[@]}" ps --format 'table {{.Service}}\t{{.Status}}'

URL="${PUBLIC_ORIGIN:-https://${DOMAIN}}"
echo
say "Habita (producción) está en: ${URL}"
if [ "$LOCAL_TRIAL" = 1 ] || [ "${DOMAIN}" = "localhost" ]; then
  echo "  El navegador avisará del certificado (es de prueba, emitido por Caddy para localhost): Avanzado → Continuar."
  echo "  El correo está deshabilitado (SMTP_HOST=smtp.invalid): las invitaciones no se envían. Para probar sin ellas, regístrate en ${URL}/register."
fi
echo "  Logs: ./start.sh --logs   ·   Apagar: ./start.sh --stop   ·   Respaldos: ${BACKUP_PATH:-./backups}"
