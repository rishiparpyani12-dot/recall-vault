[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ConfirmVersion,
    [string] $RootDirectory = 'C:\RecallVault'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$pendingPath = Join-Path $RootDirectory 'pending-release.json'
if (-not (Test-Path -LiteralPath $pendingPath)) { throw 'No staged release is awaiting activation.' }
$pending = Get-Content -Raw -LiteralPath $pendingPath | ConvertFrom-Json
if ($pending.version -ne $ConfirmVersion) { throw 'ConfirmVersion does not match the staged release.' }
if (-not (Test-Path -LiteralPath $pending.path)) { throw 'The staged release directory does not exist.' }
$releaseRoot = [IO.Path]::GetFullPath((Join-Path $RootDirectory 'releases')).TrimEnd('\') + '\'
$pending.path = (Resolve-Path -LiteralPath $pending.path).Path
if (-not $pending.path.StartsWith($releaseRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'The staged release path is outside the release directory.' }
$dataDirectory = [Environment]::GetEnvironmentVariable('Recall__DataDirectory', 'Process')
if ([string]::IsNullOrWhiteSpace($dataDirectory)) { throw 'Recall__DataDirectory must be explicitly configured before activation.' }
$dataDirectory = [IO.Path]::GetFullPath($dataDirectory)
if ($dataDirectory -eq [IO.Path]::GetFullPath($RootDirectory) -or -not $dataDirectory.StartsWith(([IO.Path]::GetFullPath($RootDirectory).TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Recall__DataDirectory must be a dedicated directory beneath RootDirectory.'
}

$currentPath = Join-Path $RootDirectory 'current-release.json'
$previous = if (Test-Path -LiteralPath $currentPath) { Get-Content -Raw -LiteralPath $currentPath | ConvertFrom-Json } else { $null }
if ($null -ne $previous) {
    if (-not (Test-Path -LiteralPath $previous.path)) { throw 'The current release marker refers to a missing directory.' }
    $previous.path = (Resolve-Path -LiteralPath $previous.path).Path
    if (-not $previous.path.StartsWith($releaseRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'The current release path is outside the release directory.' }
}
$processPath = Join-Path $env:ProgramData 'RecallVault\processes.json'
if (Test-Path -LiteralPath $processPath) {
    if ($null -eq $previous) { throw 'A process marker exists without a current release marker; refusing to stop any process.' }
    $processes = Get-Content -Raw -LiteralPath $processPath | ConvertFrom-Json
    @($processes.mcpProcessId, $processes.apiProcessId) | ForEach-Object {
        $process = Get-Process -Id $_ -ErrorAction SilentlyContinue
        if ($null -ne $process) {
            if ([string]::IsNullOrWhiteSpace($process.Path) -or -not $process.Path.StartsWith(($previous.path.TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase)) {
                throw "Recorded process ID $_ does not belong to the active Recall release."
            }
            Stop-Process -Id $_
            Wait-Process -Id $_ -Timeout 15 -ErrorAction SilentlyContinue
        }
    }
    Remove-Item -LiteralPath $processPath -Force
}

$backupPath = $null
if (Test-Path -LiteralPath $dataDirectory) {
    $backupRoot = Join-Path $RootDirectory 'backups'
    New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null
    $backupPath = Join-Path $backupRoot ((Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ') + '-' + $pending.version)
    Copy-Item -LiteralPath $dataDirectory -Destination $backupPath -Recurse
}

try {
    $launcher = Join-Path $pending.path 'deploy\start-packaged-preview.ps1'
    $result = & $launcher -PackageDirectory $pending.path
    $current = [ordered]@{ version = $pending.version; commit = $pending.commit; path = $pending.path; sha256 = $pending.sha256; activatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O') }
    $current | ConvertTo-Json | Set-Content -Encoding utf8 $currentPath
    Remove-Item -LiteralPath $pendingPath -Force
    $result
} catch {
    if ($null -ne $backupPath) {
        if (Test-Path -LiteralPath $dataDirectory) { Remove-Item -LiteralPath $dataDirectory -Recurse -Force }
        Copy-Item -LiteralPath $backupPath -Destination $dataDirectory -Recurse
    }
    if ($null -ne $previous -and (Test-Path -LiteralPath $previous.path)) {
        & (Join-Path $previous.path 'deploy\start-packaged-preview.ps1') -PackageDirectory $previous.path | Out-Null
    }
    throw "Activation failed and the previous data/version was restored: $($_.Exception.Message)"
}
