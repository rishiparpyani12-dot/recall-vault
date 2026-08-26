# ADR 0001: Milestone 1 architecture

Status: accepted for Milestone 1 (2026-08-24)

## Decision

- **Process boundaries:** `Recall.Api` is the only database owner. Each AI launches its own `Recall.Mcp` child process. The adapter calls the service and never opens SQLite.
- **MCP transport:** stdio via the official Tier 1 C# SDK (`ModelContextProtocol` 2.2.0). Logs go to stderr because stdout is the protocol channel.
- **Local API:** versioned JSON endpoints bind to `127.0.0.1` only. No CORS policy is enabled. Loopback is a network boundary, not an authentication boundary.
- **Database and encryption:** EF Core owns schema migrations; Dapper performs parameterized FTS5 queries. The Windows x64 runtime uses the pinned Recall-built SQLCipher Community Edition provider. A random 256-bit database key is read from Windows Credential Manager, and startup fails closed when provider, key, or database validation fails. Legacy plaintext files use the backup-first migration defined in [ADR 0002](adr-0002-encryption-at-rest.md).
- **Authentication:** registered clients receive a random 256-bit bearer token once. Only its SHA-256 digest is stored. Registration requires an operator-supplied `RECALL_BOOTSTRAP_TOKEN`. A future installer will store these secrets in the operating-system credential vault and may add request replay protection.
- **Authorization:** deny by default. The requesting client must have an exact category permission for the operation and the memory sensitivity must be at or below its ceiling. Updates require access to both old and new category/sensitivity. Filtering happens in the application service and all relevant allow/deny attempts create append-only audit rows.
- **Search:** SQLite FTS5 with Unicode tokenization and quoted token conjunction. SQL is parameterized, results are capped, then authorization and expiration are applied. Soft-deleted rows are removed from the FTS index by triggers.

## Consequences

The shared service supports multiple clients and central audit policy, while compromise of an MCP adapter does not directly grant database-file access. Loopback bearer authentication reduces—but does not eliminate—risk from another process running as the same OS user. SQLCipher integration and database-key storage are complete for Windows x64. Independent backup/key recovery, client-token rotation and revocation, replay resistance, and cross-platform key backends remain security work.

## Primary references

- [Official C# SDK getting started](https://github.com/modelcontextprotocol/csharp-sdk/blob/main/docs/concepts/getting-started.md)
- [Official transport documentation](https://github.com/modelcontextprotocol/csharp-sdk/blob/main/docs/concepts/transports/transports.md)
- [Official MCP SDK tiers](https://github.com/modelcontextprotocol/modelcontextprotocol/blob/main/docs/docs/2026-07-28/sdk.mdx)
