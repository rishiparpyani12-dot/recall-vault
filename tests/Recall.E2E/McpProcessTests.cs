using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Recall.Api;
using Recall.Application;
using Recall.Domain;
using Xunit;

namespace Recall.E2E;

public sealed class McpProcessTests : IAsyncLifetime
{
    private const string BootstrapToken = "e2e-bootstrap-token";
    private readonly string dataDirectory = Path.Combine(Path.GetTempPath(), "recall-vault-e2e", Guid.NewGuid().ToString("N"));
    private Process? apiProcess;
    private HttpClient? http;
    private Uri? apiUrl;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(dataDirectory);
        apiUrl = new Uri($"http://127.0.0.1:{GetFreePort()}");
        apiProcess = StartApi(apiUrl);
        http = new HttpClient { BaseAddress = apiUrl, Timeout = TimeSpan.FromSeconds(5) };

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!timeout.IsCancellationRequested)
        {
            if (apiProcess.HasExited)
            {
                var error = await apiProcess.StandardError.ReadToEndAsync(timeout.Token);
                throw new InvalidOperationException($"Recall API exited with code {apiProcess.ExitCode}: {error}");
            }
            try
            {
                using var response = await http.GetAsync("/health", timeout.Token);
                if (response.StatusCode == HttpStatusCode.OK) return;
            }
            catch (HttpRequestException) { }
            await Task.Delay(100, timeout.Token);
        }
        throw new TimeoutException("Recall API did not become healthy.");
    }

    public async Task DisposeAsync()
    {
        http?.Dispose();
        if (apiProcess is { HasExited: false })
        {
            apiProcess.Kill(entireProcessTree: true);
            await apiProcess.WaitForExitAsync();
        }
        apiProcess?.Dispose();
        SqliteConnection.ClearAllPools();

        var resolved = Path.GetFullPath(dataDirectory);
        var safeRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "recall-vault-e2e"));
        if (resolved.StartsWith(safeRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            for (var attempt = 0; attempt < 5 && Directory.Exists(resolved); attempt++)
            {
                try { Directory.Delete(resolved, recursive: true); }
                catch (IOException) when (attempt < 4) { await Task.Delay(100); }
            }
        }
    }

    [Fact]
    public async Task Real_stdio_server_exposes_and_executes_all_tools_with_authorization()
    {
        var credentials = await RegisterAsync("mcp-e2e-client", Sensitivity.Personal);
        await using var mcp = await CreateMcpClientAsync(credentials);

        var tools = await mcp.ListToolsAsync();
        tools.Select(x => x.Name).Should().BeEquivalentTo(
            "memory_remember", "memory_search", "memory_get", "memory_update",
            "memory_forget", "memory_list", "memory_permissions", "memory_access_history");

        var remembered = await CallAsync(mcp, "memory_remember", new Dictionary<string, object?>
        {
            ["content"] = "E2E protocol tea preference",
            ["summary"] = "Protocol test",
            ["category"] = "preferences",
            ["sensitivity"] = "Personal",
            ["purpose"] = "MCP process test"
        });
        var memoryId = GetGuid(remembered, "id");

        (await CallAsync(mcp, "memory_search", new Dictionary<string, object?> { ["query"] = "protocol", ["category"] = "preferences" })).Should().NotBeNull();
        (await CallAsync(mcp, "memory_get", new Dictionary<string, object?> { ["id"] = memoryId })).Should().NotBeNull();
        (await CallAsync(mcp, "memory_list", new Dictionary<string, object?> { ["limit"] = 1 })).Should().NotBeNull();
        (await CallAsync(mcp, "memory_permissions", new Dictionary<string, object?> { ["limit"] = 20 })).Should().NotBeNull();
        (await CallAsync(mcp, "memory_access_history", new Dictionary<string, object?> { ["limit"] = 20, ["memoryId"] = memoryId })).Should().NotBeNull();

        var updated = await CallAsync(mcp, "memory_update", new Dictionary<string, object?>
        {
            ["id"] = memoryId,
            ["content"] = "Updated E2E protocol preference",
            ["category"] = "preferences",
            ["sensitivity"] = "Personal",
            ["expectedVersion"] = 1
        });
        GetInt(updated, "version").Should().Be(2);

        await CallAsync(mcp, "memory_forget", new Dictionary<string, object?> { ["id"] = memoryId });
        var missing = await mcp.CallToolAsync("memory_get", new Dictionary<string, object?> { ["id"] = memoryId });
        missing.IsError.Should().NotBeTrue();
        missing.Content.Should().BeEmpty();
        var afterForget = await CallAsync(mcp, "memory_search", new Dictionary<string, object?> { ["query"] = "protocol" });
        afterForget.GetArrayLength().Should().Be(0);

        var invalidCredentials = credentials with { Token = "invalid-e2e-token" };
        await using var unauthorizedMcp = await CreateMcpClientAsync(invalidCredentials);
        CallToolResult? deniedResult = null;
        McpProtocolException? deniedException = null;
        try
        {
            deniedResult = await unauthorizedMcp.CallToolAsync("memory_search", new Dictionary<string, object?> { ["query"] = "protocol" });
        }
        catch (McpProtocolException exception)
        {
            deniedException = exception;
        }
        (deniedException is not null || deniedResult?.IsError == true).Should().BeTrue("invalid MCP credentials must be rejected");
    }

    private Process StartApi(Uri url)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(GetProjectAssembly("Recall.Api"));
        start.Environment["Recall__DataDirectory"] = dataDirectory;
        start.Environment["Recall__Url"] = url.ToString().TrimEnd('/');
        start.Environment["Recall__BootstrapToken"] = BootstrapToken;
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start Recall API.");
    }

    private async Task<RegisterClientResponse> RegisterAsync(string identifier, Sensitivity maximumSensitivity)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/clients")
        {
            Content = JsonContent.Create(new RegisterClientRequest("MCP E2E Client", "e2e-test", identifier,
                [new PermissionRequest("preferences", true, true, true, true, maximumSensitivity)]))
        };
        request.Headers.Add("X-Recall-Bootstrap-Token", BootstrapToken);
        using var response = await Http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RegisterClientResponse>())!;
    }

    private async Task<McpClient> CreateMcpClientAsync(RegisterClientResponse credentials)
    {
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "Recall Vault E2E",
            Command = "dotnet",
            Arguments = [GetProjectAssembly("Recall.Mcp")],
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["RECALL_API_URL"] = ApiUrl.ToString().TrimEnd('/'),
                ["RECALL_CLIENT_ID"] = credentials.ClientId.ToString(),
                ["RECALL_CLIENT_TOKEN"] = credentials.Token
            },
            ShutdownTimeout = TimeSpan.FromSeconds(5)
        });
        return await McpClient.CreateAsync(transport);
    }

    private static async Task<JsonElement> CallAsync(McpClient mcp, string name, Dictionary<string, object?> arguments)
    {
        var result = await mcp.CallToolAsync(name, arguments);
        result.IsError.Should().NotBeTrue($"tool {name} should succeed");
        return JsonSerializer.Deserialize<JsonElement>(GetText(result));
    }

    private static string GetText(CallToolResult result) => result.Content.OfType<TextContentBlock>().Single().Text;
    private static Guid GetGuid(JsonElement value, string property) => value.GetProperty(property).GetGuid();
    private static int GetInt(JsonElement value, string property) => value.GetProperty(property).GetInt32();
    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string GetProjectAssembly(string projectName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RecallVault.slnx"))) directory = directory.Parent;
        if (directory is null) throw new DirectoryNotFoundException("Could not locate the Recall Vault solution root.");
        return Path.Combine(directory.FullName, "src", projectName, "bin", BuildConfiguration, "net10.0", $"{projectName}.dll");
    }

#if DEBUG
    private const string BuildConfiguration = "Debug";
#else
    private const string BuildConfiguration = "Release";
#endif

    private HttpClient Http => http ?? throw new InvalidOperationException("HTTP client is not initialized.");
    private Uri ApiUrl => apiUrl ?? throw new InvalidOperationException("API URL is not initialized.");
}
