# ADR 0003: Portable encrypted recovery backups

- Status: Accepted for preview
- Date: 2026-09-07

## Context

SQLCipher protects the active database, but its random key lives in the current Windows user's Credential Manager. A database copy alone does not survive credential or device loss. Recovery must therefore export both a consistent encrypted database snapshot and its key without exposing either in files, arguments, environment variables, logs, or Git.

## Decision

`Recall.Worker` provides stopped-service `backup` and `restore` commands. Passwords are read from an interactive, non-echoing console and are never accepted as command-line or environment values.

The version-1 `.recall-backup` envelope contains:

- magic and fixed format version;
- PBKDF2-HMAC-SHA-256 iteration count (600,000) and a random 128-bit salt;
- a random 96-bit AES-GCM nonce;
- authenticated payload length;
- AES-256-GCM ciphertext and a 128-bit authentication tag.

The encrypted payload is the 256-bit SQLCipher key followed by a consistent SQLCipher snapshot created with `SqliteConnection.BackupDatabase`. The entire header is authenticated as associated data. Version 1 is capped at 512 MiB and processed in memory; a future streaming format must use independently authenticated chunks and a new version.

Restore decrypts into a separate candidate, validates SQLCipher and SQLite integrity with the embedded key, and refuses active WAL/SHM sidecars. Restoring over an existing vault first creates a password-encrypted `.pre-restore` recovery package. It then replaces the database and Credential Manager key as one guarded operation, rolling both back on ordinary failures. Destinations are never silently overwritten.

## Security consequences

- Recovery strength is bounded by the user's password. The UI must encourage a unique high-entropy recovery passphrase and warn that it cannot be recovered by Recall Vault.
- Backup packages contain everything needed to decrypt the vault once the password is known. Store them offline and separately from the password.
- AES-GCM nonce uniqueness is obtained through fresh cryptographic randomness per package; packages are immutable and never rewritten in place.
- PBKDF2 parameters are recorded but version 1 accepts only the fixed reviewed value, preventing an attacker-controlled low-cost downgrade.
- Process-memory inspection, a compromised unlocked account, keyloggers, and malicious replacement binaries remain outside this control.
- File/database replacement and Credential Manager update cannot be made transactionally atomic across an OS crash. The input and pre-restore packages remain recovery anchors; crash-point testing and a durable restore journal are still required before a stable release.

## References

- [NIST SP 800-132](https://csrc.nist.gov/pubs/sp/800/132/final)
- [Microsoft .NET PBKDF2 API](https://learn.microsoft.com/dotnet/api/system.security.cryptography.rfc2898derivebytes.pbkdf2)
- [Microsoft.Data.Sqlite online backup](https://learn.microsoft.com/dotnet/standard/data/sqlite/backup)
