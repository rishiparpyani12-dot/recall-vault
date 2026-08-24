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
