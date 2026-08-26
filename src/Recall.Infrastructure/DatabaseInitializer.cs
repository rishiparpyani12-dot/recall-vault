using Microsoft.EntityFrameworkCore;
using System.Data;

namespace Recall.Infrastructure;

public sealed class DatabaseInitializer(RecallDbContext db, LegacyDatabaseMigrator? legacyMigrator = null)
{
    public async Task InitializeAsync(CancellationToken ct)
    {
        if (legacyMigrator is not null) await legacyMigrator.MigrateIfRequiredAsync(ct);
        var connection = db.Database.GetDbConnection();
        var wasClosed = connection.State == ConnectionState.Closed;
        if (wasClosed) await connection.OpenAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA cipher_version";
            var version = (await command.ExecuteScalarAsync(ct))?.ToString();
            if (string.IsNullOrWhiteSpace(version) || !version.Contains("community", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The active SQLite provider is not the verified SQLCipher Community Edition build.");
        }
        finally
        {
            if (wasClosed) await connection.CloseAsync();
        }
        await db.Database.MigrateAsync(ct);
    }
}
