using Recall.Domain;

namespace Recall.Api;

public sealed record RegisterClientRequest(string Name, string ClientType, string PublicIdentifier, IReadOnlyList<PermissionRequest> Permissions);
public sealed record PermissionRequest(string Category, bool CanRead, bool CanCreate, bool CanUpdate, bool CanDelete, Sensitivity MaximumSensitivity);
public sealed record RegisterClientResponse(Guid ClientId, string Token);
