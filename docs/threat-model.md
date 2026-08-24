# Threat model (Milestone 1)

Trust boundary: AI output, MCP arguments, stored memory content, and every local process are untrusted. Only the Recall service may access the database.

| Threat | Current control | Remaining work |
|---|---|---|
| Local process calls API | Loopback bind, per-client bearer token | OS ACL/IPC option, rate limits, replay protection |
| Over-permissioned client | Exact category and sensitivity ceiling, deny by default, audit | Permission review UI and time-bound grants |
| Database theft | None in ordinary SQLite milestone | Integrate and test SQLCipher; protect key in OS vault |
| Secrets/content in logs | Structured operational logs omit bodies and auth headers | Automated log redaction tests |
| Prompt injection | Authorization is outside model control; content treated as data | Client-side rendering and instruction-boundary guidance |
| Malicious content/input | Length limits, typed parsing, parameterized SQL, quoted FTS terms | Broader fuzzing and output encoding in UI |
| Excessive disclosure | Search cap 50, exact permissions | Response byte budget and field-level disclosure |
| Deleted memory searchable | Soft-delete status plus FTS triggers | Controlled purge and vacuum policy |

Audit records have no update/delete application operation. Database-owner compromise can still alter them; cryptographic tamper evidence is future work.
