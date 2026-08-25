using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Recall.Application;

namespace Recall.Mcp;

public sealed class RecallApiClient(HttpClient http, IRecallCredentialProvider credentialProvider)
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private void Authenticate(HttpRequestMessage request)
    {
        var credentials = credentialProvider.GetCredentials();
        request.Headers.Add("X-Recall-Client-Id", credentials.ClientId.ToString());
        request.Headers.Authorization = new("Bearer", credentials.Token);
    }

    public async Task<MemoryResult> RememberAsync(RememberRequest request, CancellationToken ct) => await SendAsync<MemoryResult>(HttpMethod.Post, "/v1/memories", request, ct);
    public async Task<IReadOnlyList<MemoryResult>> SearchAsync(SearchRequest request, CancellationToken ct) => await SendAsync<List<MemoryResult>>(HttpMethod.Post, "/v1/memories/search", request, ct);
    public async Task<MemoryResult?> GetAsync(Guid id, string? purpose, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/memories/{id}?purpose={Uri.EscapeDataString(purpose ?? string.Empty)}");
        Authenticate(request);
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        await EnsureAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<MemoryResult>(JsonOptions, ct);
    }
    public Task<MemoryResult> UpdateAsync(Guid id, UpdateMemoryRequest request, CancellationToken ct) => SendAsync<MemoryResult>(HttpMethod.Put, $"/v1/memories/{id}", request, ct);
    public Task<Page<MemoryResult>> ListAsync(int offset, int limit, string? category, string? purpose, CancellationToken ct) => GetAsync<Page<MemoryResult>>($"/v1/memories?offset={offset}&limit={limit}&category={Escape(category)}&purpose={Escape(purpose)}", ct);
    public Task<Page<PermissionResult>> PermissionsAsync(int offset, int limit, string? purpose, CancellationToken ct) => GetAsync<Page<PermissionResult>>($"/v1/permissions?offset={offset}&limit={limit}&purpose={Escape(purpose)}", ct);
    public Task<Page<AuditEventResult>> AccessHistoryAsync(int offset, int limit, Guid? memoryId, string? purpose, CancellationToken ct) => GetAsync<Page<AuditEventResult>>($"/v1/access-history?offset={offset}&limit={limit}&memoryId={memoryId}&purpose={Escape(purpose)}", ct);
    public async Task ForgetAsync(Guid id, string? purpose, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/v1/memories/{id}?purpose={Uri.EscapeDataString(purpose ?? string.Empty)}");
        Authenticate(request);
        using var response = await http.SendAsync(request, ct);
        await EnsureAsync(response, ct);
    }
    private async Task<T> SendAsync<T>(HttpMethod method, string uri, object value, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, uri) { Content = JsonContent.Create(value) };
        Authenticate(request);
        using var response = await http.SendAsync(request, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct))!;
    }
    private async Task<T> GetAsync<T>(string uri, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        Authenticate(request);
        using var response = await http.SendAsync(request, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct))!;
    }
    private static string Escape(string? value) => Uri.EscapeDataString(value ?? string.Empty);
    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
    private static async Task EnsureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = (await response.Content.ReadAsStringAsync(ct))[..Math.Min(500, (int)response.Content.Headers.ContentLength.GetValueOrDefault(500))];
        throw new InvalidOperationException($"Recall service rejected the request ({(int)response.StatusCode}): {detail}");
    }
}
