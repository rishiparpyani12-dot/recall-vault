[CmdletBinding()]
param(
    [ValidateSet('x64')]
    [string] $Architecture = 'x64',
    [string] $ArtifactsDirectory,
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
$sourceDirectory = Join-Path $ArtifactsDirectory 'src'
$installDirectory = Join-Path $ArtifactsDirectory 'vcpkg_installed'
$outputDirectory = Join-Path $ArtifactsDirectory 'output'

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

New-Item -ItemType Directory -Force -Path $ArtifactsDirectory, $outputDirectory | Out-Null

if (-not (Test-Path -LiteralPath (Join-Path $sourceDirectory '.git'))) {
    New-Item -ItemType Directory -Force -Path $sourceDirectory | Out-Null
    & git -C $sourceDirectory init --quiet
    & git -C $sourceDirectory remote add origin $configuration.repository
}

& git -C $sourceDirectory fetch --quiet --depth 1 origin "refs/tags/$($configuration.tag)"
if ($LASTEXITCODE -ne 0) { throw 'Unable to fetch the pinned SQLCipher tag.' }
$resolvedCommit = (& git -C $sourceDirectory rev-parse FETCH_HEAD).Trim()
if ($resolvedCommit -ne $configuration.commit) {
    throw "SQLCipher source verification failed. Expected $($configuration.commit), received $resolvedCommit."
}
& git -C $sourceDirectory checkout --quiet --detach $resolvedCommit
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
$compilerOptions = @(
    '-DSQLITE_HAS_CODEC',
    '-DSQLCIPHER_CRYPTO_OPENSSL',
    '-DSQLITE_EXTRA_INIT=sqlcipher_extra_init',
    '-DSQLITE_EXTRA_SHUTDOWN=sqlcipher_extra_shutdown',
    '-DSQLITE_THREADSAFE=1',
    '-DSQLITE_TEMP_STORE=2',
    '-DSQLITE_ENABLE_FTS5',
    "-I$opensslRoot\include"
) -join ' '
$linkerOptions = "/LIBPATH:$opensslRoot\lib libcrypto.lib"

$buildCommand = @(
    "call `"$vcvars`"",
    "cd /d `"$sourceDirectory`"",
    'nmake /f Makefile.msc clean',
    "nmake /f Makefile.msc sqlite3.dll NO_TCL=1 USE_CRT_DLL=1 DLL_FILE_NAME=sqlcipher.dll LIB_FILE_NAME=sqlcipher.lib `"OPTS=$compilerOptions`" `"CORE_LINK_OPTS=$linkerOptions`""
) -join ' && '

& cmd.exe /d /s /c $buildCommand
if ($LASTEXITCODE -ne 0) { throw 'The SQLCipher native build failed.' }

$requiredFiles = @('sqlcipher.dll', 'sqlcipher.lib', 'sqlite3.h', 'sqlite3ext.h')
foreach ($file in $requiredFiles) {
    $path = Join-Path $sourceDirectory $file
    if (-not (Test-Path -LiteralPath $path)) { throw "Expected build output '$file' was not produced." }
    Copy-Item -LiteralPath $path -Destination $outputDirectory -Force
}

$opensslRuntime = Get-ChildItem -LiteralPath (Join-Path $opensslRoot 'bin') -Filter 'libcrypto-3-x64.dll' | Select-Object -First 1
if (-not $opensslRuntime) { throw 'The OpenSSL runtime DLL was not found.' }
Copy-Item -LiteralPath $opensslRuntime.FullName -Destination $outputDirectory -Force

$checksums = Get-ChildItem -LiteralPath $outputDirectory -File |
    Sort-Object Name |
    ForEach-Object { "{0}  {1}" -f (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant(), $_.Name }
$checksums | Set-Content -Encoding ascii (Join-Path $outputDirectory 'SHA256SUMS')

Write-Host "Verified SQLCipher $($configuration.tag) ($resolvedCommit) and built win-$Architecture artifacts in $outputDirectory"
