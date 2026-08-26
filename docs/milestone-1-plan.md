# Milestone 1 plan

1. Establish solution conventions and domain model. **Done**
2. Add EF Core SQLite migrations, FTS5, and automatic first-run creation. **Done**
3. Implement remember, search, get, update, and soft-forget with authorization and audit records. **Done**
4. Add authenticated loopback API and client registration. **Done**
5. Expose five operations through the official stdio MCP SDK. **Done**
6. Prove permission denial and deleted-memory search behavior in tests. **Done**
7. Add service/MCP end-to-end protocol tests, packaging, SQLCipher, secret-vault integration, audit/list/permission tools. **Done**
8. Add backup-first plaintext migration and fail-closed encryption/key tests. **Done**
9. Publish accurate security, recovery, and incident-response documentation. **Done**
10. Add independent encrypted backup/key recovery, credential rotation/revocation, monitoring, and restore drills. **Future milestone**

The encryption architecture decision is recorded in [ADR 0002](adr-0002-encryption-at-rest.md): Windows is the first supported host, keys are held in Windows Credential Manager, and public releases use reproducible builds of SQLCipher Community Edition with checksums, an SBOM, and required notices. The encrypted runtime, migration, and failure matrix are implemented. The build remains a preview for synthetic or replaceable data because independent key/backup recovery and credential lifecycle operations are not implemented; see the [operations runbook](security-operations.md).
