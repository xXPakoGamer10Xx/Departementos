#!/usr/bin/env bash
# Backup of the Habita database (pg_dump custom format, compressed) AND of the uploaded files (payment proofs, in FILES_DIR), each with an
# integrity check and a SHA-256 checksum, plus retention. Connection comes from the standard PG* variables.
# Backups contain personal and financial data: they are created readable only by their owner (umask 077).
# The files are archived BEFORE the dump, so every file the dump refers to is already in the archive (a file uploaded in between is only an
# extra). The pair shares one timestamp: <db>-<stamp>.dump and <db>-<stamp>.files.tar.gz.
set -euo pipefail
umask 077

: "${PGHOST:?define PGHOST}" "${PGUSER:?define PGUSER}" "${PGPASSWORD:?define PGPASSWORD}" "${PGDATABASE:?define PGDATABASE}"
BACKUP_DIR="${BACKUP_DIR:-/backups}"
RETENTION_DAYS="${RETENTION_DAYS:-14}"
FILES_DIR="${FILES_DIR:-}"   # empty = no uploaded files to protect (the stack always sets it)

mkdir -p "$BACKUP_DIR"
stamp="$(date -u +%Y%m%d-%H%M%S)"
file="$BACKUP_DIR/${PGDATABASE}-${stamp}.dump"

if [ -n "$FILES_DIR" ]; then
  [ -d "$FILES_DIR" ] || { echo "BACKUP FAILED: FILES_DIR=$FILES_DIR no existe (¿volumen sin montar?)" >&2; exit 1; }
  archive="$BACKUP_DIR/${PGDATABASE}-${stamp}.files.tar.gz"
  tar -C "$FILES_DIR" -czf "$archive.partial" .
  tar -tzf "$archive.partial" > /dev/null          # the archive must be readable end to end
  mv "$archive.partial" "$archive"
  ( cd "$BACKUP_DIR" && sha256sum "$(basename "$archive")" > "$(basename "$archive").sha256" )
  echo "files ok: $archive ($(tar -tzf "$archive" | grep -vc '/$') files)"
fi

# Write to a partial file first so a crash never leaves something that looks like a good backup.
pg_dump --format=custom --compress=6 --no-owner --file="$file.partial"
pg_restore --list "$file.partial" > /dev/null          # the archive must be readable end to end
mv "$file.partial" "$file"
( cd "$BACKUP_DIR" && sha256sum "$(basename "$file")" > "$(basename "$file").sha256" )

# Retention: drop dumps (and their checksums) older than RETENTION_DAYS.
find "$BACKUP_DIR" -maxdepth 1 \( -name "${PGDATABASE}-*.dump*" -o -name "${PGDATABASE}-*.files.tar.gz*" \) -type f -mtime "+${RETENTION_DAYS}" -print -delete | sed 's/^/pruned: /'

echo "backup ok: $file ($(du -h "$file" | cut -f1), sha256 $(cut -c1-16 "$file.sha256")...)"
