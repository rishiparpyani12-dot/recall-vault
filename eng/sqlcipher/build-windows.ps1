[CmdletBinding()]
param(
    [ValidateSet('x64')]
    [string] $Architecture = 'x64',
    [string] $ArtifactsDirectory,
    [string] $BuildDirectory,
    [string] $VcpkgExecutable
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scriptDirectory = Split-Path -Parent $PSCommandPath
$repositoryRoot = Resolve-Path (Join-Path $scriptDirectory '..\..')
$configuration = Get-Content -Raw (Join-Path $scriptDirectory 'upstream.json') | ConvertFrom-Json

if ($configuration.target -ne "win-$Architecture") {
    throw "The pinned target '$($configuration.target)' does not match win-$Architecture."
}

if (-not $ArtifactsDirectory) {
    $ArtifactsDirectory = Join-Path $repositoryRoot "artifacts\sqlcipher\win-$Architecture"
}
$ArtifactsDirectory = [IO.Path]::GetFullPath($ArtifactsDirectory)
$outputDirectory = Join-Path $ArtifactsDirectory 'output'
if (-not $BuildDirectory) {
    $BuildDirectory = Join-Path ([IO.Path]::GetTempPath()) "recall-vault-sqlcipher\win-$Architecture"
}
$BuildDirectory = [IO.Path]::GetFullPath($BuildDirectory)
if ($BuildDirectory.Contains(' ')) {
    throw "The SQLCipher Windows build directory cannot contain spaces: '$BuildDirectory'."
}
$sourceDirectory = Join-Path $BuildDirectory 'src'
$installDirectory = Join-Path $BuildDirectory 'vcpkg_installed'

if (-not $VcpkgExecutable) {
    $vcpkgCommand = Get-Command vcpkg.exe -ErrorAction SilentlyContinue
    if ($vcpkgCommand) {
        $VcpkgExecutable = $vcpkgCommand.Source
    } else {
        $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
        if (-not (Test-Path -LiteralPath $vswhere)) {
            throw 'vcpkg.exe was not found and Visual Studio Installer is unavailable.'
        }
        $visualStudio = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
        if (-not $visualStudio) {
            throw 'Visual Studio C++ Build Tools were not found.'
        }
        $VcpkgExecutable = Join-Path $visualStudio 'VC\vcpkg\vcpkg.exe'
    }
}
if (-not (Test-Path -LiteralPath $VcpkgExecutable)) {
    throw "vcpkg.exe was not found at '$VcpkgExecutable'."
}

$vcpkgRoot = Split-Path -Parent $VcpkgExecutable
$vcpkgConfiguration = Get-Content -Raw (Join-Path $scriptDirectory 'vcpkg-configuration.json') | ConvertFrom-Json
$vcpkgBaseline = $vcpkgConfiguration.'default-registry'.baseline
if (Test-Path -LiteralPath (Join-Path $vcpkgRoot '.git')) {
    & git -c "safe.directory=$vcpkgRoot" -C $vcpkgRoot cat-file -e "${vcpkgBaseline}:versions/baseline.json" 2>$null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Fetching pinned vcpkg registry baseline $vcpkgBaseline..."
        & git -c "safe.directory=$vcpkgRoot" -C $vcpkgRoot fetch --quiet --force --depth 1 origin $vcpkgBaseline
        if ($LASTEXITCODE -ne 0) { throw 'Unable to fetch the pinned vcpkg registry baseline.' }
    }
}

New-Item -ItemType Directory -Force -Path $ArtifactsDirectory, $outputDirectory, $BuildDirectory | Out-Null

if (-not (Test-Path -LiteralPath (Join-Path $sourceDirectory '.git'))) {
    New-Item -ItemType Directory -Force -Path $sourceDirectory | Out-Null
    & git -C $sourceDirectory init --quiet
    & git -c "safe.directory=$sourceDirectory" -C $sourceDirectory remote add origin $configuration.repository
}

