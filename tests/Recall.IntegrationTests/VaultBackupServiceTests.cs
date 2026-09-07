using FluentAssertions;
using Microsoft.Data.Sqlite;
using Recall.Infrastructure;
using Xunit;

namespace Recall.IntegrationTests;

public sealed class VaultBackupServiceTests : IDisposable
{
    private const string OriginalKey = "A4C28A91E9CF69FB55BBD8A48973920B35F020290E0B7D9B8228EAA26DF85904";
    private const string OtherKey = "B4C28A91E9CF69FB55BBD8A48973920B35F020290E0B7D9B8228EAA26DF85904";
    private readonly string directory = Path.Combine(Path.GetTempPath(), "recall-vault-backup-tests", Guid.NewGuid().ToString("N"));

    public VaultBackupServiceTests()
    {
        Directory.CreateDirectory(directory);
        SqlCipherConnectionFactory.InitializeProvider();
    }

    [Fact]
    public async Task Backup_restores_database_and_key_into_empty_vault()
    {
        var source = Path.Combine(directory, "source.db");
        var backup = Path.Combine(directory, "vault.recall-backup");
        await CreateDatabaseAsync(source, OriginalKey, "recovery-marker");
        var sourceStore = new FakeCredentialStore(OriginalKey);
        await Service(source, sourceStore).CreateAsync(backup, "correct horse battery staple".AsMemory(), CancellationToken.None);

        File.ReadAllBytes(backup).AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes("recovery-marker")).Should().Be(-1);
        var restored = Path.Combine(directory, "restored.db");
        var restoredStore = new FakeCredentialStore();
        (await Service(restored, restoredStore).RestoreAsync(backup, "correct horse battery staple".AsMemory(), CancellationToken.None)).Should().BeNull();

        restoredStore.Secret.Should().Be(OriginalKey);
        (await ReadValueAsync(restored, OriginalKey)).Should().Be("recovery-marker");
    }

    [Fact]
    public async Task Wrong_password_or_corruption_does_not_change_live_vault_or_key()
    {
        var source = Path.Combine(directory, "protected.db");
        var backup = Path.Combine(directory, "protected.recall-backup");
        var store = new FakeCredentialStore(OriginalKey);
        await CreateDatabaseAsync(source, OriginalKey, "original-marker");
        await Service(source, store).CreateAsync(backup, "correct horse battery staple".AsMemory(), CancellationToken.None);
        var originalBytes = await File.ReadAllBytesAsync(source);

        var wrong = async () => await Service(source, store).RestoreAsync(backup, "incorrect horse battery".AsMemory(), CancellationToken.None);
        await wrong.Should().ThrowAsync<InvalidDataException>().WithMessage("*wrong*corrupted*");
        (await File.ReadAllBytesAsync(source)).Should().Equal(originalBytes);
        store.Secret.Should().Be(OriginalKey);

        var corrupted = await File.ReadAllBytesAsync(backup);
        corrupted[^20] ^= 0x40;
        var corruptPath = Path.Combine(directory, "corrupt.recall-backup");
        await File.WriteAllBytesAsync(corruptPath, corrupted);
        var restoreCorrupt = async () => await Service(source, store).RestoreAsync(corruptPath, "correct horse battery staple".AsMemory(), CancellationToken.None);
        await restoreCorrupt.Should().ThrowAsync<InvalidDataException>().WithMessage("*wrong*corrupted*");
        (await File.ReadAllBytesAsync(source)).Should().Equal(originalBytes);
        store.Secret.Should().Be(OriginalKey);
    }

    [Fact]
    public async Task Restore_over_existing_vault_creates_encrypted_rollback_package()
    {
        var source = Path.Combine(directory, "old-source.db");
        var backup = Path.Combine(directory, "incoming.recall-backup");
        await CreateDatabaseAsync(source, OriginalKey, "incoming-marker");
        await Service(source, new FakeCredentialStore(OriginalKey)).CreateAsync(backup, "correct horse battery staple".AsMemory(), CancellationToken.None);

        var live = Path.Combine(directory, "live.db");
        await CreateDatabaseAsync(live, OtherKey, "current-marker");
        var liveStore = new FakeCredentialStore(OtherKey);
        var rollback = await Service(live, liveStore).RestoreAsync(backup, "correct horse battery staple".AsMemory(), CancellationToken.None);

        rollback.Should().Be(backup + ".pre-restore").And.Subject.As<string>().Should().Match(path => File.Exists(path));
        liveStore.Secret.Should().Be(OriginalKey);
        (await ReadValueAsync(live, OriginalKey)).Should().Be("incoming-marker");

        var recoveredOld = Path.Combine(directory, "recovered-old.db");
        var recoveredStore = new FakeCredentialStore();
        await Service(recoveredOld, recoveredStore).RestoreAsync(rollback!, "correct horse battery staple".AsMemory(), CancellationToken.None);
        (await ReadValueAsync(recoveredOld, OtherKey)).Should().Be("current-marker");
    }

    private static VaultBackupService Service(string path, FakeCredentialStore store) => new(path, new FixedKeyProvider(store), store);

    private static async Task CreateDatabaseAsync(string path, string key, string value)
    {
        await using var connection = new SqliteConnection(ConnectionString(path, key, SqliteOpenMode.ReadWriteCreate));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE Data (Value TEXT NOT NULL); INSERT INTO Data VALUES ($value);";
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string?> ReadValueAsync(string path, string key)
    {
        await using var connection = new SqliteConnection(ConnectionString(path, key, SqliteOpenMode.ReadOnly));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM Data";
        return (await command.ExecuteScalarAsync())?.ToString();
    }

    private static string ConnectionString(string path, string key, SqliteOpenMode mode) => new SqliteConnectionStringBuilder(SqlCipherConnectionFactory.CreateConnectionString(path, key)) { Mode = mode, Pooling = false }.ToString();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    private sealed class FixedKeyProvider(FakeCredentialStore store) : IRecallDatabaseKeyProvider
    {
        public ValueTask<string> GetOrCreateKeyAsync(CancellationToken cancellationToken) => ValueTask.FromResult(store.Secret ?? throw new InvalidOperationException("No key."));
    }

    private sealed class FakeCredentialStore(string? secret = null) : IRecallCredentialStore
    {
        public string? Secret { get; private set; } = secret;
        public bool TryRead(string targetName, out string value) { value = Secret ?? string.Empty; return Secret is not null; }
        public void Write(string targetName, string value) => Secret = value;
    }
}
