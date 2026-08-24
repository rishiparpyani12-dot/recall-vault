using FluentAssertions;
using Recall.Application;
using Recall.Domain;
using Xunit;

namespace Recall.UnitTests;

public sealed class AuthorizationTests
{
    [Fact]
    public async Task Get_does_not_return_memory_above_sensitivity_ceiling()
    {
        var clientId = Guid.NewGuid();
        var memory = NewMemory(Sensitivity.Restricted);
        var store = new FakeStore(memory, new Permission { ClientId = clientId, Category = "identity", CanRead = true, MaximumSensitivity = Sensitivity.Personal });
        var service = new RecallService(store, new FakeClock());

        var act = () => service.GetAsync(new Caller(clientId, "test"), memory.Id, "unit test", CancellationToken.None);

        await act.Should().ThrowAsync<RecallAccessException>();
        store.Audits.Should().ContainSingle(x => !x.WasAllowed && x.MemoryId == memory.Id);
    }

    [Fact]
    public async Task Search_filters_unauthorized_results()
    {
        var clientId = Guid.NewGuid();
        var memory = NewMemory(Sensitivity.Sensitive);
        var store = new FakeStore(memory, new Permission { ClientId = clientId, Category = "identity", CanRead = true, MaximumSensitivity = Sensitivity.Normal });
        var service = new RecallService(store, new FakeClock());

        var results = await service.SearchAsync(new Caller(clientId, "test"), new SearchRequest("secret"), CancellationToken.None);

        results.Should().BeEmpty();
        store.Audits.Should().ContainSingle(x => !x.WasAllowed);
    }

    [Fact]
    public async Task List_filters_unauthorized_results()
    {
        var clientId = Guid.NewGuid();
        var memory = NewMemory(Sensitivity.Restricted);
        var store = new FakeStore(memory, new Permission { ClientId = clientId, Category = "identity", CanRead = true, MaximumSensitivity = Sensitivity.Personal });
        var service = new RecallService(store, new FakeClock());

        var page = await service.ListAsync(new Caller(clientId, "test"), 0, 20, null, "unit test", CancellationToken.None);

        page.Items.Should().BeEmpty();
        store.Audits.Should().ContainSingle(x => x.Action == AuditAction.List && !x.WasAllowed);
    }

    private static Memory NewMemory(Sensitivity sensitivity) => new() { Content = "secret", Category = "identity", Sensitivity = sensitivity, SourceClientId = Guid.NewGuid(), CreatedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z"), UpdatedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z"), ContentHash = "hash" };
    private sealed class FakeClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.Parse("2026-02-01T00:00:00Z"); }
    private sealed class FakeStore(Memory memory, Permission permission) : IRecallStore
    {
        public List<AuditEvent> Audits { get; } = [];
        public Task AddAuditAsync(AuditEvent audit, CancellationToken ct) { Audits.Add(audit); return Task.CompletedTask; }
        public Task AddMemoryAsync(Memory value, CancellationToken ct) => Task.CompletedTask;
        public Task<Memory?> FindMemoryAsync(Guid id, CancellationToken ct) => Task.FromResult<Memory?>(id == memory.Id ? memory : null);
        public Task<Permission?> FindPermissionAsync(Guid clientId, string category, CancellationToken ct) => Task.FromResult<Permission?>(permission.ClientId == clientId && permission.Category == category ? permission : null);
        public Task<IReadOnlyList<Memory>> SearchAsync(string query, string? category, int limit, CancellationToken ct) => Task.FromResult<IReadOnlyList<Memory>>([memory]);
        public Task<IReadOnlyList<Memory>> ListMemoriesAsync(int offset, int limit, string? category, CancellationToken ct) => Task.FromResult<IReadOnlyList<Memory>>([memory]);
        public Task<IReadOnlyList<Permission>> ListPermissionsAsync(Guid clientId, int offset, int limit, CancellationToken ct) => Task.FromResult<IReadOnlyList<Permission>>([permission]);
        public Task<IReadOnlyList<AuditEvent>> ListAuditEventsAsync(Guid clientId, int offset, int limit, Guid? memoryId, CancellationToken ct) => Task.FromResult<IReadOnlyList<AuditEvent>>(Audits);
        public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
