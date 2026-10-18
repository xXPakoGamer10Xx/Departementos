#!/usr/bin/env bash
# Runs inside the `backup` service: a backup every BACKUP_INTERVAL_HOURS and, once a week, a restore test.
set -uo pipefail
interval=$(( ${BACKUP_INTERVAL_HOURS:-24} * 3600 ))
restore_dow="${RESTORE_TEST_DOW:-7}"   # 1=Monday ... 7=Sunday
while true; do
  bash /ops/backup.sh || echo "BACKUP FAILED $(date -u +%FT%TZ)" >&2
  if [ "$(date -u +%u)" = "$restore_dow" ]; then
    bash /ops/restore-test.sh || echo "RESTORE TEST FAILED $(date -u +%FT%TZ)" >&2
  fi
  sleep "$interval"
done
