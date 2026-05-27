#!/usr/bin/env bash
set -euo pipefail

USER_NAME="${1:-admin}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BACKEND="$ROOT/backend"
CONFIG="$BACKEND/config.json"

if [ ! -f "$CONFIG" ]; then
  cp "$ROOT/config.example.json" "$CONFIG"
  echo "config.json wurde erstellt: $CONFIG"
fi

command -v dotnet >/dev/null || { echo ".NET 8 SDK fehlt."; exit 1; }
command -v node >/dev/null || { echo "Node.js fehlt."; exit 1; }

(cd "$ROOT/frontend" && npm install && npm run build)
(cd "$BACKEND" && dotnet run -- setup --user "$USER_NAME")

echo "Setup fertig. Start: ./scripts/start-dev.sh"
