[CmdletBinding()]
param(
    [string]$ApiAssembly = (Join-Path $PSScriptRoot '..\..\..\src\Recall.Api\bin\Release\net10.0\Recall.Api.dll'),
    [string]$McpAssembly = (Join-Path $PSScriptRoot '..\..\..\src\Recall.Mcp.Http\bin\Release\net10.0\Recall.Mcp.Http.dll'),
    [string]$ApiUrl = 'http://127.0.0.1:5278',
    [string]$McpUrl = 'http://127.0.0.1:8080',
    [string]$LogDirectory = (Join-Path $env:LOCALAPPDATA 'RecallVault\preview-logs')
)

$ErrorActionPreference = 'Stop'

function Assert-LoopbackUrl([string]$Value, [string]$Name) {
    $uri = [Uri]$Value
    $address = $null
    $isAddress = [Net.IPAddress]::TryParse($uri.Host, [ref]$address)
    $isLoopback = $uri.Host -eq 'localhost' -or ($isAddress -and [Net.IPAddress]::IsLoopback($address))
    if ($uri.Scheme -ne 'http' -or -not $isLoopback) {
        throw "$Name must be an HTTP loopback URL. Put HTTPS at a reviewed reverse proxy or private tunnel."
    }
}

function Require-EnvironmentValue([string]$Name) {
    $value = [Environment]::GetEnvironmentVariable($Name, 'Process')
    if ([string]::IsNullOrWhiteSpace($value)) { throw "Required process environment variable '$Name' is missing." }
}

Assert-LoopbackUrl $ApiUrl 'ApiUrl'
Assert-LoopbackUrl $McpUrl 'McpUrl'
$apiPath = (Resolve-Path -LiteralPath $ApiAssembly).Path
$mcpPath = (Resolve-Path -LiteralPath $McpAssembly).Path

Require-EnvironmentValue 'RecallPreview__AllowedOrigins__0'
Require-EnvironmentValue 'RecallPreview__AllowedHosts__0'
$authMode = [Environment]::GetEnvironmentVariable('RecallPreview__AuthMode', 'Process')
if ([string]::IsNullOrWhiteSpace($authMode)) { $authMode = 'OAuth' }
if ($authMode -eq 'OAuth') {
    @(
        'RecallPreview__PublicBaseUrl',
        'RecallPreview__OAuth__Authority',
        'RecallPreview__OAuth__Audience',
        'RecallPreview__OAuth__RequiredScope',
        'RecallPreview__Tenants__0__Subject',
        'RecallPreview__Tenants__0__ClientId',
        'RecallPreview__Tenants__0__Token'
    ) | ForEach-Object { Require-EnvironmentValue $_ }
} elseif ($authMode -eq 'StaticToken') {
    Require-EnvironmentValue 'RecallPreview__Token'
    Require-EnvironmentValue 'RECALL_CLIENT_ID'
    Require-EnvironmentValue 'RECALL_CLIENT_TOKEN'
} else {
    throw 'RecallPreview__AuthMode must be OAuth or StaticToken.'
}

New-Item -ItemType Directory -Force -Path $LogDirectory | Out-Null
$env:Recall__Url = $ApiUrl
$apiProcess = Start-Process dotnet -ArgumentList @('"' + $apiPath + '"') -PassThru -WindowStyle Hidden `
    -RedirectStandardOutput (Join-Path $LogDirectory 'api.stdout.log') `
    -RedirectStandardError (Join-Path $LogDirectory 'api.stderr.log')

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
    $mcpProcess = Start-Process dotnet -ArgumentList @('"' + $mcpPath + '"') -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $LogDirectory 'mcp.stdout.log') `
        -RedirectStandardError (Join-Path $LogDirectory 'mcp.stderr.log')

    $mcpDeadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
    do {
        if ($mcpProcess.HasExited) { throw "Remote MCP host exited with code $($mcpProcess.ExitCode)." }
        try { $mcpHealth = Invoke-RestMethod -Uri "$McpUrl/health" -TimeoutSec 2 } catch { $mcpHealth = $null }
        if ($null -eq $mcpHealth) { Start-Sleep -Milliseconds 250 }
    } until ($null -ne $mcpHealth -or [DateTimeOffset]::UtcNow -ge $mcpDeadline)
    if ($null -eq $mcpHealth) { throw 'Remote MCP host did not become healthy within 30 seconds.' }

    [pscustomobject]@{
        Warning = 'PRIVATE PREVIEW — SYNTHETIC DATA ONLY'
        ApiProcessId = $apiProcess.Id
        McpProcessId = $mcpProcess.Id
        ApiUrl = $ApiUrl
        McpUrl = "$McpUrl/mcp"
        AuthMode = $authMode
        LogDirectory = (Resolve-Path -LiteralPath $LogDirectory).Path
    }
} catch {
    if ($null -ne $mcpProcess -and -not $mcpProcess.HasExited) { Stop-Process -Id $mcpProcess.Id -Force }
    if (-not $apiProcess.HasExited) { Stop-Process -Id $apiProcess.Id -Force }
    throw
}
