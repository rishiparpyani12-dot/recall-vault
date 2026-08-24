namespace Recall.Domain;

public enum Sensitivity { Public, Normal, Personal, Sensitive, Restricted }
public enum MemoryStatus { Active, Deleted }
public enum AuditAction { Remember, Search, Get, Update, Forget, List, Permissions, AccessHistory }

public sealed class Memory
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Content { get; set; }
    public string? Summary { get; set; }
    public required string Category { get; set; }
    public Sensitivity Sensitivity { get; set; } = Sensitivity.Normal;
    public int Importance { get; set; } = 5;
    public double Confidence { get; set; } = 1;
    public Guid SourceClientId { get; set; }
    public string? SourceConversation { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? LastAccessedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public MemoryStatus Status { get; set; } = MemoryStatus.Active;
    public required string ContentHash { get; set; }
    public int Version { get; set; } = 1;
}

public sealed class Client
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public required string ClientType { get; set; }
    public required string PublicIdentifier { get; set; }
    public required string TokenHash { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    public List<Permission> Permissions { get; set; } = [];
}

public sealed class Permission
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClientId { get; set; }
    public required string Category { get; set; }
    public bool CanRead { get; set; }
    public bool CanCreate { get; set; }
    public bool CanUpdate { get; set; }
    public bool CanDelete { get; set; }
    public Sensitivity MaximumSensitivity { get; set; } = Sensitivity.Normal;
}

public sealed class AuditEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClientId { get; set; }
    public Guid? MemoryId { get; set; }
    public AuditAction Action { get; set; }
    public string? Purpose { get; set; }
    public bool WasAllowed { get; set; }
    public required string Reason { get; set; }
    public DateTimeOffset Timestamp { get; set; }
}
