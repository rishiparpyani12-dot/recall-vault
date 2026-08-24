using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Recall.Domain;

namespace Recall.Infrastructure;

public sealed class RecallDbContext(DbContextOptions<RecallDbContext> options) : DbContext(options)
{
    public DbSet<Memory> Memories => Set<Memory>();
    public DbSet<Client> Clients => Set<Client>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning));

    protected override void OnModelCreating(ModelBuilder modelBuilder) => ConfigureModel(modelBuilder);

    public static void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Memory>(b => { b.ToTable("Memories"); b.HasKey(x => x.Id); b.Property(x => x.Content).HasMaxLength(20_000); b.Property(x => x.Category).HasMaxLength(100); b.Property(x => x.ContentHash).HasMaxLength(64); b.HasIndex(x => new { x.Category, x.Status, x.ExpiresAt }); b.Property(x => x.Version).IsConcurrencyToken(); });
        modelBuilder.Entity<Client>(b => { b.ToTable("Clients"); b.HasKey(x => x.Id); b.HasIndex(x => x.PublicIdentifier).IsUnique(); b.Ignore(x => x.Permissions); });
        modelBuilder.Entity<Permission>(b => { b.ToTable("Permissions"); b.HasKey(x => x.Id); b.HasIndex(x => new { x.ClientId, x.Category }).IsUnique(); });
        modelBuilder.Entity<AuditEvent>(b => { b.ToTable("AuditEvents"); b.HasKey(x => x.Id); b.HasIndex(x => new { x.ClientId, x.Timestamp }); });
    }
}
