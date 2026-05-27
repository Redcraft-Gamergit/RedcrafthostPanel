#!/usr/bin/env bash
set -euo pipefail

# Daily backup for Raspberry Pi host
# - Creates one backup per day
# - Keeps max 2 backup files
# - Deletes oldest automatically

PROJECT_DIR="${PROJECT_DIR:-$HOME/GameHostPanel}"
DATA_DIR="${DATA_DIR:-$PROJECT_DIR/data}"
CONFIG_FILE="${CONFIG_FILE:-$PROJECT_DIR/backend/config.json}"
BACKUP_DIR="${BACKUP_DIR:-$HOME/Desktop/GameHostPanelBackups}"
MAX_BACKUPS="${MAX_BACKUPS:-2}"

mkdir -p "$BACKUP_DIR"

DATE_TAG="$(date +%Y-%m-%d)"
BACKUP_FILE="$BACKUP_DIR/gamehostpanel-backup-$DATE_TAG.tar.gz"
TMP_FILE="$BACKUP_FILE.tmp"

if [[ ! -d "$PROJECT_DIR" ]]; then
  echo "Project directory not found: $PROJECT_DIR"
  exit 1
fi

if [[ ! -d "$DATA_DIR" ]]; then
  echo "Data directory not found: $DATA_DIR"
  exit 1
fi

tar -czf "$TMP_FILE" -C "$PROJECT_DIR" data $( [[ -f "$CONFIG_FILE" ]] && printf "backend/config.json" )
mv "$TMP_FILE" "$BACKUP_FILE"

mapfile -t backups < <(ls -1t "$BACKUP_DIR"/gamehostpanel-backup-*.tar.gz 2>/dev/null || true)
if (( ${#backups[@]} > MAX_BACKUPS )); then
  for old_file in "${backups[@]:MAX_BACKUPS}"; do
    rm -f "$old_file"
  done
fi

echo "Backup complete: $BACKUP_FILE"
