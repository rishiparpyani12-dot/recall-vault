# Remote MCP preview

Recall Vault includes an opt-in Streamable HTTP MCP host for private testing. It is disabled by default, uses a separate bearer token at the remote boundary, validates `Host` and `Origin`, limits MCP request bodies to 1 MiB, and allows 60 requests per minute.

This preview is not production-ready. Use synthetic data only. The API database remains ordinary SQLite until MEM-7 through MEM-10 are complete, and the static preview bearer token is not a replacement for OAuth. Claude's hosted custom-connector flow requires either an authless or OAuth-compatible remote server, so do not expose this static-token preview publicly to make Claude connect.

## Configuration

The HTTP host reads these settings from environment variables:

| Variable | Purpose |
| --- | --- |
| `RecallPreview__Enabled` | Must be `true`; otherwise startup fails. |
| `RecallPreview__Token` | Random remote bearer token of at least 32 bytes. |
| `RecallPreview__AllowedOrigins__0` | Exact trusted browser origin, without a trailing slash. Add numbered entries for more origins. |
| `RecallPreview__AllowedHosts__0` | Exact public host name. Add numbered entries for local probes or additional hosts. |
| `Recall__ApiUrl` | Internal Recall API URL. |
| `RECALL_CLIENT_ID` | Registered Recall API client UUID. |
| `RECALL_CLIENT_TOKEN` | Token returned once when that client is registered. |

Keep all token values in the deployment platform's secret store. Never commit `.env.preview`.

## Local container smoke test

1. Copy `deploy/preview/.env.preview.example` to `deploy/preview/.env.preview` and replace every placeholder with test-only values.
2. Start only the API, register a least-privilege preview client through the existing bootstrap flow, and place the returned client ID/token in `.env.preview`.
3. Start the stack behind a TLS reverse proxy or private tunnel:

   ```powershell
   docker compose --env-file deploy/preview/.env.preview -f deploy/preview/compose.yml up --build
   ```

The compose file binds the MCP port to `127.0.0.1:8080`; it is not exposed to the LAN or internet. Point a TLS tunnel or reverse proxy at that loopback address. The remote MCP URL is `https://your-preview-host.example/mcp`.

## Client compatibility

- The OpenAI Responses API can send the preview bearer token as an MCP request header, making it suitable for a controlled API-level smoke test.
- ChatGPT workspace apps and Claude custom connectors should use OAuth for an internet-hosted preview. OAuth protected-resource metadata and tenant isolation are the next gate before UI-based testing.
- The existing `Recall.Mcp` executable remains the supported local `stdio` path for Claude Desktop and other local MCP clients.

## Promotion gates

Do not remove the test-data-only label until encryption at rest, OS-protected keys, fail-closed migration/recovery, encryption tests, OAuth, tenant isolation, TLS, backups, and operational documentation are complete.
