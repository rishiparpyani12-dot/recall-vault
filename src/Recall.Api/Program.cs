using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Recall.Api;
using Recall.Application;
using Recall.Domain;
using Recall.Infrastructure;
using Serilog;

var builder = WebApplication.CreateBuilder(args);
var dataDirectory = Path.GetFullPath(builder.Configuration["Recall:DataDirectory"] ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RecallVault"));
Directory.CreateDirectory(dataDirectory);
builder.WebHost.UseUrls(builder.Configuration["Recall:Url"] ?? "http://127.0.0.1:5278");
builder.Host.UseSerilog((_, config) => config.MinimumLevel.Information().Enrich.FromLogContext().WriteTo.File(Path.Combine(dataDirectory, "logs", "recall-.log"), rollingInterval: RollingInterval.Day));
builder.Services.AddRecallInfrastructure($"Data Source={Path.Combine(dataDirectory, "recall.db")};Cache=Shared");
builder.Services.AddScoped<ClientAuthenticator>();
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();
await using (var scope = app.Services.CreateAsyncScope()) await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().InitializeAsync(CancellationToken.None);

app.Use(async (context, next) => { context.Response.Headers.CacheControl = "no-store"; await next(); });
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/v1/clients", async (RegisterClientRequest request, HttpContext http, RecallDbContext db, IConfiguration config, CancellationToken ct) =>
{
    var expected = config["Recall:BootstrapToken"] ?? Environment.GetEnvironmentVariable("RECALL_BOOTSTRAP_TOKEN");
    var supplied = http.Request.Headers["X-Recall-Bootstrap-Token"].ToString();
    if (string.IsNullOrWhiteSpace(expected) || !TokenTools.Equals(expected, supplied)) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.PublicIdentifier) || request.Permissions.Count is 0 or > 100) return Results.BadRequest();
    var token = TokenTools.Create();
    var client = new Client { Name = request.Name.Trim(), ClientType = request.ClientType.Trim(), PublicIdentifier = request.PublicIdentifier.Trim(), TokenHash = TokenTools.Hash(token), CreatedAt = DateTimeOffset.UtcNow };
    db.Clients.Add(client);
    foreach (var p in request.Permissions) db.Permissions.Add(new Permission { ClientId = client.Id, Category = p.Category.Trim(), CanRead = p.CanRead, CanCreate = p.CanCreate, CanUpdate = p.CanUpdate, CanDelete = p.CanDelete, MaximumSensitivity = p.MaximumSensitivity });
    await db.SaveChangesAsync(ct);
    return Results.Created($"/v1/clients/{client.Id}", new RegisterClientResponse(client.Id, token));
});

var memories = app.MapGroup("/v1/memories");
memories.MapPost("/", async (RememberRequest request, HttpContext http, ClientAuthenticator auth, IRecallService service, CancellationToken ct) => Results.Ok(await service.RememberAsync(await auth.AuthenticateAsync(http, ct), request, ct)));
memories.MapPost("/search", async (SearchRequest request, HttpContext http, ClientAuthenticator auth, IRecallService service, CancellationToken ct) => Results.Ok(await service.SearchAsync(await auth.AuthenticateAsync(http, ct), request, ct)));
memories.MapGet("/{id:guid}", async (Guid id, string? purpose, HttpContext http, ClientAuthenticator auth, IRecallService service, CancellationToken ct) => (await service.GetAsync(await auth.AuthenticateAsync(http, ct), id, purpose, ct)) is { } result ? Results.Ok(result) : Results.NotFound());
memories.MapPut("/{id:guid}", async (Guid id, UpdateMemoryRequest request, HttpContext http, ClientAuthenticator auth, IRecallService service, CancellationToken ct) => Results.Ok(await service.UpdateAsync(await auth.AuthenticateAsync(http, ct), id, request, ct)));
memories.MapDelete("/{id:guid}", async (Guid id, string? purpose, HttpContext http, ClientAuthenticator auth, IRecallService service, CancellationToken ct) => { await service.ForgetAsync(await auth.AuthenticateAsync(http, ct), id, purpose, ct); return Results.NoContent(); });

app.UseExceptionHandler(handler => handler.Run(async context =>
{
    var error = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()!.Error;
    context.Response.StatusCode = error switch { RecallAccessException => 403, RecallConflictException => 409, KeyNotFoundException => 404, ArgumentException => 400, UnauthorizedAccessException => 401, _ => 500 };
    await context.Response.WriteAsJsonAsync(new { error = context.Response.StatusCode == 500 ? "internal_error" : error.Message });
}));

await app.RunAsync();

public partial class Program;
