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

Run the Windows build from a PowerShell prompt:

```powershell
./eng/sqlcipher/build-windows.ps1
```

The script locates Visual Studio Build Tools and vcpkg, verifies the exact SQLCipher tag commit before checkout, restores pinned OpenSSL sources through vcpkg manifest mode, builds with MSVC, and writes the DLL, import library, headers, OpenSSL runtime, and `SHA256SUMS` to the ignored artifact directory. SQLCipher's Windows source-generation helpers cannot run from a path containing spaces, so compilation uses a validated temporary build directory and only finalized outputs are copied into the repository artifact tree.

The build finishes by running a native interop smoke test against the packaged files. It verifies the exact SQLCipher version, FTS5 indexing and search, a non-plaintext database header, and rejection of an unkeyed schema read.

SBOM generation is not implemented yet. Until the native provider is integrated into the application and the encryption-at-rest suite passes, Recall Vault continues to use ordinary SQLite and must not claim encryption at rest.
