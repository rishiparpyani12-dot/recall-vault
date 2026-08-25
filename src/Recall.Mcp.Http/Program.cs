using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using ModelContextProtocol.Server;
using Recall.Mcp;
using Recall.Mcp.Http;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole();

var enabled = builder.Configuration.GetValue<bool>("RecallPreview:Enabled");
if (!enabled)
    throw new InvalidOperationException("Remote MCP preview is disabled. Set RecallPreview__Enabled=true to opt in.");

var authMode = builder.Configuration["RecallPreview:AuthMode"] ?? "StaticToken";
var staticTokenMode = authMode.Equals("StaticToken", StringComparison.OrdinalIgnoreCase);
var oauthMode = authMode.Equals("OAuth", StringComparison.OrdinalIgnoreCase);
if (!staticTokenMode && !oauthMode)
    throw new InvalidOperationException("RecallPreview__AuthMode must be StaticToken or OAuth.");

var previewToken = builder.Configuration["RecallPreview:Token"];
if (staticTokenMode && (string.IsNullOrWhiteSpace(previewToken) || Encoding.UTF8.GetByteCount(previewToken) < 32))
    throw new InvalidOperationException("RecallPreview__Token must be a random secret of at least 32 bytes in StaticToken mode.");

var allowedOrigins = builder.Configuration.GetSection("RecallPreview:AllowedOrigins").Get<string[]>() ?? [];
if (allowedOrigins.Length == 0)
    throw new InvalidOperationException("At least one RecallPreview__AllowedOrigins entry is required.");
var normalizedOrigins = allowedOrigins
    .Select(origin => origin.TrimEnd('/'))
    .ToHashSet(StringComparer.OrdinalIgnoreCase);
var allowedHosts = builder.Configuration.GetSection("RecallPreview:AllowedHosts").Get<string[]>() ?? [];
if (allowedHosts.Length == 0)
    throw new InvalidOperationException("At least one RecallPreview__AllowedHosts entry is required.");
var normalizedHosts = allowedHosts.ToHashSet(StringComparer.OrdinalIgnoreCase);

builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 1_048_576);
builder.Services.AddRateLimiter(options => options.AddFixedWindowLimiter("mcp", limiter =>
{
    limiter.PermitLimit = 60;
    limiter.Window = TimeSpan.FromMinutes(1);
    limiter.QueueLimit = 0;
}));
builder.Services.AddHttpClient<RecallApiClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Recall:ApiUrl"] ?? "http://127.0.0.1:5278");
    client.Timeout = TimeSpan.FromSeconds(15);
});
if (staticTokenMode)
{
    builder.Services.AddSingleton<IRecallCredentialProvider, EnvironmentRecallCredentialProvider>();
}
else
{
    var authority = RequireHttpsUri(builder.Configuration["RecallPreview:OAuth:Authority"], "RecallPreview__OAuth__Authority");
    var audience = builder.Configuration["RecallPreview:OAuth:Audience"];
    if (string.IsNullOrWhiteSpace(audience))
        throw new InvalidOperationException("RecallPreview__OAuth__Audience is required in OAuth mode.");
    var requiredScope = builder.Configuration["RecallPreview:OAuth:RequiredScope"];
    if (string.IsNullOrWhiteSpace(requiredScope))
        throw new InvalidOperationException("RecallPreview__OAuth__RequiredScope is required in OAuth mode.");
    var publicBaseUrl = RequireHttpsUri(builder.Configuration["RecallPreview:PublicBaseUrl"], "RecallPreview__PublicBaseUrl");
    var tenants = LoadTenants(builder.Configuration.GetSection("RecallPreview:Tenants"));

    builder.Services.AddHttpContextAccessor();
    builder.Services.AddSingleton(new RecallTenantCredentials(tenants));
    builder.Services.AddScoped<IRecallCredentialProvider, OAuthRecallCredentialProvider>();
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.Authority = authority.ToString().TrimEnd('/');
            options.Audience = audience;
            options.RequireHttpsMetadata = true;
            options.MapInboundClaims = false;
            options.Events = new JwtBearerEvents
            {
                OnChallenge = context =>
                {
                    context.HandleResponse();
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.Response.Headers.WWWAuthenticate =
                        $"Bearer resource_metadata=\"{new Uri(publicBaseUrl, "/.well-known/oauth-protected-resource")}\"";
                    return Task.CompletedTask;
                }
            };
        });
    builder.Services.AddAuthorization(options => options.AddPolicy("mcp", policy => policy
        .RequireAuthenticatedUser()
        .RequireAssertion(context => HasScope(context.User, requiredScope))));
}
builder.Services.AddMcpServer()
    .WithHttpTransport(options => options.Stateless = true)
    .WithTools<MemoryTools>();