& git -c "safe.directory=$sourceDirectory" -C $sourceDirectory fetch --quiet --depth 1 origin "refs/tags/$($configuration.tag)"
if ($LASTEXITCODE -ne 0) { throw 'Unable to fetch the pinned SQLCipher tag.' }
$resolvedCommit = (& git -c "safe.directory=$sourceDirectory" -C $sourceDirectory rev-parse FETCH_HEAD).Trim()
if ($resolvedCommit -ne $configuration.commit) {
    throw "SQLCipher source verification failed. Expected $($configuration.commit), received $resolvedCommit."
}
& git -c "safe.directory=$sourceDirectory" -C $sourceDirectory checkout --quiet --detach $resolvedCommit
if ($LASTEXITCODE -ne 0) { throw 'Unable to check out the verified SQLCipher commit.' }

& $VcpkgExecutable install `
    "--x-manifest-root=$scriptDirectory" `
    "--x-install-root=$installDirectory" `
    '--triplet=x64-windows'
if ($LASTEXITCODE -ne 0) { throw 'vcpkg failed to restore the pinned OpenSSL dependency.' }

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$visualStudio = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
$vcvars = Join-Path $visualStudio 'VC\Auxiliary\Build\vcvars64.bat'
if (-not (Test-Path -LiteralPath $vcvars)) { throw 'vcvars64.bat was not found.' }

$opensslRoot = Join-Path $installDirectory 'x64-windows'
$opensslIncludeForBuild = '..\vcpkg_installed\x64-windows\include'
$opensslLibraryForBuild = '..\vcpkg_installed\x64-windows\lib'
$compilerOptions = @(
    '-DSQLITE_HAS_CODEC',
    '-DSQLCIPHER_CRYPTO_OPENSSL',
    '-DSQLITE_EXTRA_INIT=sqlcipher_extra_init',
    '-DSQLITE_EXTRA_SHUTDOWN=sqlcipher_extra_shutdown',
    '-DSQLITE_THREADSAFE=1',
    '-DSQLITE_TEMP_STORE=2',
    '-DSQLITE_ENABLE_FTS5',
    "-I$opensslIncludeForBuild"
) -join ' '
$libraryPath = "/LIBPATH:$opensslLibraryForBuild"

$buildCommand = @(
    "call `"$vcvars`"",
    "cd /d `"$sourceDirectory`"",
    'nmake /f Makefile.msc clean',
    "nmake /f Makefile.msc sqlite3.dll NO_TCL=1 `"OPTS=$compilerOptions`" `"LDOPTS=/Brepro`" `"LTLIBPATHS=$libraryPath`" `"LTLIBS=libcrypto.lib`""
) -join ' && '

& cmd.exe /d /s /c $buildCommand
if ($LASTEXITCODE -ne 0) { throw 'The SQLCipher native build failed.' }

$requiredFiles = @(
    @{ Source = 'sqlite3.dll'; Destination = 'sqlcipher.dll' },
    @{ Source = 'sqlite3.lib'; Destination = 'sqlcipher.lib' },
    @{ Source = 'sqlite3.h'; Destination = 'sqlite3.h' },
    @{ Source = 'sqlite3ext.h'; Destination = 'sqlite3ext.h' }
)
foreach ($file in $requiredFiles) {
    $path = Join-Path $sourceDirectory $file.Source
    if (-not (Test-Path -LiteralPath $path)) { throw "Expected build output '$($file.Source)' was not produced." }
    Copy-Item -LiteralPath $path -Destination (Join-Path $outputDirectory $file.Destination) -Force
}

$opensslRuntime = Get-ChildItem -LiteralPath (Join-Path $opensslRoot 'bin') -Filter 'libcrypto-3-x64.dll' | Select-Object -First 1
if (-not $opensslRuntime) { throw 'The OpenSSL runtime DLL was not found.' }
Copy-Item -LiteralPath $opensslRuntime.FullName -Destination $outputDirectory -Force

$env:DOTNET_CLI_HOME = Join-Path $BuildDirectory 'dotnet-cli'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'
$env:APPDATA = Join-Path $BuildDirectory 'appdata'
New-Item -ItemType Directory -Force -Path (Join-Path $env:APPDATA 'NuGet') | Out-Null
$smokeTestProject = Join-Path $scriptDirectory 'SmokeTest\Recall.SqlCipherSmokeTest.csproj'
& dotnet restore $smokeTestProject --configfile (Join-Path $repositoryRoot 'NuGet.Config')
if ($LASTEXITCODE -ne 0) { throw 'The SQLCipher smoke test restore failed.' }
& dotnet run --project $smokeTestProject --configuration Release --no-restore -- $outputDirectory
if ($LASTEXITCODE -ne 0) { throw 'The packaged SQLCipher smoke test failed.' }

