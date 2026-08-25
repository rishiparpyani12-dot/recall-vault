using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using ModelContextProtocol.Client;
using Xunit;

namespace Recall.E2E;

public sealed class RemoteMcpPreviewTests : IAsyncLifetime
{
    private const string PreviewToken = "e2e-preview-token-with-at-least-32-bytes";
    private const string AllowedOrigin = "https://chatgpt.com";
    private Process? process;
    private Uri? baseUrl;

    public async Task InitializeAsync()
    {
        baseUrl = new Uri($"http://127.0.0.1:{GetFreePort()}");
        process = StartPreviewHost(baseUrl);
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
    }

    private static Process StartPreviewHost(Uri url)
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
        start.Environment["Recall__ApiUrl"] = "http://127.0.0.1:1";
        start.Environment["RECALL_CLIENT_ID"] = Guid.Empty.ToString();
        start.Environment["RECALL_CLIENT_TOKEN"] = "unused-by-tool-discovery";
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
}
