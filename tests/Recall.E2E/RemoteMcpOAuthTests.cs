using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Recall.Mcp;
using Recall.Mcp.Http;
using Xunit;

namespace Recall.E2E;

public sealed class RemoteMcpOAuthTests : IAsyncLifetime
{
    private Process? process;
    private Uri? baseUrl;

    public async Task InitializeAsync()
    {
        baseUrl = new Uri($"http://127.0.0.1:{GetFreePort()}");
        process = StartOAuthHost(baseUrl);
        using var http = new HttpClient { BaseAddress = baseUrl, Timeout = TimeSpan.FromSeconds(5) };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!timeout.IsCancellationRequested)
        {
            if (process.HasExited) throw new InvalidOperationException($"OAuth preview exited with code {process.ExitCode}.");
            try
            {
                using var response = await http.GetAsync("/health", timeout.Token);
                if (response.StatusCode == HttpStatusCode.OK) return;
            }
            catch (HttpRequestException) { }
            await Task.Delay(100, timeout.Token);
        }
        throw new TimeoutException("OAuth preview did not become healthy.");
    }

    public async Task DisposeAsync()
    {
        if (process is { HasExited: false })
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
        process?.Dispose();
    }

    [Fact]
    public async Task OAuth_mode_publishes_metadata_and_challenges_anonymous_requests()
    {
        using var http = new HttpClient { BaseAddress = BaseUrl };
        using var metadataResponse = await http.GetAsync("/.well-known/oauth-protected-resource");
        Assert.Equal(HttpStatusCode.OK, metadataResponse.StatusCode);
        var metadata = JsonSerializer.Deserialize<JsonElement>(await metadataResponse.Content.ReadAsStringAsync());
        Assert.Equal("https://preview.example/mcp", metadata.GetProperty("resource").GetString());
        Assert.Equal("https://idp.example", metadata.GetProperty("authorization_servers")[0].GetString());
        Assert.Equal("recall.mcp", metadata.GetProperty("scopes_supported")[0].GetString());

        using var denied = await http.PostAsync("/mcp", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.Contains("resource_metadata=\"https://preview.example/.well-known/oauth-protected-resource\"",
            denied.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void OAuth_subjects_resolve_to_distinct_recall_clients_and_unmapped_subjects_fail_closed()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var tenants = new RecallTenantCredentials(new Dictionary<string, RecallClientCredentials>
        {
            ["user-a"] = new(firstId, "token-a"),
            ["user-b"] = new(secondId, "token-b")
        });
        var accessor = new HttpContextAccessor { HttpContext = ContextFor("user-a") };
        var provider = new OAuthRecallCredentialProvider(accessor, tenants);
        Assert.Equal(firstId, provider.GetCredentials().ClientId);

        accessor.HttpContext = ContextFor("user-b");
        Assert.Equal(secondId, provider.GetCredentials().ClientId);

        accessor.HttpContext = ContextFor("unknown-user");
        Assert.Throws<UnauthorizedAccessException>(() => provider.GetCredentials());
    }

    private static DefaultHttpContext ContextFor(string subject)
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", subject)], "test"));
        return context;
    }

    private static Process StartOAuthHost(Uri url)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(GetProjectAssembly("Recall.Mcp.Http"));
        start.Environment["ASPNETCORE_URLS"] = url.ToString().TrimEnd('/');
        start.Environment["RecallPreview__Enabled"] = "true";
        start.Environment["RecallPreview__AuthMode"] = "OAuth";
        start.Environment["RecallPreview__AllowedOrigins__0"] = "https://chatgpt.com";
        start.Environment["RecallPreview__AllowedHosts__0"] = "127.0.0.1";
        start.Environment["RecallPreview__OAuth__Authority"] = "https://idp.example";
        start.Environment["RecallPreview__OAuth__Audience"] = "https://preview.example/mcp";
        start.Environment["RecallPreview__OAuth__RequiredScope"] = "recall.mcp";
        start.Environment["RecallPreview__PublicBaseUrl"] = "https://preview.example";
        start.Environment["RecallPreview__Tenants__0__Subject"] = "user-a";
        start.Environment["RecallPreview__Tenants__0__ClientId"] = Guid.NewGuid().ToString();
        start.Environment["RecallPreview__Tenants__0__Token"] = "test-only-recall-client-token";
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start OAuth preview host.");
    }

    private static string GetProjectAssembly(string projectName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RecallVault.slnx"))) directory = directory.Parent;
        if (directory is null) throw new DirectoryNotFoundException("Could not locate the Recall Vault solution root.");
        return Path.Combine(directory.FullName, "src", projectName, "bin", BuildConfiguration, "net10.0", $"{projectName}.dll");
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

#if DEBUG
    private const string BuildConfiguration = "Debug";
#else
    private const string BuildConfiguration = "Release";
#endif

    private Uri BaseUrl => baseUrl ?? throw new InvalidOperationException("Preview URL is not initialized.");
}
