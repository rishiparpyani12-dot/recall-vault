using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Recall.Api;
using Recall.Application;
using Recall.Domain;
using Recall.Infrastructure;
using Xunit;

namespace Recall.E2E;

public sealed class RemoteMcpPreviewTests : IAsyncLifetime
{
    private const string PreviewToken = "e2e-preview-token-with-at-least-32-bytes";
    private const string AllowedOrigin = "https://chatgpt.com";
    private const string BootstrapToken = "remote-preview-e2e-bootstrap";
    private readonly string dataDirectory = Path.Combine(Path.GetTempPath(), "recall-vault-remote-e2e", Guid.NewGuid().ToString("N"));
    private Process? process;
    private Uri? baseUrl;
    private WebApplicationFactory<ApiAssemblyMarker>? apiFactory;
    private HttpClient? apiHttp;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(dataDirectory);
        var apiUrl = new Uri($"http://127.0.0.1:{GetFreePort()}");
        apiFactory = new WebApplicationFactory<ApiAssemblyMarker>().WithWebHostBuilder(builder => builder
            .UseSetting("Recall:DataDirectory", dataDirectory)
            .UseSetting("Recall:BootstrapToken", BootstrapToken)
            .UseSetting("Recall:Url", apiUrl.ToString().TrimEnd('/'))
            .ConfigureServices(services =>
            {
                services.RemoveAll<IRecallDatabaseKeyProvider>();
                services.AddSingleton<IRecallDatabaseKeyProvider>(new TestDatabaseKeyProvider());
            }));
        apiFactory.UseKestrel(apiUrl.Port);
        apiHttp = apiFactory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = apiUrl });
        var credentials = await RegisterAsync();

        baseUrl = new Uri($"http://127.0.0.1:{GetFreePort()}");
        process = StartPreviewHost(baseUrl, apiUrl, credentials);
        using var http = new HttpClient { BaseAddress = baseUrl, Timeout = TimeSpan.FromSeconds(5) };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        while (!timeout.IsCancellationRequested)
        {
            if (process.HasExited)
                throw new InvalidOperationException($"Remote MCP preview exited with code {process.ExitCode}.");
            try
            {
                using var response = await http.GetAsync("/health", timeout.Token);
                if (response.StatusCode == HttpStatusCode.OK) return;
            }
            catch (HttpRequestException) { }
            await Task.Delay(100, timeout.Token);
        }
        throw new TimeoutException("Remote MCP preview did not become healthy.");
    }

    public async Task DisposeAsync()
    {
        if (process is { HasExited: false })
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
        process?.Dispose();
        apiHttp?.Dispose();
        if (apiFactory is not null) await apiFactory.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(dataDirectory)) Directory.Delete(dataDirectory, recursive: true);
    }

    [Fact]
    public async Task Streamable_http_requires_token_and_rejects_untrusted_origins()
    {
        using var http = new HttpClient { BaseAddress = BaseUrl };
        using var unauthorized = await http.PostAsync("/mcp", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        using var disallowedRequest = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent("{}") };
        disallowedRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", PreviewToken);
        disallowedRequest.Headers.Add("Origin", "https://attacker.example");
        using var disallowed = await http.SendAsync(disallowedRequest);
        Assert.Equal(HttpStatusCode.Forbidden, disallowed.StatusCode);

        using var disallowedHostRequest = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent("{}") };
        disallowedHostRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", PreviewToken);
        disallowedHostRequest.Headers.Add("Origin", AllowedOrigin);
        disallowedHostRequest.Headers.Host = "attacker.example";
        using var disallowedHost = await http.SendAsync(disallowedHostRequest);
        Assert.Equal(HttpStatusCode.BadRequest, disallowedHost.StatusCode);

        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Name = "Recall Vault remote preview E2E",
            Endpoint = new Uri(BaseUrl, "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string>
            {
                ["Authorization"] = $"Bearer {PreviewToken}",
                ["Origin"] = AllowedOrigin
            }
        });
        await using var client = await McpClient.CreateAsync(transport);
        var tools = await client.ListToolsAsync();
        Assert.Equal(8, tools.Count);

        var remembered = await client.CallToolAsync("memory_remember", new Dictionary<string, object?>
        {
            ["content"] = "Synthetic remote preview memory",
            ["category"] = "preview-test",
            ["purpose"] = "Remote MCP CI verification"
        });
        Assert.NotEqual(true, remembered.IsError);
        var rememberedJson = JsonSerializer.Deserialize<JsonElement>(remembered.Content.OfType<TextContentBlock>().Single().Text);
        Assert.NotEqual(Guid.Empty, rememberedJson.GetProperty("id").GetGuid());

        var searched = await client.CallToolAsync("memory_search", new Dictionary<string, object?>
        {
            ["query"] = "Synthetic",
            ["category"] = "preview-test",
            ["purpose"] = "Remote MCP CI verification"
        });
        Assert.NotEqual(true, searched.IsError);
        var searchJson = JsonSerializer.Deserialize<JsonElement>(searched.Content.OfType<TextContentBlock>().Single().Text);
        Assert.Single(searchJson.EnumerateArray());
    }

    private async Task<RegisterClientResponse> RegisterAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/clients")
        {
            Content = JsonContent.Create(new RegisterClientRequest("Remote MCP E2E", "remote-preview-test", "remote-preview-e2e",
                [new PermissionRequest("preview-test", true, true, true, true, Sensitivity.Personal)]))
        };
        request.Headers.Add("X-Recall-Bootstrap-Token", BootstrapToken);
        using var response = await apiHttp!.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RegisterClientResponse>())!;
    }

    private static Process StartPreviewHost(Uri url, Uri apiUrl, RegisterClientResponse credentials)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(GetProjectAssembly("Recall.Mcp.Http"));
        start.Environment["ASPNETCORE_URLS"] = url.ToString().TrimEnd('/');
        start.Environment["RecallPreview__Enabled"] = "true";
        start.Environment["RecallPreview__Token"] = PreviewToken;
        start.Environment["RecallPreview__AllowedOrigins__0"] = AllowedOrigin;
        start.Environment["RecallPreview__AllowedHosts__0"] = "127.0.0.1";
        start.Environment["Recall__ApiUrl"] = apiUrl.ToString().TrimEnd('/');
        start.Environment["RECALL_CLIENT_ID"] = credentials.ClientId.ToString();
        start.Environment["RECALL_CLIENT_TOKEN"] = credentials.Token;
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start remote MCP preview.");
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

    private sealed class TestDatabaseKeyProvider : IRecallDatabaseKeyProvider
    {
        private const string Key = "71FCE87A7B99B6CA12568EB128AAE7DEB5D2C743326F2573B732CD53EB75D682";
        public ValueTask<string> GetOrCreateKeyAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Key);
    }
}
