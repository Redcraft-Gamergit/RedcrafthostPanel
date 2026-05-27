$ErrorActionPreference = "Stop"
$Root = Resolve-Path (Join-Path $PSScriptRoot "..")
$Publish = Join-Path $Root "publish"

if (Test-Path $Publish) {
  Remove-Item $Publish -Recurse -Force
}

Push-Location (Join-Path $Root "frontend")
try {
  npm install
  npm run build
}
finally {
  Pop-Location
}

Push-Location (Join-Path $Root "backend")
try {
  dotnet publish -c Release -o (Join-Path $Publish "backend")
}
finally {
  Pop-Location
}

New-Item -ItemType Directory -Force -Path (Join-Path $Publish "frontend\dist") | Out-Null
Copy-Item (Join-Path $Root "frontend\dist\*") (Join-Path $Publish "frontend\dist") -Recurse -Force
Copy-Item (Join-Path $Root "config.example.json") (Join-Path $Publish "backend\config.json") -Force

Write-Host "Publish fertig: $Publish"
