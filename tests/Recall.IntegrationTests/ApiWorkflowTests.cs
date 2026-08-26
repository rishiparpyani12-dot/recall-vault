using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Recall.Api;
using Recall.Application;
using Recall.Domain;
using Recall.Infrastructure;
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
        factory = CreateFactory();
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

        var secondRememberedResponse = await Http.PostAsJsonAsync("/v1/memories", new RememberRequest("I prefer a second integration tea", "Second preference", "preferences", Purpose: "HTTP integration test"));
        secondRememberedResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var listPage = await Http.GetFromJsonAsync<Page<MemoryResult>>("/v1/memories?offset=0&limit=1&category=preferences&purpose=HTTP%20integration%20test", JsonOptions);
        listPage.Should().NotBeNull();
        listPage!.Items.Should().ContainSingle();
        listPage.NextOffset.Should().NotBeNull();
        var nextListPage = await Http.GetFromJsonAsync<Page<MemoryResult>>($"/v1/memories?offset={listPage.NextOffset}&limit=1&category=preferences&purpose=HTTP%20integration%20test", JsonOptions);
        nextListPage.Should().NotBeNull();
        nextListPage!.Items.Should().ContainSingle();
        nextListPage.Items[0].Id.Should().NotBe(listPage.Items[0].Id);

        var permissionsPage = await Http.GetFromJsonAsync<Page<PermissionResult>>("/v1/permissions?offset=0&limit=20&purpose=HTTP%20integration%20test", JsonOptions);
        permissionsPage.Should().NotBeNull();
        permissionsPage!.Items.Should().ContainSingle(x => x.Category == "preferences" && x.CanRead);

        var historyPage = await Http.GetFromJsonAsync<Page<AuditEventResult>>($"/v1/access-history?offset=0&limit=1&memoryId={remembered.Id}&purpose=HTTP%20integration%20test", JsonOptions);
        historyPage.Should().NotBeNull();
        historyPage!.Items.Should().Contain(x => x.MemoryId == remembered.Id && x.WasAllowed);
        historyPage.NextOffset.Should().NotBeNull();
        var nextHistoryPage = await Http.GetFromJsonAsync<Page<AuditEventResult>>($"/v1/access-history?offset={historyPage.NextOffset}&limit=1&memoryId={remembered.Id}&purpose=HTTP%20integration%20test", JsonOptions);
        nextHistoryPage.Should().NotBeNull();
        nextHistoryPage!.Items.Should().ContainSingle();
        nextHistoryPage.Items[0].Id.Should().NotBe(historyPage.Items[0].Id);
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

    [Fact]
    public async Task Database_is_encrypted_restarts_and_rejects_unkeyed_reads()
    {
        var marker = "encryption-marker-that-must-not-appear-in-file";
        var registrationResponse = await RegisterAsync(NewRegistration("encryption-client"));
        var credentials = await registrationResponse.Content.ReadFromJsonAsync<RegisterClientResponse>(JsonOptions);
        Authenticate(credentials!);
        (await Http.PostAsJsonAsync("/v1/memories", new RememberRequest(marker, null, "preferences"))).EnsureSuccessStatusCode();

        http!.Dispose();
        http = null;
        await factory!.DisposeAsync();
        factory = null;
        SqliteConnection.ClearAllPools();

        factory = CreateFactory();
        http = factory.CreateClient();
        (await Http.GetAsync("/health")).EnsureSuccessStatusCode();

        http.Dispose();
        http = null;
        await factory.DisposeAsync();
        factory = null;
        SqliteConnection.ClearAllPools();

        var databasePath = Path.Combine(dataDirectory, "recall.db");
        var bytes = await File.ReadAllBytesAsync(databasePath);
        bytes.AsSpan(0, 16).SequenceEqual("SQLite format 3\0"u8).Should().BeFalse();
        bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(marker)).Should().Be(-1);

        await using var unkeyed = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        await unkeyed.OpenAsync();
        var command = unkeyed.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master";
        var read = async () => await command.ExecuteScalarAsync();
        await read.Should().ThrowAsync<SqliteException>();
    }

    private HttpClient Http => http ?? throw new InvalidOperationException("Test client has not been initialized.");
    private WebApplicationFactory<Program> CreateFactory() => new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
        .UseSetting("Recall:DataDirectory", dataDirectory)
        .UseSetting("Recall:BootstrapToken", BootstrapToken)
        .UseSetting("Recall:Url", "http://127.0.0.1:0")
        .ConfigureServices(services =>
        {
            services.RemoveAll<IRecallDatabaseKeyProvider>();
            services.AddSingleton<IRecallDatabaseKeyProvider>(new TestDatabaseKeyProvider());
        }));
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

    private sealed class TestDatabaseKeyProvider : IRecallDatabaseKeyProvider
    {
        private const string Key = "B4C28A91E9CF69FB55BBD8A48973920B35F020290E0B7D9B8228EAA26DF85903";
        public ValueTask<string> GetOrCreateKeyAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Key);
    }
}
