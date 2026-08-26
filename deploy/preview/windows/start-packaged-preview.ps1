[CmdletBinding()]
param(
    [string] $PackageDirectory = (Split-Path -Parent $PSScriptRoot),
    [string] $StateDirectory = (Join-Path $env:ProgramData 'RecallVault'),
    [string] $ApiUrl = 'http://127.0.0.1:5278',
    [string] $McpUrl = 'http://127.0.0.1:8080'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-LoopbackUrl([string] $Value, [string] $Name) {
    $uri = [Uri]$Value
    $address = $null
    $isAddress = [Net.IPAddress]::TryParse($uri.Host, [ref]$address)
    if ($uri.Scheme -ne 'http' -or ($uri.Host -ne 'localhost' -and (-not $isAddress -or -not [Net.IPAddress]::IsLoopback($address)))) {
        throw "$Name must be an HTTP loopback URL."
    }
}

function Require-EnvironmentValue([string] $Name) {
    if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($Name, 'Process'))) {
        throw "Required process environment variable '$Name' is missing."
    }
}

Assert-LoopbackUrl $ApiUrl 'ApiUrl'
Assert-LoopbackUrl $McpUrl 'McpUrl'
$packagePath = (Resolve-Path -LiteralPath $PackageDirectory).Path
$apiExecutable = Join-Path $packagePath 'Recall.Api\Recall.Api.exe'
$mcpExecutable = Join-Path $packagePath 'Recall.Mcp.Http\Recall.Mcp.Http.exe'
if (-not (Test-Path -LiteralPath $apiExecutable) -or -not (Test-Path -LiteralPath $mcpExecutable)) {
    throw 'The package does not contain the expected self-contained Windows executables.'
}

Require-EnvironmentValue 'RecallPreview__AllowedOrigins__0'
Require-EnvironmentValue 'RecallPreview__AllowedHosts__0'
Require-EnvironmentValue 'Recall__DataDirectory'
$authMode = [Environment]::GetEnvironmentVariable('RecallPreview__AuthMode', 'Process')
if ([string]::IsNullOrWhiteSpace($authMode)) { $authMode = 'OAuth' }
if ($authMode -eq 'OAuth') {
    @(
        'RecallPreview__PublicBaseUrl', 'RecallPreview__OAuth__Authority',
        'RecallPreview__OAuth__Audience', 'RecallPreview__OAuth__RequiredScope',
        'RecallPreview__Tenants__0__Subject', 'RecallPreview__Tenants__0__ClientId',
        'RecallPreview__Tenants__0__Token'
    ) | ForEach-Object { Require-EnvironmentValue $_ }
} elseif ($authMode -eq 'StaticToken') {
    @('RecallPreview__Token', 'RECALL_CLIENT_ID', 'RECALL_CLIENT_TOKEN') | ForEach-Object { Require-EnvironmentValue $_ }
} else {
    throw 'RecallPreview__AuthMode must be OAuth or StaticToken.'
}

$logDirectory = Join-Path $StateDirectory 'logs'
New-Item -ItemType Directory -Force -Path $StateDirectory, $logDirectory | Out-Null
$env:Recall__Url = $ApiUrl
$mcpProcess = $null
$apiProcess = Start-Process $apiExecutable -PassThru -WindowStyle Hidden `
    -RedirectStandardOutput (Join-Path $logDirectory 'api.stdout.log') `
    -RedirectStandardError (Join-Path $logDirectory 'api.stderr.log')

try {
    $apiDeadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
    do {
        if ($apiProcess.HasExited) { throw "Recall API exited with code $($apiProcess.ExitCode)." }
        try { $apiHealth = Invoke-RestMethod -Uri "$ApiUrl/health" -TimeoutSec 2 } catch { $apiHealth = $null }
        if ($null -eq $apiHealth) { Start-Sleep -Milliseconds 250 }
    } until ($null -ne $apiHealth -or [DateTimeOffset]::UtcNow -ge $apiDeadline)
    if ($null -eq $apiHealth) { throw 'Recall API did not become healthy within 30 seconds.' }

    $env:ASPNETCORE_URLS = $McpUrl
    $env:RecallPreview__Enabled = 'true'
    $env:RecallPreview__AuthMode = $authMode
    $env:Recall__ApiUrl = $ApiUrl
    $mcpProcess = Start-Process $mcpExecutable -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $logDirectory 'mcp.stdout.log') `
        -RedirectStandardError (Join-Path $logDirectory 'mcp.stderr.log')

    $mcpDeadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
    do {
        if ($mcpProcess.HasExited) { throw "Remote MCP host exited with code $($mcpProcess.ExitCode)." }
        try { $mcpHealth = Invoke-RestMethod -Uri "$McpUrl/health" -TimeoutSec 2 } catch { $mcpHealth = $null }
        if ($null -eq $mcpHealth) { Start-Sleep -Milliseconds 250 }
    } until ($null -ne $mcpHealth -or [DateTimeOffset]::UtcNow -ge $mcpDeadline)
    if ($null -eq $mcpHealth) { throw 'Remote MCP host did not become healthy within 30 seconds.' }

    $state = [ordered]@{
        packageDirectory = $packagePath
        apiProcessId = $apiProcess.Id
        mcpProcessId = $mcpProcess.Id
        startedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    }
    $state | ConvertTo-Json | Set-Content -Encoding utf8 (Join-Path $StateDirectory 'processes.json')
    [pscustomobject]@{ Status = 'healthy'; ApiProcessId = $apiProcess.Id; McpProcessId = $mcpProcess.Id; AuthMode = $authMode }
} catch {
    if ($null -ne $mcpProcess -and -not $mcpProcess.HasExited) { Stop-Process -Id $mcpProcess.Id -Force }
    if (-not $apiProcess.HasExited) { Stop-Process -Id $apiProcess.Id -Force }
    throw
}
