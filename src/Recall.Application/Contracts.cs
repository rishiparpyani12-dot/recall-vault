using Recall.Domain;

namespace Recall.Application;

public sealed record Caller(Guid ClientId, string Name);
public sealed record RememberRequest(string Content, string? Summary, string Category, Sensitivity Sensitivity = Sensitivity.Normal, int Importance = 5, double Confidence = 1, string? SourceConversation = null, DateTimeOffset? ExpiresAt = null, string? Purpose = null);
public sealed record UpdateMemoryRequest(string Content, string? Summary, string Category, Sensitivity Sensitivity, int Importance, double Confidence, DateTimeOffset? ExpiresAt, int ExpectedVersion, string? Purpose = null);
public sealed record SearchRequest(string Query, string? Category = null, int Limit = 20, string? Purpose = null);
public sealed record MemoryResult(Guid Id, string Content, string? Summary, string Category, Sensitivity Sensitivity, int Importance, double Confidence, string? SourceConversation, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? ExpiresAt, int Version);
public sealed record Page<T>(IReadOnlyList<T> Items, int Offset, int Limit, int? NextOffset);
public sealed record PermissionResult(string Category, bool CanRead, bool CanCreate, bool CanUpdate, bool CanDelete, Sensitivity MaximumSensitivity);
public sealed record AuditEventResult(Guid Id, Guid? MemoryId, AuditAction Action, string? Purpose, bool WasAllowed, string Reason, DateTimeOffset Timestamp);

public interface IRecallStore
{
    Task AddMemoryAsync(Memory memory, CancellationToken ct);
    Task<Memory?> FindMemoryAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<Memory>> SearchAsync(string query, string? category, int limit, CancellationToken ct);
    Task<IReadOnlyList<Memory>> ListMemoriesAsync(int offset, int limit, string? category, CancellationToken ct);
    Task<IReadOnlyList<Permission>> ListPermissionsAsync(Guid clientId, int offset, int limit, CancellationToken ct);
    Task<IReadOnlyList<AuditEvent>> ListAuditEventsAsync(Guid clientId, int offset, int limit, Guid? memoryId, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
    Task<Permission?> FindPermissionAsync(Guid clientId, string category, CancellationToken ct);
    Task AddAuditAsync(AuditEvent audit, CancellationToken ct);
}

public interface IClock { DateTimeOffset UtcNow { get; } }
public sealed class SystemClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }

public interface IRecallService
{
    Task<MemoryResult> RememberAsync(Caller caller, RememberRequest request, CancellationToken ct);
    Task<IReadOnlyList<MemoryResult>> SearchAsync(Caller caller, SearchRequest request, CancellationToken ct);
    Task<MemoryResult?> GetAsync(Caller caller, Guid id, string? purpose, CancellationToken ct);
    Task<MemoryResult> UpdateAsync(Caller caller, Guid id, UpdateMemoryRequest request, CancellationToken ct);
    Task ForgetAsync(Caller caller, Guid id, string? purpose, CancellationToken ct);
    Task<Page<MemoryResult>> ListAsync(Caller caller, int offset, int limit, string? category, string? purpose, CancellationToken ct);
    Task<Page<PermissionResult>> PermissionsAsync(Caller caller, int offset, int limit, string? purpose, CancellationToken ct);
    Task<Page<AuditEventResult>> AccessHistoryAsync(Caller caller, int offset, int limit, Guid? memoryId, string? purpose, CancellationToken ct);
}

public sealed class RecallAccessException(string message) : Exception(message);
public sealed class RecallConflictException(string message) : Exception(message);
