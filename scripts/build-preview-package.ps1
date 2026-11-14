# Build a local portable preview ZIP only after exact-commit Windows test evidence.
# Never pushes, merges, creates GitHub releases, installs software, or cleans up files.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)]
    [string]$VerificationFile
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $repo

$dirty = @(git status --porcelain)
if ($LASTEXITCODE -ne 0) { throw 'Cannot read Git status.' }
if ($dirty.Count -gt 0) { throw 'Working tree is dirty. Preserve changes before packaging.' }

$commit = (git rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot read Git HEAD.' }
if ($commit -notmatch '^[0-9a-fA-F]{40}$') { throw 'Unexpected Git commit format.' }

$evidencePath = (Resolve-Path -LiteralPath $VerificationFile -ErrorAction Stop).Path
$qaRoot = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts\qa'))
$qaRoot = $qaRoot.TrimEnd([char[]]@('\', '/')) + '\'
if (-not $evidencePath.StartsWith($qaRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Verification evidence must be under $qaRoot"
}

$map = @{}
foreach ($line in Get-Content -LiteralPath $evidencePath) {
    if ($line -match '^([A-Za-z][A-Za-z0-9]*)=(.*)$') {
        $map[$matches[1]] = $matches[2]
    }
}
foreach ($name in @('Commit','TestTotal','TestPassed','TestFailed','TestNotExecuted','TestReport')) {
    if (-not $map.ContainsKey($name)) { throw "Verification evidence missing $name." }
}
if ($map['Commit'] -ne $commit) {
    throw "Evidence commit $($map['Commit']) does not match current HEAD $commit."
}

[int]$testTotal = 0
[int]$testPassed = 0
[int]$testFailed = 0
[int]$testSkipped = 0
if (-not [int]::TryParse($map['TestTotal'], [ref]$testTotal) -or
    -not [int]::TryParse($map['TestPassed'], [ref]$testPassed) -or
    -not [int]::TryParse($map['TestFailed'], [ref]$testFailed) -or
    -not [int]::TryParse($map['TestNotExecuted'], [ref]$testSkipped)) {
    throw 'Test totals in verification evidence are not integers.'
}
if ($testTotal -lt 74 -or $testPassed -ne $testTotal -or
    $testFailed -ne 0 -or $testSkipped -ne 0) {
    throw "Release gate not satisfied: $testPassed/$testTotal, failed=$testFailed, skipped=$testSkipped."
}

# Independently re-read the actual test results, not just the summary text.
$trxPath = (Resolve-Path -LiteralPath $map['TestReport'] -ErrorAction Stop).Path
if (-not $trxPath.StartsWith($qaRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'TRX report must be stored in the ignored local artifacts/qa directory.'
}
[xml]$trx = Get-Content -LiteralPath $trxPath -Raw
$counters = $trx.SelectSingleNode("//*[local-name()='Counters']")
if ($null -eq $counters) { throw 'TRX test results are missing counters.' }
if ([int]$counters.GetAttribute('total') -ne $testTotal -or
    [int]$counters.GetAttribute('passed') -ne $testPassed -or
    [int]$counters.GetAttribute('failed') -ne 0 -or
    [int]$counters.GetAttribute('notExecuted') -ne 0) {
    throw 'TRX counters do not match passing verification evidence.'
}

$shortSha = $commit.Substring(0, 12)
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$name = "StorageChronicle-preview-win-x64-$shortSha-$stamp"
$targetRoot = Join-Path $repo 'artifacts\preview'
$publishDir = Join-Path $targetRoot "$name-files"
$zipPath = Join-Path $targetRoot "$name.zip"
$manifestPath = Join-Path $targetRoot "$name.sha256.txt"
if (Test-Path -LiteralPath $publishDir) { throw 'Publish output directory already exists.' }
if (Test-Path -LiteralPath $zipPath) { throw 'ZIP output already exists.' }
New-Item -ItemType Directory -Force -Path $targetRoot | Out-Null

Write-Host "=== Verified source commit: $commit ==="
Write-Host "=== Windows tests: $testPassed/$testTotal ==="
Write-Host "=== Building local portable Windows preview (no GitHub release) ==="
dotnet publish .\StorageChronicle.App\StorageChronicle.App.csproj --configuration Release --runtime win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugSymbols=false -p:DebugType=none -warnaserror --output "$publishDir"
if ($LASTEXITCODE -ne 0) { throw 'Release preview publish failed.' }

$exe = Join-Path $publishDir 'StorageChronicle.App.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw "Expected Windows application executable missing: $exe"
}

$instructions = @(
    'StorageChronicle portable preview — NOT a signed or final release'
    "Commit: $commit"
    "Windows test evidence: $testPassed/$testTotal pass"
    'Extract the entire ZIP to a new directory on a Windows x64 computer.'
    'Launch StorageChronicle.App.exe as your NORMAL user, not administrator.'
    'Existing snapshot metadata remains in D:\StorageChronicleData\snapshots.db when D: is available.'
    'Do not move/delete snapshot databases, and do not delete Windows system files based on residuals.'
    'Test snapshot history, file/folder comparisons, coverage, reconciliation and the VSS permissions display.'
    'This package has not been automatically installed or published.'
)
$instructions | Set-Content -LiteralPath (Join-Path $publishDir 'README_PREVIEW.txt') -Encoding UTF8
Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $([IO.Path]::GetFileName($zipPath))" |
    Set-Content -LiteralPath $manifestPath -Encoding ASCII

Write-Host "=== LOCAL PREVIEW PACKAGE CREATED ==="
Write-Host "ZIP: $zipPath"
Write-Host "SHA-256: $hash"
Write-Host "Manifest: $manifestPath"
Write-Host 'Nothing was installed, merged or uploaded. Manual smoke testing is still required.'
