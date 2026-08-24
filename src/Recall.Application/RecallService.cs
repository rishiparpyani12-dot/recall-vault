using System.Security.Cryptography;
using System.Text;
using Recall.Domain;

namespace Recall.Application;

public sealed class RecallService(IRecallStore store, IClock clock) : IRecallService
{
    public async Task<MemoryResult> RememberAsync(Caller caller, RememberRequest request, CancellationToken ct)
    {
        Validate(request.Content, request.Category, request.Importance, request.Confidence);
        await DemandAsync(caller, request.Category, request.Sensitivity, AuditAction.Remember, request.Purpose, p => p.CanCreate, null, ct);
        var now = clock.UtcNow;
        var memory = new Memory { Content = request.Content.Trim(), Summary = request.Summary?.Trim(), Category = request.Category.Trim(), Sensitivity = request.Sensitivity, Importance = request.Importance, Confidence = request.Confidence, SourceClientId = caller.ClientId, SourceConversation = request.SourceConversation, CreatedAt = now, UpdatedAt = now, ExpiresAt = request.ExpiresAt, ContentHash = Hash(request.Content.Trim()) };
        await store.AddMemoryAsync(memory, ct);
        await store.SaveChangesAsync(ct);
        await AuditAsync(caller, memory.Id, AuditAction.Remember, request.Purpose, true, "allowed", ct);
        return Map(memory);
    }

    public async Task<IReadOnlyList<MemoryResult>> SearchAsync(Caller caller, SearchRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Query) || request.Query.Length > 500) throw new ArgumentException("Query must be 1-500 characters.");
        var limit = Math.Clamp(request.Limit, 1, 50);
        var candidates = await store.SearchAsync(request.Query, request.Category, limit * 3, ct);
        var results = new List<MemoryResult>(limit);
        foreach (var memory in candidates)
        {
            var allowed = await IsAllowedAsync(caller, memory, p => p.CanRead, ct);
            await AuditAsync(caller, memory.Id, AuditAction.Search, request.Purpose, allowed, allowed ? "allowed" : "permission_denied", ct);
            if (allowed && results.Count < limit) results.Add(Map(memory));
        }
        await store.SaveChangesAsync(ct);
        return results;
    }

    public async Task<MemoryResult?> GetAsync(Caller caller, Guid id, string? purpose, CancellationToken ct)
    {
        var memory = await store.FindMemoryAsync(id, ct);
        if (!Available(memory)) { await AuditAsync(caller, id, AuditAction.Get, purpose, false, "not_found_or_unavailable", ct); await store.SaveChangesAsync(ct); return null; }
        await DemandAsync(caller, memory!.Category, memory.Sensitivity, AuditAction.Get, purpose, p => p.CanRead, id, ct);
        memory.LastAccessedAt = clock.UtcNow;
        await AuditAsync(caller, id, AuditAction.Get, purpose, true, "allowed", ct);
        await store.SaveChangesAsync(ct);
        return Map(memory);
    }

    public async Task<MemoryResult> UpdateAsync(Caller caller, Guid id, UpdateMemoryRequest request, CancellationToken ct)
    {
        Validate(request.Content, request.Category, request.Importance, request.Confidence);
        var memory = await store.FindMemoryAsync(id, ct);
        if (!Available(memory)) throw new KeyNotFoundException("Memory not found.");
        await DemandAsync(caller, memory!.Category, memory.Sensitivity, AuditAction.Update, request.Purpose, p => p.CanUpdate, id, ct);
        await DemandAsync(caller, request.Category, request.Sensitivity, AuditAction.Update, request.Purpose, p => p.CanUpdate, id, ct);
        if (memory.Version != request.ExpectedVersion) throw new RecallConflictException("Memory version changed; reload before updating.");
        memory.Content = request.Content.Trim(); memory.Summary = request.Summary?.Trim(); memory.Category = request.Category.Trim(); memory.Sensitivity = request.Sensitivity; memory.Importance = request.Importance; memory.Confidence = request.Confidence; memory.ExpiresAt = request.ExpiresAt; memory.ContentHash = Hash(memory.Content); memory.UpdatedAt = clock.UtcNow; memory.Version++;
        await AuditAsync(caller, id, AuditAction.Update, request.Purpose, true, "allowed", ct);
        await store.SaveChangesAsync(ct);
        return Map(memory);
    }

    public async Task ForgetAsync(Caller caller, Guid id, string? purpose, CancellationToken ct)
    {
        var memory = await store.FindMemoryAsync(id, ct);
        if (!Available(memory)) throw new KeyNotFoundException("Memory not found.");
        await DemandAsync(caller, memory!.Category, memory.Sensitivity, AuditAction.Forget, purpose, p => p.CanDelete, id, ct);
        memory.Status = MemoryStatus.Deleted; memory.UpdatedAt = clock.UtcNow; memory.Version++;
        await AuditAsync(caller, id, AuditAction.Forget, purpose, true, "soft_deleted", ct);
        await store.SaveChangesAsync(ct);
    }

    private async Task DemandAsync(Caller caller, string category, Sensitivity sensitivity, AuditAction action, string? purpose, Func<Permission, bool> operation, Guid? memoryId, CancellationToken ct)
    {
        var permission = await store.FindPermissionAsync(caller.ClientId, category, ct);
        if (permission is not null && operation(permission) && sensitivity <= permission.MaximumSensitivity) return;
        await AuditAsync(caller, memoryId, action, purpose, false, "permission_denied", ct);
        await store.SaveChangesAsync(ct);
        throw new RecallAccessException("The client is not permitted to perform this operation.");
    }

    private async Task<bool> IsAllowedAsync(Caller caller, Memory memory, Func<Permission, bool> operation, CancellationToken ct)
    { var permission = await store.FindPermissionAsync(caller.ClientId, memory.Category, ct); return Available(memory) && permission is not null && operation(permission) && memory.Sensitivity <= permission.MaximumSensitivity; }
    private bool Available(Memory? memory) => memory is { Status: MemoryStatus.Active } && (memory.ExpiresAt is null || memory.ExpiresAt > clock.UtcNow);
    private Task AuditAsync(Caller caller, Guid? memoryId, AuditAction action, string? purpose, bool allowed, string reason, CancellationToken ct) => store.AddAuditAsync(new AuditEvent { ClientId = caller.ClientId, MemoryId = memoryId, Action = action, Purpose = purpose?[..Math.Min(purpose.Length, 200)], WasAllowed = allowed, Reason = reason, Timestamp = clock.UtcNow }, ct);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static void Validate(string content, string category, int importance, double confidence) { if (string.IsNullOrWhiteSpace(content) || content.Length > 20_000) throw new ArgumentException("Content must be 1-20,000 characters."); if (string.IsNullOrWhiteSpace(category) || category.Length > 100) throw new ArgumentException("Category must be 1-100 characters."); if (importance is < 0 or > 10) throw new ArgumentOutOfRangeException(nameof(importance)); if (confidence is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(confidence)); }
    private static MemoryResult Map(Memory m) => new(m.Id, m.Content, m.Summary, m.Category, m.Sensitivity, m.Importance, m.Confidence, m.SourceConversation, m.CreatedAt, m.UpdatedAt, m.ExpiresAt, m.Version);
}
