param(
  [string]$ServiceName = "GameHostPanel",
  [string]$DisplayName = "GameHostPanel",
  [string]$Port = "8080"
)

$ErrorActionPreference = "Stop"
$Root = Resolve-Path (Join-Path $PSScriptRoot "..")
$Dll = Join-Path $Root "publish\backend\GameHostPanel.Api.dll"
$Runner = Join-Path $Root "publish\run-service.ps1"

if (-not (Test-Path $Dll)) {
  & (Join-Path $PSScriptRoot "publish.ps1")
}

$Dotnet = (Get-Command dotnet).Source
$RunnerContent = @"
Set-Location -LiteralPath "$Root\publish\backend"
& "$Dotnet" "$Dll"
"@
Set-Content -LiteralPath $Runner -Value $RunnerContent -Encoding UTF8
$PowerShell = (Get-Command powershell).Source
$BinPath = "`"$PowerShell`" -NoProfile -ExecutionPolicy Bypass -File `"$Runner`""

if (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
  sc.exe stop $ServiceName | Out-Null
  sc.exe delete $ServiceName | Out-Null
  Start-Sleep -Seconds 2
}

sc.exe create $ServiceName binPath= $BinPath start= auto DisplayName= $DisplayName | Out-Null
sc.exe description $ServiceName "Self-hosted Docker Game Hosting Panel" | Out-Null
sc.exe start $ServiceName | Out-Null

Write-Host "$DisplayName installiert und gestartet: http://localhost:$Port"
