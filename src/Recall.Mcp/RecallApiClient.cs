using System.Net.Http.Json;
using Recall.Application;

namespace Recall.Mcp;

public sealed class RecallApiClient(HttpClient http)
{
    private void Authenticate()
    {
        var id = Environment.GetEnvironmentVariable("RECALL_CLIENT_ID") ?? throw new InvalidOperationException("RECALL_CLIENT_ID is required.");
        var token = Environment.GetEnvironmentVariable("RECALL_CLIENT_TOKEN") ?? throw new InvalidOperationException("RECALL_CLIENT_TOKEN is required.");
        http.DefaultRequestHeaders.Remove("X-Recall-Client-Id");
        http.DefaultRequestHeaders.Add("X-Recall-Client-Id", id);
        http.DefaultRequestHeaders.Authorization = new("Bearer", token);
    }

    public async Task<MemoryResult> RememberAsync(RememberRequest request, CancellationToken ct) => await SendAsync<MemoryResult>(HttpMethod.Post, "/v1/memories", request, ct);
    public async Task<IReadOnlyList<MemoryResult>> SearchAsync(SearchRequest request, CancellationToken ct) => await SendAsync<List<MemoryResult>>(HttpMethod.Post, "/v1/memories/search", request, ct);
    public async Task<MemoryResult?> GetAsync(Guid id, string? purpose, CancellationToken ct)
    {
        Authenticate();
        var response = await http.GetAsync($"/v1/memories/{id}?purpose={Uri.EscapeDataString(purpose ?? string.Empty)}", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        await EnsureAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<MemoryResult>(cancellationToken: ct);
    }
    public Task<MemoryResult> UpdateAsync(Guid id, UpdateMemoryRequest request, CancellationToken ct) => SendAsync<MemoryResult>(HttpMethod.Put, $"/v1/memories/{id}", request, ct);
    public async Task ForgetAsync(Guid id, string? purpose, CancellationToken ct)
    {
        Authenticate();
        var response = await http.DeleteAsync($"/v1/memories/{id}?purpose={Uri.EscapeDataString(purpose ?? string.Empty)}", ct);
        await EnsureAsync(response, ct);
    }
    private async Task<T> SendAsync<T>(HttpMethod method, string uri, object value, CancellationToken ct)
    {
        Authenticate();
        using var request = new HttpRequestMessage(method, uri) { Content = JsonContent.Create(value) };
        using var response = await http.SendAsync(request, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct))!;
    }
    private static async Task EnsureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = (await response.Content.ReadAsStringAsync(ct))[..Math.Min(500, (int)response.Content.Headers.ContentLength.GetValueOrDefault(500))];
        throw new InvalidOperationException($"Recall service rejected the request ({(int)response.StatusCode}): {detail}");
    }
}
