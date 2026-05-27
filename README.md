# GameHostPanel

Lokales/self-hosted Game-Hosting-Panel für Docker-Server. Backend ist ASP.NET Core 8, Frontend ist React + TypeScript + Tailwind, Standarddatenbank ist SQLite. PostgreSQL kann in `backend/config.json` aktiviert werden.

## Enthalten

- Docker Engine API: Container, Images, Volumes, Networks
- Game-Server-Instanzen mit Ports, Env-Vars, Volumes, RAM/CPU-Limits
- Minecraft Vanilla, Paper, Purpur, Forge, NeoForge, Fabric plus CS:GO, Rust, ARK, Valheim, Terraria, FiveM
- Start/Stop/Restart/Kill, WebSocket-Live-Konsole, Docker-Stats
- Datei-Manager mit Liste, Upload, Download und Texteditor
- Start-Command-Konfigurator für eigene Java/Jar-Commands
- JWT-Login, Admin/User-Rollen, API-Keys
- Autostart beim Server-Boot über Panel-Service plus Docker `unless-stopped`

## Start unter Windows

Voraussetzungen: .NET 8 SDK, Node.js 20+ und Docker Desktop/Engine.

```powershell
cd "$env:USERPROFILE\Desktop\GameHostPanel"
.\scripts\setup.ps1 -User admin
.\scripts\start-dev.ps1
```

Frontend: http://localhost:5173  
Backend/API: http://localhost:8080

## Raspberry Pi Quick Install

Direkt vom GitHub-Repository aus:

```bash
curl -fsSL https://raw.githubusercontent.com/<owner>/<repo>/main/install-gamehostpanel-pi.sh | bash -s -- --repo https://github.com/<owner>/<repo>.git
```

Optional:

```bash
curl -fsSL https://raw.githubusercontent.com/<owner>/<repo>/main/install-gamehostpanel-pi.sh | bash -s -- --repo https://github.com/<owner>/<repo>.git --port 8080
```

## Raspberry Pi Daily Backups (Desktop, max 2)

Ziel: TÃ¤glich 1 Backup auf den Desktop, maximal 2 Backups behalten.

```bash
cd ~/GameHostPanel
chmod +x ./scripts/pi-daily-backup.sh ./scripts/install-pi-backup-cron.sh
./scripts/install-pi-backup-cron.sh
```

Das Backup landet hier:

`~/Desktop/GameHostPanelBackups`

Manueller Test:

```bash
~/GameHostPanel/scripts/pi-daily-backup.sh
ls -lah ~/Desktop/GameHostPanelBackups
```

## Produktion/Autostart

```powershell
cd "$env:USERPROFILE\Desktop\GameHostPanel"
.\scripts\publish.ps1
.\scripts\install-windows-service.ps1
```

Linux:

```bash
cd ~/GameHostPanel
./scripts/setup.sh admin
dotnet publish backend/GameHostPanel.Api.csproj -c Release -o publish/backend
mkdir -p publish/frontend
cp -r frontend/dist publish/frontend/dist
./scripts/install-linux-systemd.sh
```

## Wenig RAM

Das Backend pollt nicht dauerhaft aggressiv. Docker-Stats werden beim Dashboard/Detail-Refresh gelesen, Logs nur bei geöffneter Konsole gestreamt, SQLite läuft im Prozess, und das Frontend wird im Produktionsmodus statisch vom Backend ausgeliefert.

## Config

Kopiere `config.example.json` nach `backend/config.json`. Wichtige Werte:

- `Panel:HttpPort`: HTTP-Port, Standard `8080`
- `Panel:DataRoot`: Datenordner für SQLite und Serverdateien
- `Panel:DockerEndpoint`: leer = Auto; Linux `unix:///var/run/docker.sock`, Windows `npipe://./pipe/docker_engine`
- `Panel:DatabaseProvider`: `sqlite` oder `postgresql`
- `Panel:JwtSecret`: vor öffentlicher Nutzung unbedingt ändern

## Docker-Rechte

Windows braucht Docker Desktop oder eine erreichbare Docker Engine. Linux braucht Zugriff auf `/var/run/docker.sock`, zum Beispiel über die `docker`-Gruppe oder einen Service-User mit passenden Rechten.