$licenseDirectory = Join-Path $outputDirectory 'licenses'
New-Item -ItemType Directory -Force -Path $licenseDirectory | Out-Null
Copy-Item -LiteralPath (Join-Path $sourceDirectory 'LICENSE.md') -Destination (Join-Path $licenseDirectory 'SQLCipher-LICENSE.md') -Force
$opensslShare = Join-Path $opensslRoot 'share\openssl'
Copy-Item -LiteralPath (Join-Path $opensslShare 'copyright') -Destination (Join-Path $licenseDirectory 'OpenSSL-copyright') -Force
Copy-Item -LiteralPath (Join-Path $opensslShare 'vcpkg.spdx.json') -Destination (Join-Path $outputDirectory 'OpenSSL.spdx.json') -Force

$provenance = [ordered]@{
    sqlcipher = [ordered]@{
        repository = $configuration.repository
        tag = $configuration.tag
        commit = $resolvedCommit
        edition = $configuration.edition
    }
    target = $configuration.target
    cryptoProvider = $configuration.cryptoProvider
    vcpkgBaseline = $vcpkgConfiguration.'default-registry'.baseline
    openssl = [ordered]@{ version = '3.6.3'; triplet = 'x64-windows' }
}
$provenance | ConvertTo-Json -Depth 5 | Set-Content -Encoding utf8 (Join-Path $outputDirectory 'provenance.json')

$sbom = [ordered]@{
    spdxVersion = 'SPDX-2.3'
    dataLicense = 'CC0-1.0'
    SPDXID = 'SPDXRef-DOCUMENT'
    name = "Recall-Vault-SQLCipher-$($configuration.tag)-win-$Architecture"
    documentNamespace = "https://github.com/rishiparpyani12-dot/recall-vault/sbom/sqlcipher/$resolvedCommit/win-$Architecture"
    creationInfo = [ordered]@{
        created = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
        creators = @('Tool: Recall Vault eng/sqlcipher/build-windows.ps1')
    }
    packages = @(
        [ordered]@{
            name = 'SQLCipher Community Edition'
            SPDXID = 'SPDXRef-Package-SQLCipher'
            versionInfo = $configuration.tag.TrimStart('v')
            downloadLocation = "$($configuration.repository)@$resolvedCommit"
            filesAnalyzed = $false
            licenseConcluded = 'BSD-3-Clause'
            licenseDeclared = 'BSD-3-Clause'
            copyrightText = 'Copyright (c) 2008-2026, ZETETIC, LLC'
        },
        [ordered]@{
            name = 'OpenSSL'
            SPDXID = 'SPDXRef-Package-OpenSSL'
            versionInfo = '3.6.3'
            downloadLocation = 'https://github.com/openssl/openssl'
            filesAnalyzed = $false
            licenseConcluded = 'Apache-2.0'
            licenseDeclared = 'Apache-2.0'
            copyrightText = 'NOASSERTION'
        }
    )
    relationships = @(
        [ordered]@{ spdxElementId = 'SPDXRef-DOCUMENT'; relationshipType = 'DESCRIBES'; relatedSpdxElement = 'SPDXRef-Package-SQLCipher' },
        [ordered]@{ spdxElementId = 'SPDXRef-Package-SQLCipher'; relationshipType = 'DEPENDS_ON'; relatedSpdxElement = 'SPDXRef-Package-OpenSSL' }
    )
}
$sbom | ConvertTo-Json -Depth 8 | Set-Content -Encoding utf8 (Join-Path $outputDirectory 'SBOM.spdx.json')

$outputPrefixLength = $outputDirectory.TrimEnd('\').Length + 1
$checksums = Get-ChildItem -LiteralPath $outputDirectory -File -Recurse |
    Where-Object Name -ne 'SHA256SUMS' |
    Sort-Object FullName |
    ForEach-Object {
        $relativePath = $_.FullName.Substring($outputPrefixLength).Replace('\', '/')
        "{0}  {1}" -f (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant(), $relativePath
    }
$checksums | Set-Content -Encoding ascii (Join-Path $outputDirectory 'SHA256SUMS')

Write-Host "Verified SQLCipher $($configuration.tag) ($resolvedCommit) and built win-$Architecture artifacts in $outputDirectory"
