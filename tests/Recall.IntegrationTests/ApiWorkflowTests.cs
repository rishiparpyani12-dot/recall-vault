using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Recall.Api;
using Recall.Application;
using Recall.Domain;
using Xunit;

namespace Recall.IntegrationTests;

public sealed class ApiWorkflowTests : IAsyncLifetime
{
    private const string BootstrapToken = "integration-test-bootstrap-token";
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly string dataDirectory = Path.Combine(Path.GetTempPath(), "recall-vault-tests", Guid.NewGuid().ToString("N"));
    private WebApplicationFactory<Program>? factory;
    private HttpClient? http;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(dataDirectory);
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("Recall:DataDirectory", dataDirectory)
            .UseSetting("Recall:BootstrapToken", BootstrapToken)
            .UseSetting("Recall:Url", "http://127.0.0.1:0"));
        http = factory.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        http?.Dispose();
        if (factory is not null) await factory.DisposeAsync();
        SqliteConnection.ClearAllPools();
        var resolved = Path.GetFullPath(dataDirectory);
        var safeRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "recall-vault-tests"));
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
    public async Task Duplicate_public_identifier_returns_conflict()
    {
        var request = NewRegistration("duplicate-client");

        (await RegisterAsync(request)).StatusCode.Should().Be(HttpStatusCode.Created);
        var duplicate = await RegisterAsync(request);

        duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await duplicate.Content.ReadAsStringAsync()).Should().Contain("public_identifier_exists");
    }

    [Fact]
    public async Task Authenticated_client_can_remember_search_and_get_memory()
    {
        var registrationResponse = await RegisterAsync(NewRegistration("workflow-client"));
        var credentials = await registrationResponse.Content.ReadFromJsonAsync<RegisterClientResponse>(JsonOptions);
        credentials.Should().NotBeNull();
        Authenticate(credentials!);

        var rememberedResponse = await Http.PostAsJsonAsync("/v1/memories", new RememberRequest("I prefer integration tea", "Tea preference", "preferences", Purpose: "HTTP integration test"));
        rememberedResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var remembered = await rememberedResponse.Content.ReadFromJsonAsync<MemoryResult>(JsonOptions);
        remembered.Should().NotBeNull();

        var searchResponse = await Http.PostAsJsonAsync("/v1/memories/search", new SearchRequest("integration", "preferences", Purpose: "HTTP integration test"));
        searchResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var searchResults = await searchResponse.Content.ReadFromJsonAsync<List<MemoryResult>>(JsonOptions);
        searchResults.Should().ContainSingle(x => x.Id == remembered!.Id);

        var getResponse = await Http.GetAsync($"/v1/memories/{remembered!.Id}?purpose=HTTP%20integration%20test");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Missing_or_invalid_credentials_are_rejected()
    {
        var missing = await Http.PostAsJsonAsync("/v1/memories/search", new SearchRequest("anything"));
        missing.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        Http.DefaultRequestHeaders.Add("X-Recall-Client-Id", Guid.NewGuid().ToString());
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid-token");
        var invalid = await Http.PostAsJsonAsync("/v1/memories/search", new SearchRequest("anything"));
        invalid.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private HttpClient Http => http ?? throw new InvalidOperationException("Test client has not been initialized.");
    private async Task<HttpResponseMessage> RegisterAsync(RegisterClientRequest request)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/v1/clients") { Content = JsonContent.Create(request) };
        message.Headers.Add("X-Recall-Bootstrap-Token", BootstrapToken);
        return await Http.SendAsync(message);
    }
    private void Authenticate(RegisterClientResponse credentials)
    {
        Http.DefaultRequestHeaders.Add("X-Recall-Client-Id", credentials.ClientId.ToString());
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credentials.Token);
    }
    private static RegisterClientRequest NewRegistration(string identifier) => new("Integration Client", "integration-test", identifier,
        [new PermissionRequest("preferences", true, true, true, true, Sensitivity.Personal)]);
    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
