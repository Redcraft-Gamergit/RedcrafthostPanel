#!/usr/bin/env bash
set -euo pipefail

# GameHostPanel Raspberry Pi installer
# Usage examples:
#   curl -fsSL https://raw.githubusercontent.com/<owner>/<repo>/main/install-gamehostpanel-pi.sh | bash
#   curl -fsSL https://raw.githubusercontent.com/<owner>/<repo>/main/install-gamehostpanel-pi.sh | bash -s -- --repo https://github.com/<owner>/<repo>.git

REPO_URL=""
TARGET_DIR="$HOME/GameHostPanel"
PORT="8080"
JWT_SECRET=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --repo)
      REPO_URL="${2:-}"
      shift 2
      ;;
    --dir)
      TARGET_DIR="${2:-}"
      shift 2
      ;;
    --port)
      PORT="${2:-8080}"
      shift 2
      ;;
    --jwt-secret)
      JWT_SECRET="${2:-}"
      shift 2
      ;;
    *)
      echo "Unknown argument: $1"
      exit 1
      ;;
  esac
done

echo "[1/8] Checking OS and architecture..."
if ! grep -qiE "debian|ubuntu|raspbian" /etc/os-release; then
  echo "This installer supports Debian/Ubuntu/Raspberry Pi OS only."
  exit 1
fi

ARCH="$(uname -m)"
if [[ "$ARCH" != "aarch64" && "$ARCH" != "armv7l" && "$ARCH" != "arm64" ]]; then
  echo "Warning: detected architecture '$ARCH'. Continuing anyway."
fi

echo "[2/8] Installing base packages..."
sudo apt update
sudo apt install -y git curl ca-certificates gnupg

echo "[3/8] Installing Docker if missing..."
if ! command -v docker >/dev/null 2>&1; then
  curl -fsSL https://get.docker.com | sh
fi

echo "[4/8] Enabling Docker service..."
sudo systemctl enable docker
sudo systemctl start docker
sudo usermod -aG docker "$USER" || true

echo "[5/8] Preparing project directory..."
if [[ -n "$REPO_URL" ]]; then
  if [[ -d "$TARGET_DIR/.git" ]]; then
    echo "Git repo already exists at $TARGET_DIR, pulling latest changes..."
    git -C "$TARGET_DIR" pull --ff-only
  else
    if [[ -d "$TARGET_DIR" ]] && [[ -n "$(ls -A "$TARGET_DIR" 2>/dev/null || true)" ]]; then
      echo "Directory '$TARGET_DIR' already exists and is not a git repo."
      echo "For safety, installer will NOT delete it automatically."
      echo "Please either:"
      echo "  1) move/rename that folder, or"
      echo "  2) run with a new --dir path."
      exit 1
    fi
    mkdir -p "$TARGET_DIR"
    git clone "$REPO_URL" "$TARGET_DIR"
  fi
else
  if [[ ! -f "$TARGET_DIR/docker-compose.yml" ]]; then
    echo "No --repo supplied and no project found at $TARGET_DIR."
    echo "Please pass --repo https://github.com/<owner>/<repo>.git"
    exit 1
  fi
fi

cd "$TARGET_DIR"

echo "[6/8] Creating backend config..."
if [[ ! -f "backend/config.json" ]]; then
  cp "config.example.json" "backend/config.json"
fi

if [[ -z "$JWT_SECRET" ]]; then
  JWT_SECRET="$(head -c 48 /dev/urandom | base64 | tr -d '\n' | tr '/+' 'ab')"
fi

export GHP_PORT="$PORT"
export GHP_JWT="$JWT_SECRET"

python3 - <<'PYEOF'
import json
from pathlib import Path
import os

path = Path("backend/config.json")
cfg = json.loads(path.read_text(encoding="utf-8"))
cfg.setdefault("Panel", {})
cfg["Panel"]["HttpPort"] = int(os.environ["GHP_PORT"])
cfg["Panel"]["JwtSecret"] = os.environ["GHP_JWT"]
path.write_text(json.dumps(cfg, indent=2), encoding="utf-8")
PYEOF

echo "[7/8] Building and starting container..."
sudo docker compose up -d --build

echo "[8/8] Done."
IP_ADDR="$(hostname -I | awk '{print $1}')"
echo
echo "GameHostPanel is starting:"
echo "  Local:  http://localhost:${PORT}"
if [[ -n "${IP_ADDR:-}" ]]; then
  echo "  LAN:    http://${IP_ADDR}:${PORT}"
fi
echo
echo "Important:"
echo "  - Log out and log in again once, so your docker group permissions apply."
echo "  - First web login will ask to create the admin account."
