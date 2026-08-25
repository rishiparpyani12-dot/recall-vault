# ADR 0002: Windows-first encryption at rest

- Status: Accepted
- Date: 2026-08-24
- Linear: MEM-6

## Context

Recall Vault currently stores data in ordinary SQLite. Milestone 1 deliberately does not claim encryption at rest, and valuable secrets must not be stored until encryption and key-handling tests are complete.

The application uses `Microsoft.EntityFrameworkCore.Sqlite`, `Microsoft.Data.Sqlite`, Dapper, SQLite migrations, and FTS5. `Microsoft.Data.Sqlite` does not provide encryption itself; it can send a key to an encryption-capable native SQLite library. The previously convenient no-cost `SQLitePCLRaw.bundle_e_sqlcipher` distribution is deprecated as of SQLitePCLRaw 3.0. Current supported SQLCipher builds are distributed commercially by Zetetic, while using the open-source SQLCipher source requires Recall Vault to own the native build and update pipeline.

Supporting every desktop OS in the first encryption release would also require independently designed and tested credential-vault backends and packaging paths. The current development and operator workflow is Windows and PowerShell based.

## Decision

### Platform

The first encrypted release will support **Windows only**. Cross-platform support remains a goal, but macOS Keychain and Linux Secret Service backends will be separate follow-up work and will not be simulated with plaintext files or environment variables.

### SQLCipher distribution

Recall Vault will use a **supported Zetetic SQLCipher native package compatible with SQLitePCLRaw and `Microsoft.Data.Sqlite`**. Before implementation begins, the maintainer must acquire the appropriate package/feed access and confirm redistribution rights for the intended Recall Vault distribution.

We reject the deprecated `SQLitePCLRaw.bundle_e_sqlcipher` package. We also defer self-building SQLCipher because doing so would make Recall Vault responsible for reproducible native builds, platform hardening, CVE monitoring, and rapid SQLite/SQLCipher updates before the application packaging pipeline exists.

The provider integration must:

- initialize the SQLCipher-capable SQLitePCLRaw provider before any connection opens;
- supply the database key through `SqliteConnectionStringBuilder.Password`, never through checked-in configuration;
- verify encryption support at startup instead of assuming that a password implies encryption;
- preserve EF Core migrations, Dapper access, and FTS5;
- fail closed if the provider, key, or encrypted database cannot be opened.

### Key protection

The database key will be a randomly generated 256-bit value stored as a **Windows Credential Manager generic credential**, scoped to the interactive user and identified by a stable Recall Vault target name. Recall Vault will access Credential Manager through a narrow application-owned interface so later platform backends do not affect database code.

The key must never be accepted from ordinary configuration, command-line arguments, or environment variables in production. Tests may inject an in-memory key provider through dependency injection.

### Existing databases and recovery

Encryption work must not silently replace or modify an existing plaintext database. A later migration ticket will define an explicit, backup-first conversion using SQLCipher export semantics. Until that migration is implemented, startup must detect an existing plaintext vault and stop with actionable guidance.

Missing credentials, a wrong key, an unavailable credential service, an unsupported native provider, and interrupted migration are fatal startup errors. None may fall back to plaintext or create an empty replacement vault.

## Consequences

- Encryption implementation is blocked on supported SQLCipher package/feed access and redistribution review.
- The first secure package can be delivered and tested deeply on one OS.
- Cross-platform clients can still speak HTTP or MCP to a Windows-hosted Recall Vault, but running the encrypted service itself on macOS or Linux is deferred.
- The current security warning remains in force until provider integration, credential storage, migration behavior, and encryption-at-rest tests are complete.

## References

- [Microsoft.Data.Sqlite encryption guidance](https://learn.microsoft.com/dotnet/standard/data/sqlite/encryption)
- [Microsoft.Data.Sqlite connection-string behavior](https://learn.microsoft.com/dotnet/standard/data/sqlite/connection-strings)
- [SQLitePCLRaw 3.0 encryption distribution changes](https://github.com/ericsink/SQLitePCL.raw/blob/main/v3.md)
- [SQLitePCLRaw encryption options](https://github.com/ericsink/SQLitePCL.raw/wiki/SQLite-encryption-options-for-use-with-SQLitePCLRaw)

