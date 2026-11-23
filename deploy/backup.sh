#!/usr/bin/env bash
# Backs up the MySQL database and the uploaded documents of the production stack.
# Usage (on the server, from anywhere): ~/realestate/deploy/backup.sh [label]   (label defaults to "daily")
# Output: $BACKUP_DIR (default ~/backups): db-<label>-<time>.sql.gz and docs-<label>-<time>.tar.gz
# Old backups are deleted after KEEP_DAYS (default 14). Never touches the live data.
set -euo pipefail

cd "$(dirname "$0")/.."
COMPOSE="docker compose -f docker-compose.prod.yml"
BACKUP_DIR="${BACKUP_DIR:-$HOME/backups}"
KEEP_DAYS="${KEEP_DAYS:-14}"
label="${1:-daily}"
stamp="$(date +%F_%H%M%S)"

umask 077
mkdir -p "$BACKUP_DIR"

db_file="$BACKUP_DIR/db-$label-$stamp.sql.gz"
docs_file="$BACKUP_DIR/docs-$label-$stamp.tar.gz"
trap 'rm -f "$db_file.tmp" "$docs_file.tmp"' EXIT

echo "[$(date -Is)] backup '$label' started"

# Database: consistent snapshot (InnoDB) without locking the app. Password is read inside the container.
$COMPOSE exec -T mysql sh -c 'MYSQL_PWD="$MYSQL_ROOT_PASSWORD" mysqldump -uroot --single-transaction --routines --triggers --events --no-tablespaces realestate' \
  | gzip > "$db_file.tmp"
gzip -t "$db_file.tmp"
[ "$(gzip -dc "$db_file.tmp" | head -c 2000 | grep -c 'MySQL dump')" -ge 1 ] || { echo "database dump looks invalid" >&2; exit 1; }
mv "$db_file.tmp" "$db_file"

# Uploaded documents: read-only copy from the volume mounted in the api container.
api_id="$($COMPOSE ps -q api)"
[ -n "$api_id" ] || { echo "api container is not running; cannot back up documents" >&2; exit 1; }
docker run --rm --volumes-from "$api_id:ro" alpine:3 tar czf - -C /var/realestate/storage . > "$docs_file.tmp"
gzip -t "$docs_file.tmp"
mv "$docs_file.tmp" "$docs_file"

# Retention
find "$BACKUP_DIR" -maxdepth 1 -type f \( -name 'db-*.sql.gz' -o -name 'docs-*.tar.gz' \) -mtime +"$KEEP_DAYS" -delete

echo "[$(date -Is)] backup done: $(basename "$db_file") ($(du -h "$db_file" | cut -f1)), $(basename "$docs_file") ($(du -h "$docs_file" | cut -f1))"
