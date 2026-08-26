[CmdletBinding()]
param(
    [string] $Repository = 'rishiparpyani12-dot/recall-vault',
    [string] $RootDirectory = 'C:\RecallVault'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($Repository -ne 'rishiparpyani12-dot/recall-vault') {
    throw 'This updater is pinned to rishiparpyani12-dot/recall-vault.'
}

$headers = @{ Accept = 'application/vnd.github+json'; 'User-Agent' = 'RecallVault-Preview-Updater' }
$releases = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repository/releases?per_page=20" -Headers $headers
$release = $releases | Where-Object { $_.prerelease -and -not $_.draft } | Select-Object -First 1
if ($null -eq $release) { throw 'No published prerelease is available.' }
if ($release.tag_name -notmatch '^v[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$') { throw 'The prerelease tag is invalid.' }

$manifestAsset = $release.assets | Where-Object name -eq 'release-manifest.json' | Select-Object -First 1
if ($null -eq $manifestAsset) { throw 'The prerelease does not contain release-manifest.json.' }
if ($manifestAsset.size -le 0 -or $manifestAsset.size -gt 65536) { throw 'The release manifest has an invalid size.' }
$downloadDirectory = Join-Path $RootDirectory 'downloads'
$releaseDirectory = Join-Path $RootDirectory 'releases'
New-Item -ItemType Directory -Force -Path $downloadDirectory, $releaseDirectory | Out-Null
$manifestPath = Join-Path $downloadDirectory "$($release.tag_name)-manifest.json"
Invoke-WebRequest -Uri $manifestAsset.browser_download_url -Headers $headers -OutFile $manifestPath
$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json

if ($manifest.repository -ne $Repository -or $manifest.version -ne $release.tag_name -or $manifest.commit -notmatch '^[0-9a-f]{40}$' -or
    $manifest.archive -notmatch '^recall-vault-v[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?-win-x64\.zip$' -or
    $manifest.sha256 -notmatch '^[0-9a-f]{64}$' -or $manifest.selfContained -ne $true -or $manifest.deploymentMode -ne 'synthetic-preview') {
    throw 'The release manifest failed validation.'
}
$archiveAsset = $release.assets | Where-Object name -eq $manifest.archive | Select-Object -First 1
if ($null -eq $archiveAsset) { throw 'The manifest archive is not attached to the prerelease.' }
if ($archiveAsset.size -le 1024 -or $archiveAsset.size -gt 536870912) { throw 'The release archive has an invalid size.' }
$archivePath = Join-Path $downloadDirectory $manifest.archive
Invoke-WebRequest -Uri $archiveAsset.browser_download_url -Headers $headers -OutFile $archivePath
$actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $archivePath).Hash.ToLowerInvariant()
if ($actualHash -ne $manifest.sha256) { Remove-Item -LiteralPath $archivePath -Force; throw 'The downloaded archive SHA-256 does not match the release manifest.' }

$target = Join-Path $releaseDirectory $manifest.version
if (-not (Test-Path -LiteralPath $target)) {
    $staging = Join-Path $releaseDirectory (".staging-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $staging | Out-Null
    try {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
        try {
            $stagingPrefix = [IO.Path]::GetFullPath($staging).TrimEnd('\') + '\'
            foreach ($entry in $archive.Entries) {
                $entryPath = [IO.Path]::GetFullPath((Join-Path $staging $entry.FullName))
                if (-not $entryPath.StartsWith($stagingPrefix, [StringComparison]::OrdinalIgnoreCase)) {
                    throw "The release archive contains an unsafe path: $($entry.FullName)"
                }
            }
        } finally { $archive.Dispose() }
        Expand-Archive -LiteralPath $archivePath -DestinationPath $staging
        @('Recall.Api\Recall.Api.exe', 'Recall.Mcp.Http\Recall.Mcp.Http.exe', 'deploy\start-packaged-preview.ps1') | ForEach-Object {
            if (-not (Test-Path -LiteralPath (Join-Path $staging $_))) { throw "The release is missing required file '$_'." }
        }
        Move-Item -LiteralPath $staging -Destination $target
    } catch {
        if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
        throw
    }
}

$pending = [ordered]@{ version = $manifest.version; commit = $manifest.commit; path = $target; sha256 = $manifest.sha256; stagedAtUtc = [DateTimeOffset]::UtcNow.ToString('O') }
$pending | ConvertTo-Json | Set-Content -Encoding utf8 (Join-Path $RootDirectory 'pending-release.json')
[pscustomobject]@{ Status = 'staged-awaiting-activation'; Version = $manifest.version; Commit = $manifest.commit; Path = $target }
