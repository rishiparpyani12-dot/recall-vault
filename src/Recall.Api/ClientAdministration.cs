using Microsoft.EntityFrameworkCore;
using Recall.Application;
using Recall.Domain;
using Recall.Infrastructure;

namespace Recall.Api;

public sealed class ClientAdministration(RecallDbContext db)
{
    public async Task<Page<ClientSummaryResult>> ListAsync(int offset, int limit, CancellationToken ct)
    {
        ValidatePage(offset, limit);
        var clients = await db.Clients.AsNoTracking()
            .OrderBy(x => x.Id)
            .Skip(offset).Take(limit + 1).ToListAsync(ct);
        var ids = clients.Take(limit).Select(x => x.Id).ToArray();
        var counts = await db.Permissions.AsNoTracking().Where(x => ids.Contains(x.ClientId))
            .GroupBy(x => x.ClientId).Select(x => new { ClientId = x.Key, Count = x.Count() })
            .ToDictionaryAsync(x => x.ClientId, x => x.Count, ct);
        var hasMore = clients.Count > limit;
        var items = clients.Take(limit).Select(x => new ClientSummaryResult(x.Id, x.Name, x.ClientType, x.PublicIdentifier, x.IsEnabled, x.CreatedAt, x.LastSeenAt, counts.GetValueOrDefault(x.Id))).ToArray();
        return new Page<ClientSummaryResult>(items, offset, limit, hasMore ? offset + limit : null);
    }

    public async Task<ClientDetailResult> GetAsync(Guid id, CancellationToken ct)
    {
        var client = await db.Clients.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException("Client not found.");
        var permissions = await db.Permissions.AsNoTracking().Where(x => x.ClientId == id).OrderBy(x => x.Category)
            .Select(x => new ClientPermissionResult(x.Category, x.CanRead, x.CanCreate, x.CanUpdate, x.CanDelete, x.MaximumSensitivity)).ToListAsync(ct);
        return ToDetail(client, permissions);
    }

    public async Task<ClientDetailResult> SetStatusAsync(Guid id, bool isEnabled, CancellationToken ct)
    {
        var client = await FindAsync(id, ct);
        client.IsEnabled = isEnabled;
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<RotateClientTokenResponse> RotateTokenAsync(Guid id, CancellationToken ct)
    {
        var client = await FindAsync(id, ct);
        var token = TokenTools.Create();
        client.TokenHash = TokenTools.Hash(token);
        await db.SaveChangesAsync(ct);
        return new RotateClientTokenResponse(client.Id, token);
    }

    public async Task<ClientDetailResult> ReplacePermissionsAsync(Guid id, IReadOnlyList<PermissionRequest> requests, CancellationToken ct)
    {
        var normalized = ValidatePermissions(requests);
        _ = await FindAsync(id, ct);
        var existing = await db.Permissions.Where(x => x.ClientId == id).ToListAsync(ct);
        db.Permissions.RemoveRange(existing);
        db.Permissions.AddRange(normalized.Select(x => new Permission
        {
            ClientId = id,
            Category = x.Category,
            CanRead = x.CanRead,
            CanCreate = x.CanCreate,
            CanUpdate = x.CanUpdate,
            CanDelete = x.CanDelete,
            MaximumSensitivity = x.MaximumSensitivity
        }));
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public static IReadOnlyList<PermissionRequest> ValidatePermissions(IReadOnlyList<PermissionRequest>? requests)
    {
        if (requests is null || requests.Count is 0 or > 100) throw new ArgumentException("Between 1 and 100 permissions are required.");
        var normalized = requests.Select(x => x with { Category = x.Category?.Trim() ?? string.Empty }).ToArray();
        if (normalized.Any(x => x.Category.Length is 0 or > 100)) throw new ArgumentException("Permission categories must contain 1 to 100 characters.");
        if (normalized.Select(x => x.Category).Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalized.Length) throw new ArgumentException("Permission categories must be unique.");
        return normalized;
    }

    private async Task<Client> FindAsync(Guid id, CancellationToken ct) =>
        await db.Clients.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException("Client not found.");

    private static ClientDetailResult ToDetail(Client client, IReadOnlyList<ClientPermissionResult> permissions) =>
        new(client.Id, client.Name, client.ClientType, client.PublicIdentifier, client.IsEnabled, client.CreatedAt, client.LastSeenAt, permissions);

    private static void ValidatePage(int offset, int limit)
    {
        if (offset < 0) throw new ArgumentException("Offset cannot be negative.");
        if (limit is < 1 or > 100) throw new ArgumentException("Limit must be between 1 and 100.");
    }
}
