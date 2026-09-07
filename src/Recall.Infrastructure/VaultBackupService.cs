using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Recall.Infrastructure;

public sealed class VaultBackupService(string databasePath, IRecallDatabaseKeyProvider keyProvider, IRecallCredentialStore credentialStore)
{
    public const int PasswordIterations = 600_000;
    public const long MaximumDatabaseBytes = 512L * 1024 * 1024;
    private const int HeaderLength = 48;
    private static readonly byte[] Magic = "RCVLTBK1"u8.ToArray();

    public async Task CreateAsync(string outputPath, ReadOnlyMemory<char> password, CancellationToken ct)
    {
        ValidatePassword(password.Span);
        var sourcePath = Path.GetFullPath(databasePath);
        var targetPath = Path.GetFullPath(outputPath);
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("The Recall Vault database does not exist.", sourcePath);
        if (File.Exists(targetPath)) throw new IOException("The backup destination already exists; it was not overwritten.");
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        var keyHex = await keyProvider.GetOrCreateKeyAsync(ct);
        var snapshotPath = targetPath + ".snapshot";
        var packagePath = targetPath + ".partial";
        DeleteIfPresent(snapshotPath);
        DeleteIfPresent(packagePath);
        try
        {
            await CreateSqlCipherSnapshotAsync(sourcePath, snapshotPath, keyHex, ct);
            var database = await ReadBoundedAsync(snapshotPath, ct);
            var keyBytes = Convert.FromHexString(keyHex);
            var payload = new byte[keyBytes.Length + database.Length];
            keyBytes.CopyTo(payload, 0);
            database.CopyTo(payload, keyBytes.Length);
            CryptographicOperations.ZeroMemory(keyBytes);
            CryptographicOperations.ZeroMemory(database);
            try { await WritePackageAsync(packagePath, payload, password, ct); }
            finally { CryptographicOperations.ZeroMemory(payload); }
            File.Move(packagePath, targetPath);
        }
        finally
        {
            DeleteIfPresent(snapshotPath);
            DeleteIfPresent(packagePath);
        }
    }

