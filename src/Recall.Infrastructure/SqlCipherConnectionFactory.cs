using Microsoft.Data.Sqlite;

namespace Recall.Infrastructure;

public static class SqlCipherConnectionFactory
{
    public static string CreateConnectionString(string databasePath, string key)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("A database encryption key is required.");
        return new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Cache = SqliteCacheMode.Shared,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Password = key
        }.ToString();
    }

    public static void InitializeProvider()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Encrypted Recall Vault storage currently supports Windows only.");
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlcipher());
        SQLitePCL.raw.FreezeProvider();
    }
}
