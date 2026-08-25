# Milestone 1 plan

1. Establish solution conventions and domain model. **Done**
2. Add EF Core SQLite migrations, FTS5, and automatic first-run creation. **Done**
3. Implement remember, search, get, update, and soft-forget with authorization and audit records. **Done**
4. Add authenticated loopback API and client registration. **Done**
5. Expose five operations through the official stdio MCP SDK. **Done**
6. Prove permission denial and deleted-memory search behavior in tests. **Done**
7. Add service/MCP end-to-end protocol tests, packaging, SQLCipher, secret-vault integration, audit/list/permission tools. **In progress**

The encryption architecture decision is recorded in [ADR 0002](adr-0002-encryption-at-rest.md): Windows is the first supported host, keys will be held in Windows Credential Manager, and Recall Vault will use supported Zetetic SQLCipher builds after package/feed access and redistribution rights are confirmed. Encryption is not implemented yet, so the ordinary-SQLite security warning remains in force.
