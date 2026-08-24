using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Recall.Application;
using Recall.Domain;

namespace Recall.Infrastructure;

public sealed partial class RecallStore(RecallDbContext db) : IRecallStore
{
    public Task AddMemoryAsync(Memory memory, CancellationToken ct) => db.Memories.AddAsync(memory, ct).AsTask();
    public Task<Memory?> FindMemoryAsync(Guid id, CancellationToken ct) => db.Memories.SingleOrDefaultAsync(x => x.Id == id, ct);
    public Task<Permission?> FindPermissionAsync(Guid clientId, string category, CancellationToken ct) => db.Permissions.AsNoTracking().SingleOrDefaultAsync(x => x.ClientId == clientId && x.Category == category, ct);
    public Task AddAuditAsync(AuditEvent audit, CancellationToken ct) => db.AuditEvents.AddAsync(audit, ct).AsTask();
    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
    public async Task<IReadOnlyList<Memory>> ListMemoriesAsync(int offset, int limit, string? category, CancellationToken ct)
    {
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync(ct);
        const string sql = """
            SELECT CAST(Id AS TEXT) FROM Memories
            WHERE Status = 0
              AND (ExpiresAt IS NULL OR ExpiresAt > @Now)
              AND (@Category IS NULL OR Category = @Category)
            ORDER BY UpdatedAt DESC, Id
            LIMIT @Limit OFFSET @Offset
            """;
        var command = new CommandDefinition(sql, new { Now = DateTimeOffset.UtcNow, Category = category, Limit = limit, Offset = offset }, cancellationToken: ct);
        var orderedIds = (await connection.QueryAsync<string>(command)).Select(Guid.Parse).ToArray();
        var memories = await db.Memories.AsNoTracking().Where(x => orderedIds.Contains(x.Id)).ToListAsync(ct);
        var positions = orderedIds.Select((id, index) => (id, index)).ToDictionary(x => x.id, x => x.index);
        return memories.OrderBy(x => positions[x.Id]).ToArray();
    }
    public async Task<IReadOnlyList<Permission>> ListPermissionsAsync(Guid clientId, int offset, int limit, CancellationToken ct) =>
        await db.Permissions.AsNoTracking().Where(x => x.ClientId == clientId).OrderBy(x => x.Category).Skip(offset).Take(limit).ToListAsync(ct);
    public async Task<IReadOnlyList<AuditEvent>> ListAuditEventsAsync(Guid clientId, int offset, int limit, Guid? memoryId, CancellationToken ct)
    {
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync(ct);
        const string sql = """
            SELECT CAST(Id AS TEXT) FROM AuditEvents
            WHERE ClientId = @ClientId
              AND (@MemoryId IS NULL OR MemoryId = @MemoryId)
            ORDER BY Timestamp DESC, Id DESC
            LIMIT @Limit OFFSET @Offset
            """;
        var command = new CommandDefinition(sql, new { ClientId = clientId.ToString().ToUpperInvariant(), MemoryId = memoryId?.ToString().ToUpperInvariant(), Limit = limit, Offset = offset }, cancellationToken: ct);
        var orderedIds = (await connection.QueryAsync<string>(command)).Select(Guid.Parse).ToArray();
        var events = await db.AuditEvents.AsNoTracking().Where(x => orderedIds.Contains(x.Id)).ToListAsync(ct);
        var positions = orderedIds.Select((id, index) => (id, index)).ToDictionary(x => x.id, x => x.index);
        return events.OrderBy(x => positions[x.Id]).ToArray();
    }

    public async Task<IReadOnlyList<Memory>> SearchAsync(string query, string? category, int limit, CancellationToken ct)
    {
        var terms = WordPattern().Matches(query).Select(x => $"\"{x.Value.Replace("\"", "\"\"")}\"").Take(20).ToArray();
        if (terms.Length == 0) return [];
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync(ct);
        const string sql = """
            SELECT CAST(m.Id AS TEXT) FROM Memories m
            JOIN MemorySearch s ON s.MemoryId = CAST(m.Id AS TEXT)
            WHERE MemorySearch MATCH @Match
              AND m.Status = 0
              AND (m.ExpiresAt IS NULL OR m.ExpiresAt > @Now)
              AND (@Category IS NULL OR m.Category = @Category)
            ORDER BY bm25(MemorySearch), m.Importance DESC
            LIMIT @Limit
            """;
        var command = new CommandDefinition(sql, new { Match = string.Join(" AND ", terms), Category = category, Now = DateTimeOffset.UtcNow, Limit = limit }, cancellationToken: ct);
        var orderedIds = (await connection.QueryAsync<string>(command)).Select(Guid.Parse).ToArray();
        var memories = await db.Memories.Where(x => orderedIds.Contains(x.Id)).ToListAsync(ct);
        var positions = orderedIds.Select((id, index) => (id, index)).ToDictionary(x => x.id, x => x.index);
        return memories.OrderBy(x => positions[x.Id]).ToArray();
    }

    [GeneratedRegex(@"[\p{L}\p{N}_-]+", RegexOptions.CultureInvariant)]
    private static partial Regex WordPattern();
}
