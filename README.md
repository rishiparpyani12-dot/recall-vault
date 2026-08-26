# Recall Vault

Recall Vault is a local-first, user-controlled memory service for multiple AI clients. It provides one permissioned memory vault for applications such as Codex, Claude Desktop, Cursor, ChatGPT, Claude, and Gemini. This repository currently contains the Milestone 1 backend, local HTTP API, and stdio MCP adapter.

> Security status: Windows vaults use the pinned SQLCipher Community Edition build and a random 256-bit key stored in Windows Credential Manager. Legacy plaintext databases are migrated through a validated backup-first process, and automated tests cover missing, wrong, malformed, and corrupted-key/database failure paths. Key-loss recovery remains unfinished, so do not store valuable secrets yet.

## Prerequisites

- Windows x64
- .NET SDK 10
- Visual Studio C++ Build Tools with vcpkg
- PowerShell

No manual database setup is needed. The service migrates a database in `%LOCALAPPDATA%\RecallVault` on first run.

When the service is running:

- Health check: `http://127.0.0.1:5278/health`
- Swagger UI: `http://127.0.0.1:5278/swagger`
- OpenAPI document: `http://127.0.0.1:5278/swagger/v1/swagger.json`

## Run locally

```powershell
cd recall-vault
./eng/sqlcipher/build-windows.ps1
$env:RECALL_BOOTSTRAP_TOKEN = '<choose-a-long-random-bootstrap-token>'
dotnet run --project src/Recall.Api
```

The API refuses to start without the verified SQLCipher provider. On first run it creates a random database key in Windows Credential Manager under `RecallVault/DatabaseKey/v1`. The key is never accepted from application configuration or environment variables. If an encrypted or unrecognized database already exists and that credential is missing or malformed, startup fails without generating or storing a replacement key.

### Legacy plaintext migration

At startup, a database with SQLite's plaintext header is integrity-checked, any committed WAL data is checkpointed into the main file, and obsolete WAL sidecars are removed. Recall Vault then creates `recall.db.plaintext-backup` using write-through I/O, verifies any pre-existing backup matches the source byte-for-byte, exports into a separate keyed SQLCipher candidate, validates that candidate, and atomically replaces `recall.db`. A partial `.migration` candidate from an interrupted attempt is discarded and rebuilt from the verified plaintext source and matching backup.

The plaintext backup is deliberately retained for rollback and contains all legacy memory content. After starting the migrated vault and verifying the expected records, move that backup to protected offline storage or securely delete it according to your retention policy. Never upload or commit it. If the backup does not match the source, or either database fails validation, startup stops and leaves the source untouched; resolve the files manually rather than renaming or deleting the active vault. Losing the Windows credential after replacement is not recoverable yet.

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
./eng/sqlcipher/build-windows.ps1
dotnet test RecallVault.slnx
```

The test suite verifies encrypted creation and restart, protected-key creation and reuse, missing/malformed/wrong-key failures, corrupted-database refusal, credential-store write failures, backup-first plaintext migration, interrupted-candidate recovery, mismatched-backup refusal, byte-for-byte vault immutability after failed startup, a non-plaintext database header, absence of memory markers and database keys from data-directory files, rejection of unkeyed reads, FTS5 behavior, authorization, and the authenticated HTTP/MCP workflows.

## Repository layout

- `src/Recall.Domain`: storage-independent entities and enums
- `src/Recall.Application`: authorization-aware memory operations
- `src/Recall.Infrastructure`: EF Core persistence, migration, and Dapper/FTS5 search
- `src/Recall.Api`: authenticated loopback HTTP API and Swagger UI
- `src/Recall.Mcp`: official C# MCP SDK stdio adapter
- `tests`: unit, SQLite integration, and real-process MCP end-to-end tests
- `docs`: architecture decision, implementation plan, and threat model

Architecture, security tradeoffs, operations, and planned work are in [ADR 0001](docs/adr-0001-milestone-1-architecture.md), [ADR 0002](docs/adr-0002-encryption-at-rest.md), the [threat model](docs/threat-model.md), the [security and operations runbook](docs/security-operations.md), and the [Milestone 1 plan](docs/milestone-1-plan.md).

CI, dry-run packaging, and GitHub Release instructions are in the [release guide](docs/releases.md).

The opt-in Streamable HTTP MCP host is documented in the [remote preview guide](docs/remote-preview.md). The existing Linux container preview is paused because the encrypted API runtime is Windows-only; do not deploy it with a plaintext fallback.

The first encryption-at-rest runtime is Windows-only. It uses reproducible builds of SQLCipher Community Edition and Windows Credential Manager for the database key. Public binary releases include build provenance, checksums, an SBOM, and required third-party notices. [ADR 0002](docs/adr-0002-encryption-at-rest.md) records the decision and remaining recovery prerequisites.

## Current limitations

- Lost-key recovery, key rotation, and encrypted backup/restore tooling are pending.
- The encrypted API runtime currently supports Windows x64 only; the Linux container preview cannot host it.
- No desktop UI, installer, browser extension, or cloud synchronization.
- Client administration, token rotation, and revocation tools are not implemented.
- Registration is an operator API guarded by a bootstrap secret; token rotation/revocation endpoints and rate limiting are pending.
- Audit rows are application-immutable, not cryptographically tamper-evident.
- Permanent purge, vacuum, encrypted backup tooling, and automated key recovery are pending.

## Documentation maintenance

README changes are part of the definition of done. Any change to installation, configuration, API behavior, MCP tools, security guarantees, test commands, project layout, or known limitations must update this file in the same commit.
