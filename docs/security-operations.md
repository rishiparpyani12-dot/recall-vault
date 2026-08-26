# Security and operations runbook

This runbook describes the Windows x64 encrypted runtime implemented in Milestone 1. It does not apply to the paused Linux container preview.

## Security boundary

- `Recall.Api` is the only process that opens `recall.db`.
- SQLCipher Community Edition encrypts the live database. Startup verifies the SQLCipher provider before applying EF Core migrations.
- A random 256-bit database key is stored as the current Windows user's generic credential named `RecallVault/DatabaseKey/v1`.
- Production does not accept the database key through configuration, environment variables, command-line arguments, or plaintext files.
- Client bearer tokens and the bootstrap token are separate credentials. Protecting the database key does not compensate for a leaked client token.

Encryption at rest protects a copied database from an attacker who does not also obtain the Windows credential. It does not protect memory content from Recall clients that have permission to read it, malware running as the same user, a compromised unlocked account, API responses, process memory, or screenshots. Audit rows are not cryptographically tamper-evident.

## Files and credentials

The default data directory is `%LOCALAPPDATA%\RecallVault` and contains the encrypted `recall.db` and operational logs. A legacy migration can also create:

- `recall.db.plaintext-backup`: the complete legacy plaintext vault retained for rollback;
- `recall.db.migration`: a temporary encrypted export candidate, normally removed or atomically promoted;
- SQLite `-wal` and `-shm` sidecars while a database is active.

Treat the entire data directory as sensitive. The plaintext backup has no encryption-at-rest protection from Recall Vault. Never commit, upload, email, or attach any database, sidecar, log, credential export, or real memory content to an issue.

## Cold backup

Recall Vault does not yet provide an encrypted backup command or a supported database-key export. A database-only copy is therefore not an independent disaster-recovery backup: it remains usable only while the matching Windows credential is available to the same user context.

For a same-machine rollback copy:

1. Stop `Recall.Api`, the worker, and every Recall MCP process.
2. Confirm no Recall process is running and no database file is open.
3. Copy the complete data directory, including any `-wal` and `-shm` files, to storage protected at least as strongly as the Windows account.
4. Record the application version, commit or release, Windows account, backup time, and whether `recall.db.plaintext-backup` exists. Do not record secret values.
5. Restart Recall Vault and verify `/health`, a permitted memory read, and a search.

Do not use a live filesystem copy as a consistency guarantee. Automated off-machine backup and credential recovery remain release blockers for valuable data.

## Restore on the same Windows user profile

Use this only when the matching `RecallVault/DatabaseKey/v1` credential is still present.

1. Stop all Recall processes.
2. Preserve the current data directory separately; do not overwrite the only copy.
3. Restore the complete cold-copy data directory to its original path.
4. Start Recall Vault. A missing/wrong key, corrupt database, or unsupported provider must stop startup.
5. Verify `/health`, representative records, search, permissions, and access history before deleting any preserved copy.

If startup fails, stop. Do not delete `recall.db`, create an empty replacement, change the credential, or rename an unknown database into place.

## Legacy plaintext migration and rollback

On seeing a valid plaintext SQLite header, Recall Vault integrity-checks the source, checkpoints committed WAL data, creates or verifies `recall.db.plaintext-backup`, exports to a separate keyed SQLCipher candidate, validates it, and atomically replaces the live file. An interrupted candidate is rebuilt from the source. A mismatched backup stops migration without replacement.

After a successful migration:

1. Verify `/health`, record counts or representative records, FTS search, permissions, and access history.
2. Keep the service stopped while moving the plaintext backup to protected offline storage or securely deleting it under the applicable retention policy.
3. Do not claim that deleting the file guarantees forensic erasure on SSDs, snapshots, synchronized folders, or backups.

To roll back while the plaintext backup is retained, stop all Recall processes, preserve the encrypted vault, restore a copy of `recall.db.plaintext-backup` as `recall.db`, and restart. Recall Vault will migrate that plaintext copy again using the existing protected key. Never move the only plaintext backup or overwrite the only encrypted copy.

## Failure decisions

| Symptom | Meaning | Safe action |
| --- | --- | --- |
| Protected credential missing or malformed with an encrypted database | The vault cannot be decrypted | Stop; preserve the database and investigate the Windows profile/credential. No automated recovery exists. |
| Wrong key or `file is not a database` during startup | Credential and database do not match, or the database is corrupt | Stop; preserve both. Restore only a known matching cold copy under the original user profile. |
| Plaintext backup does not match the source | A previous, foreign, or partial backup occupies the rollback path | Stop; preserve both files and resolve provenance manually. Do not delete either during triage. |
| `.migration` remains after interruption | An export did not complete | Preserve evidence if investigating; otherwise the next startup rebuilds it only when the plaintext source and backup validate. |
| SQLCipher provider verification fails | Native binary is missing, incompatible, or not the verified Community build | Stop; reinstall from a verified release. Never substitute ordinary SQLite. |
| Windows Credential Manager unavailable | Key access cannot be trusted | Stop and repair the user profile or credential service. Do not add a config or environment fallback. |

## Incident response

1. Contain: stop Recall services and revoke network or tunnel exposure. If a client token may be compromised, stop the API because token revocation is not implemented yet.
2. Preserve: make read-only copies of the data directory and relevant logs. Record times, release or commit, OS account, observed errors, and affected client IDs without copying secret values into tickets.
3. Classify: determine whether the incident involves a client or bootstrap token, database key, plaintext migration backup, encrypted database, host account, or native dependency.
4. Recover: restore only from a verified cold copy with its matching Windows credential. If the database key is lost and no plaintext migration backup exists, the encrypted vault is currently unrecoverable.
5. Validate: verify health, representative reads and searches, permissions, and access history before reopening client access.
6. Learn: document scope and timeline, rotate externally managed bootstrap or client credentials where possible, preserve evidence, and open remediation work. Do not publish memory content, keys, tokens, databases, or raw sensitive logs.

## Release gates

The implemented Windows runtime has SQLCipher integration, OS-protected key creation and reuse, backup-first plaintext migration, fail-closed behavior, and automated encryption tests. It is still a preview for synthetic or replaceable data until all of the following are demonstrated:

- independent encrypted backup and restore, including the database key;
- a tested key-loss and device-loss recovery path;
- client and bootstrap token rotation and revocation;
- operational monitoring and restore drills;
- security review of packaging, installer and update behavior, and hosted OAuth/TLS deployment where applicable.

Do not describe the current build as guaranteeing secure deletion, protection from same-user compromise, tamper-proof audit history, cross-platform encrypted hosting, or disaster recovery.
