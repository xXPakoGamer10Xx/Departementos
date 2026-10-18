#!/usr/bin/env bash
# Restore test: proves a backup can actually be restored and that the restored database is sound.
# Restores the newest dump (or the one given as $1) into a scratch database, checks it, and drops the scratch database.
# Set STRICT_COMPARE=1 to also require identical row counts with the live database (only meaningful when it is quiet).
set -euo pipefail

: "${PGHOST:?define PGHOST}" "${PGUSER:?define PGUSER}" "${PGPASSWORD:?define PGPASSWORD}" "${PGDATABASE:?define PGDATABASE}"
BACKUP_DIR="${BACKUP_DIR:-/backups}"
file="${1:-$(ls -1t "$BACKUP_DIR"/${PGDATABASE}-*.dump 2>/dev/null | head -n1 || true)}"
scratch="${PGDATABASE}_restore_check"
fail() { echo "RESTORE TEST FAILED: $*" >&2; exit 1; }

[ -n "$file" ] && [ -f "$file" ] || fail "no hay ningún respaldo en $BACKUP_DIR"
echo "restoring: $file"
( cd "$(dirname "$file")" && sha256sum -c "$(basename "$file").sha256" > /dev/null ) || fail "el checksum no coincide: el respaldo está corrupto"

# The uploaded files travel with the dump: <db>-<stamp>.files.tar.gz. Its checksum is verified here too; its content is checked against the database below.
files_archive="${file%.dump}.files.tar.gz"
if [ -f "$files_archive" ]; then
  ( cd "$(dirname "$files_archive")" && sha256sum -c "$(basename "$files_archive").sha256" > /dev/null ) || fail "el checksum de los archivos subidos no coincide: el respaldo está corrupto"
fi

psql -v ON_ERROR_STOP=1 -q -d postgres -c "DROP DATABASE IF EXISTS \"$scratch\"" -c "CREATE DATABASE \"$scratch\""
trap 'psql -q -d postgres -c "DROP DATABASE IF EXISTS \"$scratch\"" > /dev/null 2>&1 || true' EXIT
pg_restore --no-owner --exit-on-error --dbname="$scratch" "$file" || fail "pg_restore terminó con error"

q() { psql -d "$scratch" -tAX -v ON_ERROR_STOP=1 -c "$1"; }
live() { psql -d "$PGDATABASE" -tAX -v ON_ERROR_STOP=1 -c "$1"; }

# 1. Schema: migrations history and the tables the application cannot live without.
migrations="$(q 'SELECT count(*) FROM "__EFMigrationsHistory"')"
[ "$migrations" -gt 0 ] || fail "el historial de migraciones está vacío"
for t in Organizations Memberships Properties Units Tenants Leases RentCharges Payments PaymentAllocations MaintenanceIssues MaintenanceIssueEvents AuditLogs StoredFiles PaymentClaims CashTokens Notifications ContractTemplates LeaseDocuments Receipts InventoryItems DepositSettlements DepositDeductions; do
  [ "$(q "SELECT count(*) FROM information_schema.tables WHERE table_schema='public' AND table_name='$t'")" = "1" ] || fail "falta la tabla $t"
done

# 2. Protections that live in the database must have survived the restore.
for trg in audit_logs_no_update_delete payment_allocations_limits payments_protect_trg payment_allocations_protect_trg maintenance_issue_events_no_update_delete \
           payment_claims_protect_trg cash_tokens_protect_trg notifications_protect_trg contract_templates_protect_trg lease_documents_immutable_trg receipts_immutable_trg \
           deposit_settlements_protect_trg deposit_deductions_immutable_trg; do
  [ "$(q "SELECT count(*) FROM pg_trigger WHERE tgname='$trg' AND NOT tgisinternal")" = "1" ] || fail "falta el trigger $trg"
done
[ "$(q "SELECT count(*) FROM pg_constraint WHERE conname='EX_Leases_NoOverlap'")" = "1" ] || fail "falta la restricción de no solapamiento de contratos"

