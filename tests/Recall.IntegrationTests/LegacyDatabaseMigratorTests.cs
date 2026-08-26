using FluentAssertions;
using Microsoft.Data.Sqlite;
using Recall.Infrastructure;
using Xunit;

namespace Recall.IntegrationTests;

public sealed class LegacyDatabaseMigratorTests : IDisposable
{
    private const string Key = "A4C28A91E9CF69FB55BBD8A48973920B35F020290E0B7D9B8228EAA26DF85904";
    private readonly string directory = Path.Combine(Path.GetTempPath(), "recall-vault-migration-tests", Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(directory, "recall.db");

    public LegacyDatabaseMigratorTests()
    {
        Directory.CreateDirectory(directory);
        SqlCipherConnectionFactory.InitializeProvider();
    }

    [Fact]
    public async Task Plaintext_database_is_backed_up_exported_validated_and_atomically_replaced()
    {
        await CreatePlaintextDatabaseAsync("migration-marker");
        var migrator = new LegacyDatabaseMigrator(DatabasePath, new FixedKeyProvider());

        await migrator.MigrateIfRequiredAsync(CancellationToken.None);

        LegacyDatabaseMigrator.HasPlaintextSqliteHeader(DatabasePath).Should().BeFalse();
        LegacyDatabaseMigrator.HasPlaintextSqliteHeader(migrator.BackupPath).Should().BeTrue();
        File.Exists(DatabasePath + ".migration").Should().BeFalse();
        (await ReadValueAsync(DatabasePath, Key)).Should().Be("migration-marker");
        (await ReadValueAsync(migrator.BackupPath, null)).Should().Be("migration-marker");

        await migrator.MigrateIfRequiredAsync(CancellationToken.None);
        (await ReadValueAsync(DatabasePath, Key)).Should().Be("migration-marker");
    }

    [Fact]
    public async Task Interrupted_candidate_is_discarded_and_migration_resumes_from_matching_backup()
    {
        await CreatePlaintextDatabaseAsync("resume-marker");
        var migrator = new LegacyDatabaseMigrator(DatabasePath, new FixedKeyProvider());
        File.Copy(DatabasePath, migrator.BackupPath);
        await File.WriteAllTextAsync(DatabasePath + ".migration", "partial encrypted output");

        await migrator.MigrateIfRequiredAsync(CancellationToken.None);

        (await ReadValueAsync(DatabasePath, Key)).Should().Be("resume-marker");
        File.Exists(DatabasePath + ".migration").Should().BeFalse();
    }

    [Fact]
    public async Task Mismatched_existing_backup_fails_without_replacing_plaintext_source()
    {
        await CreatePlaintextDatabaseAsync("source-marker");
        var original = await File.ReadAllBytesAsync(DatabasePath);
        var migrator = new LegacyDatabaseMigrator(DatabasePath, new FixedKeyProvider());
        await File.WriteAllTextAsync(migrator.BackupPath, "different backup");

        var migrate = async () => await migrator.MigrateIfRequiredAsync(CancellationToken.None);

        await migrate.Should().ThrowAsync<InvalidOperationException>().WithMessage("*does not match*no files were replaced*");
        (await File.ReadAllBytesAsync(DatabasePath)).Should().Equal(original);
        LegacyDatabaseMigrator.HasPlaintextSqliteHeader(DatabasePath).Should().BeTrue();
    }

    private async Task CreatePlaintextDatabaseAsync(string value)
    {
        await using var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE LegacyData (Value TEXT NOT NULL); INSERT INTO LegacyData VALUES ($value);";
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string?> ReadValueAsync(string path, string? key)
    {
        var connectionString = key is null ? $"Data Source={path};Mode=ReadOnly;Pooling=False" : new SqliteConnectionStringBuilder(SqlCipherConnectionFactory.CreateConnectionString(path, key)) { Pooling = false }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM LegacyData";
        return (await command.ExecuteScalarAsync())?.ToString();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    private sealed class FixedKeyProvider : IRecallDatabaseKeyProvider
    {
        public ValueTask<string> GetOrCreateKeyAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Key);
    }
}
