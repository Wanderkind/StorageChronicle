# Offline Windows release-candidate verification. No Git operations, push,
# merge, GitHub Actions, cleanup, elevation, or source-file mutation.
[CmdletBinding()]
param([switch]$RunApp, [int]$MinimumTests = 74)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $repo

if ($MinimumTests -lt 1) { throw "MinimumTests must be positive." }

$dirty = @(git status --porcelain)
if ($LASTEXITCODE -ne 0) { throw "Cannot read working tree status." }
if ($dirty.Count -gt 0) {
    throw "Working tree is not clean. Preserve changes before release verification."
}
$commit = (git rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw "Cannot determine Git commit." }
$branch = (git branch --show-current).Trim()
if ($LASTEXITCODE -ne 0) { throw "Cannot determine Git branch." }

$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$reportDir = Join-Path $repo "artifacts\qa\release-$timestamp"
New-Item -ItemType Directory -Force -Path $reportDir | Out-Null
$trxName = "StorageChronicle-release.trx"
$trxPath = Join-Path $reportDir $trxName

Write-Host "=== Release verification ==="
Write-Host "Branch: $branch"
Write-Host "Commit: $commit"
Write-Host "Evidence directory: $reportDir"

dotnet restore .\StorageChronicle.slnx
if ($LASTEXITCODE -ne 0) { throw "NuGet restore failed." }

dotnet build .\StorageChronicle.slnx --no-restore -warnaserror
if ($LASTEXITCODE -ne 0) { throw "Warnings-as-errors build failed." }

dotnet test .\StorageChronicle.Tests\StorageChronicle.Tests.csproj --no-build --no-restore --logger "trx;LogFileName=$trxName" --results-directory "$reportDir"
if ($LASTEXITCODE -ne 0) { throw "Automated tests failed." }

if (-not (Test-Path -LiteralPath $trxPath -PathType Leaf)) {
    throw "Expected TRX report is missing: $trxPath"
}
[xml]$trx = Get-Content -LiteralPath $trxPath -Raw
$counters = $trx.SelectSingleNode("//*[local-name()='Counters']")
if ($null -eq $counters) { throw "TRX test counters are missing." }

$total = [int]$counters.GetAttribute("total")
$passed = [int]$counters.GetAttribute("passed")
$failed = [int]$counters.GetAttribute("failed")
$notExecuted = [int]$counters.GetAttribute("notExecuted")
if ($failed -ne 0 -or $notExecuted -ne 0 -or $total -lt $MinimumTests -or $passed -ne $total) {
    throw "Release gate failed: total=$total, passed=$passed, failed=$failed, notExecuted=$notExecuted; minimum=$MinimumTests."
}

$evidence = @(
    "StorageChronicle Windows release verification"
    "VerifiedUtc=$((Get-Date).ToUniversalTime().ToString('o'))"
    "Branch=$branch"
    "Commit=$commit"
    "TestTotal=$total"
    "TestPassed=$passed"
    "TestFailed=$failed"
    "TestNotExecuted=$notExecuted"
    "TestReport=$trxPath"
    "SDK=$((dotnet --version).Trim())"
)
$evidencePath = Join-Path $reportDir "verification.txt"
$evidence | Set-Content -LiteralPath $evidencePath -Encoding UTF8
Write-Host "=== PASS: $passed/$total tests, no warnings in build ==="
Write-Host "Evidence: $evidencePath"

if ($RunApp) {
    Write-Host "Opening desktop app for manual smoke testing..."
    dotnet run --project .\StorageChronicle.App\StorageChronicle.App.csproj --no-build --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Desktop app exited with nonzero code." }
}
