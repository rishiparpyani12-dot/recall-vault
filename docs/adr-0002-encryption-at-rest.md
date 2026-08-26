# ADR 0002: Windows-first encryption at rest

- Status: Accepted
- Date: 2026-08-24
- Linear: MEM-6

## Context

Recall Vault originally stored data in ordinary SQLite. This ADR records the decision that led to the implemented Windows x64 encrypted runtime.

The application uses `Microsoft.EntityFrameworkCore.Sqlite`, `Microsoft.Data.Sqlite`, Dapper, SQLite migrations, and FTS5. `Microsoft.Data.Sqlite` does not provide encryption itself; it can send a key to an encryption-capable native SQLite library. The previously convenient no-cost `SQLitePCLRaw.bundle_e_sqlcipher` distribution is deprecated as of SQLitePCLRaw 3.0. Zetetic distributes SQLCipher Community Edition under a BSD-style license, but does not provide official Community Edition .NET packages. A public Recall Vault release therefore needs to own a reproducible native build and update pipeline.

Supporting every desktop OS in the first encryption release would also require independently designed and tested credential-vault backends and packaging paths. The current development and operator workflow is Windows and PowerShell based.

## Decision

### Platform

The first encrypted release will support **Windows only**. Cross-platform support remains a goal, but macOS Keychain and Linux Secret Service backends will be separate follow-up work and will not be simulated with plaintext files or environment variables.

### SQLCipher distribution

Recall Vault will use **SQLCipher Community Edition built reproducibly from a pinned, verified upstream release** and loaded through SQLitePCLRaw by `Microsoft.Data.Sqlite`. Recall Vault will publish the native-build recipe, source revision, patches, checksums, software bill of materials, and required license notices with each binary release.

We reject the deprecated `SQLitePCLRaw.bundle_e_sqlcipher` package and will not redistribute or imply support from Zetetic for a Recall Vault-built binary. Recall Vault accepts responsibility for native build reproducibility, platform hardening, upstream security monitoring, and timely SQLite/SQLCipher updates. Official Zetetic Commercial or Enterprise packages remain a possible future option for organizations that want vendor support, but they are not a prerequisite for the public project.

The provider integration must:

- initialize the SQLCipher-capable SQLitePCLRaw provider before any connection opens;
- supply the database key through `SqliteConnectionStringBuilder.Password`, never through checked-in configuration;
- verify encryption support at startup instead of assuming that a password implies encryption;
- preserve EF Core migrations, Dapper access, and FTS5;
- verify the pinned upstream source and produced native artifact with cryptographic hashes;
- fail closed if the provider, key, or encrypted database cannot be opened.

### Key protection

The database key will be a randomly generated 256-bit value stored as a **Windows Credential Manager generic credential**, scoped to the interactive user and identified by a stable Recall Vault target name. Recall Vault will access Credential Manager through a narrow application-owned interface so later platform backends do not affect database code.

The key must never be accepted from ordinary configuration, command-line arguments, or environment variables in production. Tests may inject an in-memory key provider through dependency injection.

### Existing databases and recovery

Encryption migration must not silently replace an existing plaintext database without a recoverable intermediate. The implemented flow validates and checkpoints the plaintext source, creates or verifies a retained plaintext backup, exports to a separate SQLCipher candidate, validates it, and atomically replaces the live database. Interrupted candidates are rebuilt; mismatched backups and validation failures stop without replacement.

Missing credentials, a wrong key, an unavailable credential service, an unsupported native provider, and interrupted migration are fatal startup errors. None may fall back to plaintext or create an empty replacement vault.

## Consequences

- Encryption implementation is gated on a reproducible Windows native build and packaging pipeline.
- The first secure package can be delivered and tested deeply on one OS.
- Cross-platform clients can still speak HTTP or MCP to a Windows-hosted Recall Vault, but running the encrypted service itself on macOS or Linux is deferred.
- Public binary distributions must reproduce the SQLCipher Community Edition copyright, license conditions, disclaimer, and applicable dependency notices in user-accessible materials.
- Provider integration, credential storage, migration behavior, and encryption-at-rest tests are complete for Windows x64. The preview warning remains because independent encrypted backup/key recovery, rotation, and cross-platform backends are not implemented.

## Implementation status

Implemented and tested:

- pinned SQLCipher Community Edition native build and provider verification;
- encrypted creation, FTS5 access, and correct-key restart;
- random 256-bit Windows Credential Manager key creation and reuse;
- backup-first legacy plaintext migration and interrupted-candidate recovery;
- missing, malformed, wrong-key, corrupted-database, and unkeyed-read fail-closed behavior;
- automated checks that the database key is absent from config, logs, and data-directory files.

The operational limits and recovery procedure are maintained in the [security and operations runbook](security-operations.md).

## References

- [Microsoft.Data.Sqlite encryption guidance](https://learn.microsoft.com/dotnet/standard/data/sqlite/encryption)
- [Microsoft.Data.Sqlite connection-string behavior](https://learn.microsoft.com/dotnet/standard/data/sqlite/connection-strings)
- [SQLitePCLRaw 3.0 encryption distribution changes](https://github.com/ericsink/SQLitePCL.raw/blob/main/v3.md)
- [SQLitePCLRaw encryption options](https://github.com/ericsink/SQLitePCL.raw/wiki/SQLite-encryption-options-for-use-with-SQLitePCLRaw)
- [SQLCipher Community Edition and redistribution guidance](https://www.zetetic.net/sqlcipher/community/)
- [SQLCipher license information](https://www.zetetic.net/sqlcipher/license/)
