# Milestone 1 plan

1. Establish solution conventions and domain model. **Done**
2. Add EF Core SQLite migrations, FTS5, and automatic first-run creation. **Done**
3. Implement remember, search, get, update, and soft-forget with authorization and audit records. **Done**
4. Add authenticated loopback API and client registration. **Done**
5. Expose five operations through the official stdio MCP SDK. **Done**
6. Prove permission denial and deleted-memory search behavior in tests. **Done**
7. Add service/MCP end-to-end protocol tests, packaging, SQLCipher, secret-vault integration, audit/list/permission tools. **Next**

Input is not required for the current backend design. Before consumer packaging, choose the initial OS priority (Windows-only first or cross-platform) and the preferred SQLCipher distribution/licensing approach.
