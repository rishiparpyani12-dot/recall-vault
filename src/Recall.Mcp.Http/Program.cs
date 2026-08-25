using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using ModelContextProtocol.Server;
using Recall.Mcp;

var builder = WebApplication.CreateBuilder(args);

var enabled = builder.Configuration.GetValue<bool>("RecallPreview:Enabled");
if (!enabled)
    throw new InvalidOperationException("Remote MCP preview is disabled. Set RecallPreview__Enabled=true to opt in.");

var previewToken = builder.Configuration["RecallPreview:Token"];
if (string.IsNullOrWhiteSpace(previewToken) || Encoding.UTF8.GetByteCount(previewToken) < 32)
    throw new InvalidOperationException("RecallPreview__Token must be a random secret of at least 32 bytes.");

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

    var authorization = context.Request.Headers.Authorization.ToString();
    const string prefix = "Bearer ";
    if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
        !FixedTimeEquals(authorization[prefix.Length..], previewToken))
    {
        context.Response.Headers.WWWAuthenticate = "Bearer";
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    await next();
});

app.MapGet("/health", () => Results.Ok(new { status = "ok", mode = "remote-preview", productionReady = false }));
app.MapMcp("/mcp").RequireRateLimiting("mcp");
await app.RunAsync();

static bool FixedTimeEquals(string supplied, string expected)
{
    var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
    var expectedBytes = Encoding.UTF8.GetBytes(expected);
    return suppliedBytes.Length == expectedBytes.Length &&
        CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytes);
}

public partial class Program;
