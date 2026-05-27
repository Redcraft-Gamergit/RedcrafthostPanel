param(
  [string]$User = "admin",
  [string]$Password = ""
)

$ErrorActionPreference = "Stop"
$Root = Resolve-Path (Join-Path $PSScriptRoot "..")
$Backend = Join-Path $Root "backend"
$Config = Join-Path $Backend "config.json"

if (-not (Test-Path $Config)) {
  Copy-Item (Join-Path $Root "config.example.json") $Config
  Write-Host "config.json wurde erstellt: $Config"
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
  throw ".NET 8 SDK fehlt. Installiere das SDK, nicht nur die Runtime."
}

if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
  throw "Node.js fehlt."
}

Push-Location (Join-Path $Root "frontend")
try {
  npm install
  npm run build
}
finally {
  Pop-Location
}

Push-Location $Backend
try {
  if ([string]::IsNullOrWhiteSpace($Password)) {
    dotnet run -- setup --user $User
  } else {
    dotnet run -- setup --user $User --password $Password
  }
}
finally {
  Pop-Location
}

Write-Host "Setup fertig. Start: scripts/start-dev.ps1"
