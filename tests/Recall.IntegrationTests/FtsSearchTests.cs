using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Recall.Application;
using Recall.Domain;
using Recall.Infrastructure;
using Xunit;

namespace Recall.IntegrationTests;

public sealed class FtsSearchTests
{
    [Fact]
    public async Task Soft_deleted_memory_is_removed_from_fts_results()
    {
        SqlCipherConnectionFactory.InitializeProvider();
        var ct = CancellationToken.None;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);
        var options = new DbContextOptionsBuilder<RecallDbContext>().UseSqlite(connection).Options;
        await using var db = new RecallDbContext(options);
        await new DatabaseInitializer(db).InitializeAsync(ct);
        var clientId = Guid.NewGuid();
        db.Permissions.Add(new Permission { ClientId = clientId, Category = "preferences", CanRead = true, CanCreate = true, CanDelete = true, MaximumSensitivity = Sensitivity.Personal });
        await db.SaveChangesAsync(ct);
        var service = new RecallService(new RecallStore(db), new TestClock());
        var caller = new Caller(clientId, "integration");
        var saved = await service.RememberAsync(caller, new RememberRequest("I prefer jasmine tea", null, "preferences"), ct);

        (await service.SearchAsync(caller, new SearchRequest("jasmine"), ct)).Should().ContainSingle();
        await service.ForgetAsync(caller, saved.Id, "test", ct);
        (await service.SearchAsync(caller, new SearchRequest("jasmine"), ct)).Should().BeEmpty();
    }

    private sealed class TestClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.Parse("2026-08-24T12:00:00Z"); }
}