# 3. Business invariants over the restored data (all must be zero).
check_zero() { local n; n="$(q "$2")"; [ "$n" = "0" ] || fail "$1: $n filas"; }
check_zero "aplicaciones activas sobre pagos revertidos" 'SELECT count(*) FROM "PaymentAllocations" a JOIN "Payments" p ON p."Id"=a."PaymentId" WHERE a."ReversedAt" IS NULL AND p."Status"='"'"'Reversed'"'"
check_zero "pagos con más aplicado que su importe" 'SELECT count(*) FROM (SELECT p."Id" FROM "Payments" p JOIN "PaymentAllocations" a ON a."PaymentId"=p."Id" AND a."ReversedAt" IS NULL GROUP BY p."Id", p."Amount" HAVING SUM(a."Amount") > p."Amount") x'
check_zero "cargos con más aplicado que su importe" 'SELECT count(*) FROM (SELECT c."Id" FROM "RentCharges" c JOIN "PaymentAllocations" a ON a."RentChargeId"=c."Id" AND a."ReversedAt" IS NULL GROUP BY c."Id", c."Amount" HAVING SUM(a."Amount") > c."Amount") x'
check_zero "cargos anulados con pagos aplicados" 'SELECT count(*) FROM "RentCharges" c JOIN "PaymentAllocations" a ON a."RentChargeId"=c."Id" AND a."ReversedAt" IS NULL WHERE c."Status"='"'"'Voided'"'"
check_zero "contratos en vigor que se solapan" 'SELECT count(*) FROM "Leases" a JOIN "Leases" b ON a."UnitId"=b."UnitId" AND a."Id"<b."Id" AND a."Status" IN ('"'"'Active'"'"','"'"'Ended'"'"') AND b."Status" IN ('"'"'Active'"'"','"'"'Ended'"'"') AND daterange(a."StartDate",a."EndDate",'"'"'[]'"'"') && daterange(b."StartDate",b."EndDate",'"'"'[]'"'"')'

check_zero "liquidaciones de depósito con cuentas que no cuadran" 'SELECT count(*) FROM "DepositSettlements" WHERE "RefundAmount" <> "HeldAmount" - "DeductionsTotal" OR "DeductionsTotal" <> COALESCE((SELECT SUM(d."Amount") FROM "DepositDeductions" d WHERE d."SettlementId"="DepositSettlements"."Id"),0)'
check_zero "más de una liquidación vigente por contrato" 'SELECT count(*) FROM (SELECT "LeaseId" FROM "DepositSettlements" WHERE "VoidedAt" IS NULL GROUP BY "LeaseId" HAVING count(*) > 1) x'

# 3b. Uploaded files: every file the restored database refers to must be in the archive, with the bytes the database says (SHA-256).
stored="$(q 'SELECT count(*) FROM "StoredFiles"')"
if [ "$stored" != "0" ]; then
  [ -f "$files_archive" ] || fail "la base tiene $stored archivos subidos pero no hay respaldo de archivos ($(basename "$files_archive"))"
  tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"; psql -q -d postgres -c "DROP DATABASE IF EXISTS \"$scratch\"" > /dev/null 2>&1 || true' EXIT
  tar -C "$tmp" -xzf "$files_archive" || fail "no se pudo leer el archivo de respaldo de los archivos subidos"
  missing=0; corrupt=0
  while IFS='|' read -r org id sha; do
    f="$tmp/$org/$id"
    if [ ! -f "$f" ]; then missing=$((missing + 1)); echo "falta el archivo $org/$id" >&2
    elif [ "$(sha256sum "$f" | cut -d' ' -f1)" != "$sha" ]; then corrupt=$((corrupt + 1)); echo "archivo alterado o dañado $org/$id" >&2; fi
  done < <(q "SELECT translate(\"OrganizationId\"::text,'-',''), translate(\"Id\"::text,'-',''), lower(\"Sha256\") FROM \"StoredFiles\"")
  [ "$missing" = "0" ] || fail "$missing archivos subidos no están en el respaldo"
  [ "$corrupt" = "0" ] || fail "$corrupt archivos subidos no coinciden con su huella"
  echo "archivos subidos verificados: $stored"
fi

# 4. Row counts, optionally compared with the live database.
mismatch=0
printf '%-26s %10s\n' tabla filas
for t in Organizations Memberships Properties Units Tenants Leases RentCharges Payments PaymentAllocations MaintenanceIssues MaintenanceIssueEvents AuditLogs StoredFiles DepositSettlements Receipts LeaseDocuments; do
  n="$(q "SELECT count(*) FROM \"$t\"")"
  line="$(printf '%-26s %10s' "$t" "$n")"
  if [ "${STRICT_COMPARE:-0}" = "1" ]; then
    l="$(live "SELECT count(*) FROM \"$t\"")"
    if [ "$n" != "$l" ]; then line="$line   ≠ en vivo: $l"; mismatch=1; else line="$line   = en vivo"; fi
  fi
  echo "$line"
done
[ "$mismatch" = "0" ] || fail "los conteos no coinciden con la base en vivo"

echo "RESTORE TEST OK: $(basename "$file") se restauró en la base temporal y pasó $migrations migraciones, esquema, protecciones e invariantes."
