# Recall Vault

Recall Vault is a local-first, user-controlled memory service for multiple AI clients. It provides one permissioned memory vault for applications such as Codex, Claude Desktop, Cursor, ChatGPT, Claude, and Gemini. This repository currently contains the Milestone 1 backend, local HTTP API, and stdio MCP adapter.

> Security status: the current database is ordinary SQLite and is **not encrypted at rest**. SQLCipher integration and OS credential-vault storage remain required before handling valuable secrets.

## Prerequisites

- .NET SDK 10
- PowerShell examples below; equivalent environment variables work on macOS/Linux

No manual database setup is needed. The service migrates a database in `%LOCALAPPDATA%\RecallVault` on first run.

When the service is running:

- Health check: `http://127.0.0.1:5278/health`
- Swagger UI: `http://127.0.0.1:5278/swagger`
- OpenAPI document: `http://127.0.0.1:5278/swagger/v1/swagger.json`

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

`PublicIdentifier` must be unique. A client token is returned only once and only its SHA-256 digest is stored. Keep the token outside source control.

Attempting to register an existing `PublicIdentifier` returns HTTP `409 Conflict` with `public_identifier_exists` rather than issuing another token.

## Test with Swagger

1. Open `http://127.0.0.1:5278/swagger`.
2. Click **Authorize** and set `BootstrapToken` to the value of `RECALL_BOOTSTRAP_TOKEN`.
3. Execute `POST /v1/clients` with a unique `publicIdentifier` and copy the returned `clientId` and `token` immediately.
4. Click **Authorize** again. Set `ClientId` to the returned UUID and `Bearer` to the raw returned token without adding a `Bearer ` prefix.
5. Execute `POST /v1/memories/` to create a memory.
6. Execute `POST /v1/memories/search` with a keyword from its content.
7. Use the returned memory ID to test get, version-checked update, and soft deletion.

Example memory:

```json
{
  "content": "I prefer jasmine tea",
  "summary": "Tea preference",
  "category": "preferences",
  "sensitivity": "Normal",
  "importance": 7,
  "confidence": 0.9,
  "sourceConversation": "swagger-test",
  "expiresAt": null,
  "purpose": "Testing Recall Vault"
}
```

Example search:

```json
{
  "query": "jasmine",
  "category": "preferences",
  "limit": 10,
  "purpose": "Testing FTS5 search"
}
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

MCP tools:

- `memory_remember`
- `memory_search`
- `memory_get`
- `memory_update`
- `memory_forget`
- `memory_list`
- `memory_permissions`
- `memory_access_history`

List, permission, and access-history results use `offset`, a maximum `limit` of 50, and `nextOffset`. Pass `nextOffset` as the next request's `offset` until it is `null`.

## Test

```powershell
dotnet test RecallVault.slnx
```

The test suite verifies sensitivity-based access denial, filtering of unauthorized search/list results, removal of soft-deleted memories from FTS5 results, duplicate-registration conflicts, credential rejection, and the authenticated HTTP workflow. It also launches the real API and MCP child processes, negotiates MCP over stdio with the official C# SDK, discovers all eight tools, exercises the memory lifecycle, and verifies rejected MCP credentials.

## Repository layout

- `src/Recall.Domain`: storage-independent entities and enums
- `src/Recall.Application`: authorization-aware memory operations
- `src/Recall.Infrastructure`: EF Core persistence, migration, and Dapper/FTS5 search
- `src/Recall.Api`: authenticated loopback HTTP API and Swagger UI
- `src/Recall.Mcp`: official C# MCP SDK stdio adapter
- `tests`: unit, SQLite integration, and real-process MCP end-to-end tests
- `docs`: architecture decision, implementation plan, and threat model

Architecture, security tradeoffs, and planned work are in [ADR 0001](docs/adr-0001-milestone-1-architecture.md), the [threat model](docs/threat-model.md), and the [Milestone 1 plan](docs/milestone-1-plan.md).

The first encryption-at-rest release is planned as Windows-only, using supported Zetetic SQLCipher builds and Windows Credential Manager for the database key. [ADR 0002](docs/adr-0002-encryption-at-rest.md) records the decision and its prerequisites. This is a plan, not a current security guarantee: the present database remains unencrypted.

## Current limitations

- No encryption at rest or secure OS key storage yet.
- No desktop UI, installer, browser extension, or cloud synchronization.
- Client administration, token rotation, and revocation tools are not implemented.
- Registration is an operator API guarded by a bootstrap secret; token rotation/revocation endpoints and rate limiting are pending.
- Audit rows are application-immutable, not cryptographically tamper-evident.
- Permanent purge, vacuum, backup, and migration recovery policy are pending.

## Documentation maintenance

README changes are part of the definition of done. Any change to installation, configuration, API behavior, MCP tools, security guarantees, test commands, project layout, or known limitations must update this file in the same commit.
