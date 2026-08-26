# Threat model (Milestone 1)

Trust boundary: AI output, MCP arguments, stored memory content, and every local process are untrusted. Only the Recall service may access the database.

| Threat | Current control | Remaining work |
|---|---|---|
| Local process calls API | Loopback bind, per-client bearer token | OS ACL/IPC option, rate limits, replay protection |
| Over-permissioned client | Exact category and sensitivity ceiling, deny by default, audit | Permission review UI and time-bound grants |
| Database theft | SQLCipher Community Edition; random 256-bit key in the current user's Windows Credential Manager; unkeyed-read tests | Independent encrypted backup/key recovery; packaging review; macOS/Linux backends |
| Missing, wrong, or malformed database key | Startup fails closed; encrypted file remains byte-for-byte unchanged; no plaintext/config fallback | Supported key/device-loss recovery and key rotation |
| Legacy plaintext database | Integrity check, WAL checkpoint, verified plaintext backup, separate encrypted export, atomic replacement | Operator removal of retained plaintext copies; secure-erasure claims are explicitly excluded |
| Secrets/content in logs or config | Structured logs omit bodies/auth headers; automated tests prove the database key is absent from config, logs, and data-directory files | Broader log-redaction tests for future endpoints and hosted components |
| Prompt injection | Authorization is outside model control; content treated as data | Client-side rendering and instruction-boundary guidance |
| Malicious content/input | Length limits, typed parsing, parameterized SQL, quoted FTS terms | Broader fuzzing and output encoding in UI |
| Excessive disclosure | Search cap 50, exact permissions | Response byte budget and field-level disclosure |
| Deleted memory searchable | Soft-delete status plus FTS triggers | Controlled purge and vacuum policy |

Audit records have no update/delete application operation. Database-owner compromise can still alter them; cryptographic tamper evidence is future work.

Encryption does not defend against malware or another process running as the same unlocked Windows user, an authorized client exceeding the operator's intended trust, process-memory inspection, API-response capture, or loss of both the host and its protected credential. See the [security and operations runbook](security-operations.md) for backup boundaries, recovery, and incident response.
