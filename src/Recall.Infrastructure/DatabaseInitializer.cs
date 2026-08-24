using Microsoft.EntityFrameworkCore;

namespace Recall.Infrastructure;

public sealed class DatabaseInitializer(RecallDbContext db)
{
    public async Task InitializeAsync(CancellationToken ct)
    {
        await db.Database.MigrateAsync(ct);
    }
}
