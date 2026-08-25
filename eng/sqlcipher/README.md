# SQLCipher native build inputs

Recall Vault pins SQLCipher Community Edition in `upstream.json`. Build automation must fetch the tag from the recorded repository and reject it unless the resolved commit exactly matches the recorded full hash.

The first target is `win-x64`. The native build must use MSVC, OpenSSL 3, and the upstream `Makefile.msc`, with at least these definitions:

- `SQLITE_HAS_CODEC`
- `SQLCIPHER_CRYPTO_OPENSSL`
- `SQLITE_EXTRA_INIT=sqlcipher_extra_init`
- `SQLITE_EXTRA_SHUTDOWN=sqlcipher_extra_shutdown`
- `SQLITE_THREADSAFE=1`
- `SQLITE_TEMP_STORE=2`
- `SQLITE_ENABLE_FTS5`

Generated source, native binaries, symbols, import libraries, OpenSSL runtime files, checksums, and SBOM output belong in an ignored artifact directory and must not be committed directly. Release packaging will publish them as derived artifacts with build provenance and third-party notices.

The build and smoke test are not implemented yet. Until they are, Recall Vault continues to use ordinary SQLite and must not claim encryption at rest.
