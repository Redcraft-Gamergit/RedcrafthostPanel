#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

(cd "$ROOT/backend" && dotnet run) &
(cd "$ROOT/frontend" && npm run dev) &

echo "Backend:  http://localhost:8080"
echo "Frontend: http://localhost:5173"
wait
