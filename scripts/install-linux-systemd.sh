#!/usr/bin/env bash
set -euo pipefail

SERVICE_NAME="${1:-gamehostpanel}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DLL="$ROOT/publish/backend/GameHostPanel.Api.dll"

if [ ! -f "$DLL" ]; then
  echo "Bitte zuerst unter Windows scripts/publish.ps1 oder unter Linux dotnet publish ausführen."
  exit 1
fi

sudo tee "/etc/systemd/system/$SERVICE_NAME.service" >/dev/null <<EOF
[Unit]
Description=GameHostPanel
After=network-online.target docker.service
Wants=network-online.target docker.service

[Service]
WorkingDirectory=$ROOT/publish/backend
ExecStart=$(command -v dotnet) $DLL
Restart=always
RestartSec=5
Environment=ASPNETCORE_ENVIRONMENT=Production

[Install]
WantedBy=multi-user.target
EOF

sudo systemctl daemon-reload
sudo systemctl enable "$SERVICE_NAME"
sudo systemctl restart "$SERVICE_NAME"
echo "$SERVICE_NAME installiert und gestartet."
