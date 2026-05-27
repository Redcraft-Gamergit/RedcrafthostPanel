$ErrorActionPreference = "Stop"
$Root = Resolve-Path (Join-Path $PSScriptRoot "..")

Start-Process powershell -WindowStyle Normal -ArgumentList @(
  "-NoExit",
  "-Command",
  "cd '$Root\backend'; dotnet run"
)

Start-Process powershell -WindowStyle Normal -ArgumentList @(
  "-NoExit",
  "-Command",
  "cd '$Root\frontend'; npm run dev"
)

Write-Host "Backend:  http://localhost:8080"
Write-Host "Frontend: http://localhost:5173"