    public async Task<string?> RestoreAsync(string inputPath, ReadOnlyMemory<char> password, CancellationToken ct)
    {
        ValidatePassword(password.Span);
        var packagePath = Path.GetFullPath(inputPath);
        var targetPath = Path.GetFullPath(databasePath);
        if (!File.Exists(packagePath)) throw new FileNotFoundException("The Recall Vault backup does not exist.", packagePath);
        if (File.Exists(targetPath + "-wal") || File.Exists(targetPath + "-shm")) throw new InvalidOperationException("Restore requires the Recall service to be stopped and SQLite sidecars to be absent.");
        var payload = await ReadPackageAsync(packagePath, password, ct);
        if (payload.Length <= 32) { CryptographicOperations.ZeroMemory(payload); throw new InvalidDataException("The backup payload is incomplete."); }
        var restoredKey = Convert.ToHexString(payload.AsSpan(0, 32));
        var candidatePath = targetPath + ".restore";
        DeleteIfPresent(candidatePath);
        string? rollbackPackage = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            await File.WriteAllBytesAsync(candidatePath, payload.AsMemory(32), ct);
            await ValidateDatabaseAsync(candidatePath, restoredKey, ct);
            string? oldKey = null;
            if (File.Exists(targetPath))
            {
                if (!credentialStore.TryRead(RecallDatabaseKeyProvider.CredentialTargetName, out oldKey) || string.IsNullOrWhiteSpace(oldKey)) throw new InvalidOperationException("The existing vault key is unavailable; the live database was not changed.");
                rollbackPackage = packagePath + ".pre-restore";
                if (File.Exists(rollbackPackage)) throw new IOException("The rollback backup already exists; the live database was not changed.");
                await CreateAsync(rollbackPackage, password, ct);
            }
            SqliteConnection.ClearAllPools();
            var displacedPath = targetPath + ".displaced";
            DeleteIfPresent(displacedPath);
            try
            {
                if (File.Exists(targetPath)) File.Move(targetPath, displacedPath);
                credentialStore.Write(RecallDatabaseKeyProvider.CredentialTargetName, restoredKey);
                File.Move(candidatePath, targetPath);
                DeleteIfPresent(displacedPath);
            }
            catch
            {
                DeleteIfPresent(targetPath);
                if (File.Exists(displacedPath)) File.Move(displacedPath, targetPath);
                if (oldKey is not null) credentialStore.Write(RecallDatabaseKeyProvider.CredentialTargetName, oldKey);
                throw;
            }
            return rollbackPackage;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            DeleteIfPresent(candidatePath);
        }
    }

    private static async Task CreateSqlCipherSnapshotAsync(string sourcePath, string snapshotPath, string key, CancellationToken ct)
    {
        await using var source = new SqliteConnection(ConnectionString(sourcePath, key, SqliteOpenMode.ReadOnly));
        await using var destination = new SqliteConnection(ConnectionString(snapshotPath, key, SqliteOpenMode.ReadWriteCreate));
        await source.OpenAsync(ct);
        await destination.OpenAsync(ct);
        source.BackupDatabase(destination);
        await ValidateOpenConnectionAsync(destination, ct);
    }

    private static async Task ValidateDatabaseAsync(string path, string key, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(ConnectionString(path, key, SqliteOpenMode.ReadOnly));
        await connection.OpenAsync(ct);
        await ValidateOpenConnectionAsync(connection, ct);
    }

    private static async Task ValidateOpenConnectionAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var cipherCommand = connection.CreateCommand();
        cipherCommand.CommandText = "PRAGMA cipher_integrity_check";
        var cipherError = (await cipherCommand.ExecuteScalarAsync(ct))?.ToString();
        if (!string.IsNullOrEmpty(cipherError) && !string.Equals(cipherError, "ok", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Backup database cipher integrity validation failed.");
        await using var integrityCommand = connection.CreateCommand();
        integrityCommand.CommandText = "PRAGMA integrity_check";
        var result = (await integrityCommand.ExecuteScalarAsync(ct))?.ToString();
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Backup database integrity validation failed.");
    }

    private static async Task WritePackageAsync(string path, byte[] payload, ReadOnlyMemory<char> password, CancellationToken ct)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var encryptionKey = Rfc2898DeriveBytes.Pbkdf2(password.Span, salt, PasswordIterations, HashAlgorithmName.SHA256, 32);
        var ciphertext = new byte[payload.Length];
        var tag = new byte[16];
        var header = CreateHeader(salt, nonce, ciphertext.Length);
        try
        {
            using var aes = new AesGcm(encryptionKey, tag.Length);
            aes.Encrypt(nonce, payload, ciphertext, tag, header);
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await stream.WriteAsync(header, ct);
            await stream.WriteAsync(ciphertext, ct);
            await stream.WriteAsync(tag, ct);
            await stream.FlushAsync(ct);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encryptionKey);
            CryptographicOperations.ZeroMemory(ciphertext);
        }
    }

    private static async Task<byte[]> ReadPackageAsync(string path, ReadOnlyMemory<char> password, CancellationToken ct)
    {
        var file = await File.ReadAllBytesAsync(path, ct);
        try
        {
            if (file.Length < HeaderLength + 16 || !file.AsSpan(0, Magic.Length).SequenceEqual(Magic)) throw new InvalidDataException("Unrecognized Recall Vault backup format.");
            var iterations = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(8, 4));
            var length = BinaryPrimitives.ReadInt64LittleEndian(file.AsSpan(40, 8));
            if (iterations != PasswordIterations || length <= 32 || length > MaximumDatabaseBytes + 32 || file.Length != HeaderLength + length + 16) throw new InvalidDataException("Invalid Recall Vault backup header.");
            var key = Rfc2898DeriveBytes.Pbkdf2(password.Span, file.AsSpan(12, 16), iterations, HashAlgorithmName.SHA256, 32);
            var plaintext = new byte[(int)length];
            try
            {
                using var aes = new AesGcm(key, 16);
                aes.Decrypt(file.AsSpan(28, 12), file.AsSpan(HeaderLength, (int)length), file.AsSpan(HeaderLength + (int)length, 16), plaintext, file.AsSpan(0, HeaderLength));
                return plaintext;
            }
            catch (CryptographicException exception) { CryptographicOperations.ZeroMemory(plaintext); throw new InvalidDataException("The backup password is wrong or the backup is corrupted.", exception); }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
        finally { CryptographicOperations.ZeroMemory(file); }
    }

    private static byte[] CreateHeader(byte[] salt, byte[] nonce, long payloadLength)
    {
        var header = new byte[HeaderLength];
        Magic.CopyTo(header, 0);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8, 4), PasswordIterations);
        salt.CopyTo(header, 12);
        nonce.CopyTo(header, 28);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(40, 8), payloadLength);
        return header;
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, CancellationToken ct)
    {
        var length = new FileInfo(path).Length;
        if (length <= 0 || length > MaximumDatabaseBytes) throw new InvalidOperationException($"Vault size must be between 1 byte and {MaximumDatabaseBytes} bytes for this backup format.");
        return await File.ReadAllBytesAsync(path, ct);
    }

    private static string ConnectionString(string path, string key, SqliteOpenMode mode) => new SqliteConnectionStringBuilder(SqlCipherConnectionFactory.CreateConnectionString(path, key)) { Mode = mode, Pooling = false }.ToString();
    private static void ValidatePassword(ReadOnlySpan<char> password) { if (password.Length < 12) throw new ArgumentException("The backup password must contain at least 12 characters."); }
    private static void DeleteIfPresent(string path) { if (File.Exists(path)) File.Delete(path); }
}
