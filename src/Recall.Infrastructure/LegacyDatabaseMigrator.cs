using Microsoft.Data.Sqlite;
using System.Security.Cryptography;

namespace Recall.Infrastructure;

public sealed class LegacyDatabaseMigrator(string databasePath, IRecallDatabaseKeyProvider keyProvider)
{
    public string BackupPath => databasePath + ".plaintext-backup";
    internal string CandidatePath => databasePath + ".migration";

    public async Task MigrateIfRequiredAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(databasePath) || !HasPlaintextSqliteHeader(databasePath)) return;

        await ValidatePlaintextAsync(databasePath, cancellationToken);
        await ConsolidateWalAsync(cancellationToken);
        await CreateOrValidateBackupAsync(cancellationToken);
        var key = await keyProvider.GetOrCreateKeyAsync(cancellationToken);

        TryDeleteCandidate();
        try
        {
            await ExportEncryptedCandidateAsync(key, cancellationToken);
            await ValidateEncryptedCandidateAsync(key, cancellationToken);
            File.Replace(CandidatePath, databasePath, null, ignoreMetadataErrors: true);
        }
        catch
        {
            TryDeleteCandidate();
            throw;
        }
    }

    public static bool HasPlaintextSqliteHeader(string path)
    {
        Span<byte> header = stackalloc byte[16];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return stream.Read(header) == header.Length && header.SequenceEqual("SQLite format 3\0"u8);
    }

    private async Task CreateOrValidateBackupAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(BackupPath))
        {
            await using var source = new FileStream(databasePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
            await using var backup = new FileStream(BackupPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await source.CopyToAsync(backup, cancellationToken);
            await backup.FlushAsync(cancellationToken);
            return;
        }

        if (!await FilesMatchAsync(databasePath, BackupPath, cancellationToken))
            throw new InvalidOperationException($"The plaintext migration backup already exists but does not match the source database. Resolve '{BackupPath}' manually; no files were replaced.");
    }

    private static async Task<bool> FilesMatchAsync(string first, string second, CancellationToken cancellationToken)
    {
        if (new FileInfo(first).Length != new FileInfo(second).Length) return false;
        await using var firstStream = File.OpenRead(first);
        await using var secondStream = File.OpenRead(second);
        var firstHash = await SHA256.HashDataAsync(firstStream, cancellationToken);
        var secondHash = await SHA256.HashDataAsync(secondStream, cancellationToken);
        return CryptographicOperations.FixedTimeEquals(firstHash, secondHash);
    }

    private static async Task ValidatePlaintextAsync(string path, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check";
        if (!string.Equals((await command.ExecuteScalarAsync(cancellationToken))?.ToString(), "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The legacy plaintext database failed its integrity check; no migration files were created.");
    }

    private async Task ConsolidateWalAsync(CancellationToken cancellationToken)
    {
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var sidecar in new[] { databasePath + "-wal", databasePath + "-shm" })
            if (File.Exists(sidecar)) File.Delete(sidecar);
    }

    private async Task ExportEncryptedCandidateAsync(string key, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"ATTACH DATABASE {QuoteSqlLiteral(Path.GetFullPath(CandidatePath).Replace('\\', '/'))} AS encrypted KEY {QuoteSqlLiteral(key)}";
        await command.ExecuteNonQueryAsync(cancellationToken);
        try
        {
            command.CommandText = "SELECT sqlcipher_export('encrypted')";
            await command.ExecuteScalarAsync(cancellationToken);
        }
        finally
        {
            command.CommandText = "DETACH DATABASE encrypted";
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    private async Task ValidateEncryptedCandidateAsync(string key, CancellationToken cancellationToken)
    {
        if (HasPlaintextSqliteHeader(CandidatePath)) throw new InvalidOperationException("The migration candidate was not encrypted; the plaintext vault was not replaced.");
        var connectionString = new SqliteConnectionStringBuilder(SqlCipherConnectionFactory.CreateConnectionString(CandidatePath, key)) { Pooling = false }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check";
        if (!string.Equals((await command.ExecuteScalarAsync(cancellationToken))?.ToString(), "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The encrypted migration candidate failed its integrity check; the plaintext vault was not replaced.");
    }

    private void TryDeleteCandidate()
    {
        if (File.Exists(CandidatePath)) File.Delete(CandidatePath);
    }

    private static string QuoteSqlLiteral(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
