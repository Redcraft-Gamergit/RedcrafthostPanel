#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BACKUP_SCRIPT="$SCRIPT_DIR/pi-daily-backup.sh"
CRON_FILE="/etc/cron.d/gamehostpanel-backup"
RUN_USER="${SUDO_USER:-$USER}"
RUN_HOUR="${RUN_HOUR:-3}"
RUN_MINUTE="${RUN_MINUTE:-15}"

if [[ ! -f "$BACKUP_SCRIPT" ]]; then
  echo "Backup script not found: $BACKUP_SCRIPT"
  exit 1
fi

chmod +x "$BACKUP_SCRIPT"

echo "Installing daily backup cron job for user '$RUN_USER' at ${RUN_HOUR}:${RUN_MINUTE}..."

sudo tee "$CRON_FILE" >/dev/null <<EOF
SHELL=/bin/bash
PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin
$RUN_MINUTE $RUN_HOUR * * * $RUN_USER $BACKUP_SCRIPT >> /var/log/gamehostpanel-backup.log 2>&1
EOF

sudo chmod 0644 "$CRON_FILE"
sudo systemctl restart cron || sudo service cron restart || true

echo "Cron installed: $CRON_FILE"
echo "Manual test:"
echo "  $BACKUP_SCRIPT"
echo "Backups folder:"
echo "  $HOME/Desktop/GameHostPanelBackups"
