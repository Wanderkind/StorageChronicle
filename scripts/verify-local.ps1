# Offline Windows local verification. No git operations, GitHub Actions, or cleanup.
[CmdletBinding()]
param([switch]$RunApp)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $repo

Write-Host "=== SDK ==="
dotnet --info
if ($LASTEXITCODE -ne 0) { throw "dotnet --info failed" }

Write-Host "=== Git status (read-only) ==="
git status --short
if ($LASTEXITCODE -ne 0) { throw "git status failed" }

Write-Host "=== Restore ==="
dotnet restore .\StorageChronicle.slnx
if ($LASTEXITCODE -ne 0) { throw "restore failed" }

Write-Host "=== Build (warnings as errors) ==="
dotnet build .\StorageChronicle.slnx --no-restore -warnaserror
if ($LASTEXITCODE -ne 0) { throw "build failed" }

Write-Host "=== Tests ==="
dotnet test .\StorageChronicle.Tests\StorageChronicle.Tests.csproj --no-restore --logger "console;verbosity=normal"
if ($LASTEXITCODE -ne 0) { throw "tests failed" }

Write-Host "=== Verification passed ==="
if ($RunApp) {
    dotnet run --project .\StorageChronicle.App\StorageChronicle.App.csproj --no-restore
    if ($LASTEXITCODE -ne 0) { throw "app failed" }
}
