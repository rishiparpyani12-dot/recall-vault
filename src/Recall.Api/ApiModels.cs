using Recall.Domain;

namespace Recall.Api;

public sealed record RegisterClientRequest(string Name, string ClientType, string PublicIdentifier, IReadOnlyList<PermissionRequest> Permissions);
public sealed record PermissionRequest(string Category, bool CanRead, bool CanCreate, bool CanUpdate, bool CanDelete, Sensitivity MaximumSensitivity);
public sealed record RegisterClientResponse(Guid ClientId, string Token);
public sealed record ClientStatusRequest(bool IsEnabled);
public sealed record ReplaceClientPermissionsRequest(IReadOnlyList<PermissionRequest> Permissions);
public sealed record RotateClientTokenResponse(Guid ClientId, string Token);
public sealed record ClientPermissionResult(string Category, bool CanRead, bool CanCreate, bool CanUpdate, bool CanDelete, Sensitivity MaximumSensitivity);
public sealed record ClientSummaryResult(Guid Id, string Name, string ClientType, string PublicIdentifier, bool IsEnabled, DateTimeOffset CreatedAt, DateTimeOffset? LastSeenAt, int PermissionCount);
public sealed record ClientDetailResult(Guid Id, string Name, string ClientType, string PublicIdentifier, bool IsEnabled, DateTimeOffset CreatedAt, DateTimeOffset? LastSeenAt, IReadOnlyList<ClientPermissionResult> Permissions);
