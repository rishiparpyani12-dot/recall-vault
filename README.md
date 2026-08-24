# Recall Vault

Recall Vault is a local-first, user-controlled memory service for multiple AI clients. This repository contains the Milestone 1 backend and stdio MCP adapter.

> Security status: the current database is ordinary SQLite and is **not encrypted at rest**. SQLCipher integration and OS credential-vault storage remain required before handling valuable secrets.

## Prerequisites

- .NET SDK 10
- PowerShell examples below; equivalent environment variables work on macOS/Linux

No manual database setup is needed. The service migrates a database in `%LOCALAPPDATA%\RecallVault` on first run.

## Run locally

```powershell
cd recall-vault
$env:RECALL_BOOTSTRAP_TOKEN = '<choose-a-long-random-bootstrap-token>'
dotnet run --project src/Recall.Api
```

Register a client from a second shell. Save the returned token: it is shown only once.

```powershell
$headers = @{ 'X-Recall-Bootstrap-Token' = '<same-bootstrap-token>' }
$body = @{
  name = 'Codex'
  clientType = 'mcp'
  publicIdentifier = 'codex-local'
  permissions = @(@{
    category = 'preferences'
    canRead = $true
    canCreate = $true
    canUpdate = $true
    canDelete = $true
    maximumSensitivity = 'Personal'
  })
} | ConvertTo-Json -Depth 4
$client = Invoke-RestMethod -Method Post -Uri http://127.0.0.1:5278/v1/clients -Headers $headers -ContentType application/json -Body $body
$client
```

## Configure an MCP client

Build once, then configure the AI host to launch the DLL with credentials in the child-process environment:

```powershell
dotnet build RecallVault.slnx
```

```json
{
  "mcpServers": {
    "recall-vault": {
      "command": "dotnet",
      "args": ["C:/absolute/path/recall-vault/src/Recall.Mcp/bin/Debug/net10.0/Recall.Mcp.dll"],
      "env": {
        "RECALL_API_URL": "http://127.0.0.1:5278",
        "RECALL_CLIENT_ID": "<returned-client-id>",
        "RECALL_CLIENT_TOKEN": "<returned-token>"
      }
    }
  }
}
```

Milestone 1 tools: `memory_remember`, `memory_search`, `memory_get`, `memory_update`, and `memory_forget`.

## Test

```powershell
dotnet test RecallVault.slnx
```

Architecture, security tradeoffs, and planned work are in [ADR 0001](docs/adr-0001-milestone-1-architecture.md), the [threat model](docs/threat-model.md), and the [Milestone 1 plan](docs/milestone-1-plan.md).

## Current limitations

- No encryption at rest or secure OS key storage yet.
- No desktop UI, installer, browser extension, or cloud synchronization.
- Five core MCP tools only; list, permission inspection, and access-history tools follow after the backend workflow is hardened.
- Registration is an operator API guarded by a bootstrap secret; token rotation/revocation endpoints and rate limiting are pending.
- Audit rows are application-immutable, not cryptographically tamper-evident.
- Permanent purge, vacuum, backup, and migration recovery policy are pending.
