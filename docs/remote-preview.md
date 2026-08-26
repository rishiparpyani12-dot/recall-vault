# Remote MCP preview

Recall Vault includes an opt-in Streamable HTTP MCP host for private testing. It is disabled by default, validates `Host` and `Origin`, limits MCP request bodies to 1 MiB, and allows 60 requests per minute. It supports a static token for local smoke tests and OIDC/JWT bearer validation for hosted connectors.

This preview is not production-ready. Use synthetic data only. The encrypted API runtime now requires Windows x64, so the existing Linux Compose deployment is paused and must not be made functional by restoring ordinary SQLite. A hosted preview needs a Windows container/VM path or a future reviewed cross-platform key backend. Never expose `StaticToken` mode publicly merely to make a client connect.

## Configuration

The HTTP host reads these settings from environment variables:

| Variable | Purpose |
| --- | --- |
| `RecallPreview__Enabled` | Must be `true`; otherwise startup fails. |
| `RecallPreview__AuthMode` | `StaticToken` for local smoke tests or `OAuth` for hosted connectors. |
| `RecallPreview__Token` | Random remote bearer token of at least 32 bytes; required only in `StaticToken` mode. |
| `RecallPreview__AllowedOrigins__0` | Exact trusted browser origin, without a trailing slash. Add numbered entries for more origins. |
| `RecallPreview__AllowedHosts__0` | Exact public host name. Add numbered entries for local probes or additional hosts. |
| `Recall__ApiUrl` | Internal Recall API URL. |
| `RECALL_CLIENT_ID` | Registered Recall API client UUID. |
| `RECALL_CLIENT_TOKEN` | Token returned once when that client is registered. |

OAuth mode additionally requires:

| Variable | Purpose |
| --- | --- |
| `RecallPreview__PublicBaseUrl` | Public HTTPS origin of the preview, such as `https://preview.example.com`. |
| `RecallPreview__OAuth__Authority` | HTTPS OIDC issuer/authorization server. |
| `RecallPreview__OAuth__Audience` | Audience the provider places in Recall MCP access tokens. |
| `RecallPreview__OAuth__RequiredScope` | Required access-token scope, such as `recall.mcp`. |
| `RecallPreview__Tenants__0__Subject` | Exact OIDC `sub` claim for the first tester. |
| `RecallPreview__Tenants__0__ClientId` | Distinct Recall API client UUID for that subject. |
| `RecallPreview__Tenants__0__Token` | Corresponding Recall API client token. |

Add more mappings with `Tenants__1`, `Tenants__2`, and so on. Startup rejects duplicate subjects and rejects sharing one Recall client ID between subjects. An authenticated but unmapped subject fails closed when invoking a tool. Register each tester's Recall client with a disjoint category namespace and least-privilege permissions; separate client IDs alone do not create physical database tenancy.

Keep all token values in the deployment platform's secret store. Never commit `.env.preview`.

## Local container smoke test

1. Copy `deploy/preview/.env.preview.example` to `deploy/preview/.env.preview` and replace every placeholder with test-only values.
2. Start only the API, register a least-privilege preview client through the existing bootstrap flow, and place the returned client ID/token in `.env.preview`. For OAuth mode, register one distinct client per OIDC subject.
3. Start the stack behind a TLS reverse proxy or private tunnel:

   ```powershell
   docker compose --env-file deploy/preview/.env.preview -f deploy/preview/compose.yml up --build
   ```

The compose file binds the MCP port to `127.0.0.1:8080`; it is not exposed to the LAN or internet. Point a TLS tunnel or reverse proxy at that loopback address. The remote MCP URL is `https://your-preview-host.example/mcp`.

## Client compatibility

- The OpenAI Responses API can send the static preview bearer token as an MCP request header, making it suitable for a controlled API-level smoke test.
- OAuth mode publishes protected-resource metadata at `/.well-known/oauth-protected-resource`, validates issuer and audience through the configured OIDC provider, and challenges unauthenticated MCP requests with that metadata URL.
- The configured provider must support the client registration and authorization flow required by the target ChatGPT or Claude plan. Recall Vault does not issue authorization codes or tokens itself.
- The existing `Recall.Mcp` executable remains the supported local `stdio` path for Claude Desktop and other local MCP clients.

## Promotion gates

Do not remove the test-data-only label until encryption at rest, OS-protected keys, fail-closed migration/recovery, encryption tests, OAuth, tenant isolation, TLS, backups, and operational documentation are complete.
