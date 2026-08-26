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

## Windows private dry run

1. Build SQLCipher and the Release solution. Register one least-privilege Recall client per tester using the local API bootstrap flow.
2. Stop the registration instance and remove `Recall__BootstrapToken` from the preview process environment. In a private PowerShell session, inject the MCP variables in the tables above from your secret manager. Do not save them in a script, profile, transcript, or repository file.
3. Start the Windows processes on loopback:

   ```powershell
   ./eng/sqlcipher/build-windows.ps1
   dotnet build RecallVault.slnx -c Release
   ./deploy/preview/windows/start-private-preview.ps1
   ```

The launcher refuses non-loopback process bindings, validates required settings, starts the API and MCP host with hidden windows, waits for both health checks, and returns their process IDs. Put a reviewed TLS reverse proxy or private identity-aware tunnel in front of `127.0.0.1:8080`; preserve the original public `Host` header. The connector URL is `https://your-preview-host.example/mcp`.

The old Linux Compose files are retained only as paused design artifacts and are assigned a non-default `paused-linux-plaintext-incompatible` profile. Do not enable that profile: the current API deliberately refuses Linux because it cannot provide the reviewed Windows Credential Manager key backend.

### External configuration still required

Before a real ChatGPT or Claude connection, the operator must choose and configure:

- an OIDC provider compatible with the target client plan;
- a Windows x64 VM or host for the encrypted runtime;
- an HTTPS domain or generated private-tunnel URL;
- OAuth client registrations and exact callbacks required by ChatGPT and Claude;
- one distinct least-privilege Recall client and tenant mapping per tester;
- platform secret injection, monitoring, cold backup, and a restore drill.

These are deployment-account actions, not repository defaults. Do not commit provider metadata containing secrets. `StaticToken` is only for local/API-level smoke testing and is not an acceptable public connector configuration.

## Client compatibility

- The OpenAI Responses API can send the static preview bearer token as an MCP request header, making it suitable for a controlled API-level smoke test. ChatGPT connector preview should use OAuth mode.
- OAuth mode publishes protected-resource metadata at `/.well-known/oauth-protected-resource`, validates issuer and audience through the configured OIDC provider, and challenges unauthenticated MCP requests with that metadata URL.
- The configured provider must support the client registration and authorization flow required by the target ChatGPT or Claude plan. Recall Vault does not issue authorization codes or tokens itself.
- The existing `Recall.Mcp` executable remains the supported local `stdio` path for Claude Desktop and other local MCP clients.

## Promotion gates

Encryption at rest, OS-protected keys, fail-closed migration, encryption tests, OAuth validation, subject-to-client mapping, and operational documentation are implemented. Do not remove the test-data-only label until a real private deployment has passed ChatGPT and Claude connector tests over TLS and the operator has demonstrated monitoring plus independent encrypted backup/key recovery and restore.