var app = builder.Build();

app.UseRateLimiter();
app.Use(async (context, next) =>
{
    if (!context.Request.Path.StartsWithSegments("/mcp"))
    {
        await next();
        return;
    }

    if (!normalizedHosts.Contains(context.Request.Host.Host))
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var origin = context.Request.Headers.Origin.ToString().TrimEnd('/');
    if (!string.IsNullOrWhiteSpace(origin) && !normalizedOrigins.Contains(origin))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    if (staticTokenMode)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !FixedTimeEquals(authorization[prefix.Length..], previewToken!))
        {
            context.Response.Headers.WWWAuthenticate = "Bearer";
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
    }

    await next();
});
if (oauthMode)
{
    app.UseAuthentication();
    app.UseAuthorization();
}

app.MapGet("/health", () => Results.Ok(new { status = "ok", mode = "remote-preview", productionReady = false }));
if (oauthMode)
{
    var authority = builder.Configuration["RecallPreview:OAuth:Authority"]!.TrimEnd('/');
    var publicBaseUrl = builder.Configuration["RecallPreview:PublicBaseUrl"]!.TrimEnd('/');
    var requiredScope = builder.Configuration["RecallPreview:OAuth:RequiredScope"]!;
    app.MapGet("/.well-known/oauth-protected-resource", () => Results.Ok(new
    {
        resource = $"{publicBaseUrl}/mcp",
        authorization_servers = new[] { authority },
        bearer_methods_supported = new[] { "header" },
        scopes_supported = new[] { requiredScope }
    }));
    app.MapMcp("/mcp").RequireRateLimiting("mcp").RequireAuthorization("mcp");
}
else
{
    app.MapMcp("/mcp").RequireRateLimiting("mcp");
}
await app.RunAsync();

static bool FixedTimeEquals(string supplied, string expected)
{
    var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
    var expectedBytes = Encoding.UTF8.GetBytes(expected);
    return suppliedBytes.Length == expectedBytes.Length &&
        CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytes);
}

static Uri RequireHttpsUri(string? value, string setting)
{
    if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        throw new InvalidOperationException($"{setting} must be an absolute HTTPS URL.");
    return uri;
}

static IReadOnlyDictionary<string, RecallClientCredentials> LoadTenants(IConfigurationSection section)
{
    var result = new Dictionary<string, RecallClientCredentials>(StringComparer.Ordinal);
    var clientIds = new HashSet<Guid>();
    foreach (var tenant in section.GetChildren())
    {
        var subject = tenant["Subject"];
        var token = tenant["Token"];
        if (string.IsNullOrWhiteSpace(subject) || !Guid.TryParse(tenant["ClientId"], out var clientId) || string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException($"Tenant mapping '{tenant.Key}' requires Subject, ClientId, and Token.");
        if (!result.TryAdd(subject, new(clientId, token)))
            throw new InvalidOperationException($"Duplicate OAuth subject mapping: {subject}.");
        if (!clientIds.Add(clientId))
            throw new InvalidOperationException("Each OAuth subject must map to a distinct Recall client ID.");
    }
    if (result.Count == 0)
        throw new InvalidOperationException("At least one RecallPreview__Tenants mapping is required in OAuth mode.");
    return result;
}

static bool HasScope(System.Security.Claims.ClaimsPrincipal user, string requiredScope) =>
    user.FindAll("scope").Concat(user.FindAll("scp"))
        .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        .Contains(requiredScope, StringComparer.Ordinal);

public partial class Program;
