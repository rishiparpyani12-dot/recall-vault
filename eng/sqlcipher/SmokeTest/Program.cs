using System.Runtime.InteropServices;

if (args.Length != 1) throw new ArgumentException("Pass the SQLCipher artifact directory.");

var artifactDirectory = Path.GetFullPath(args[0]);
var cryptoPath = Path.Combine(artifactDirectory, "libcrypto-3-x64.dll");
var sqlcipherPath = Path.Combine(artifactDirectory, "sqlcipher.dll");
foreach (var path in new[] { cryptoPath, sqlcipherPath })
{
    if (!File.Exists(path)) throw new FileNotFoundException("Required native artifact is missing.", path);
}

var cryptoHandle = NativeLibrary.Load(cryptoPath);
var sqlcipherHandle = NativeLibrary.Load(sqlcipherPath);
NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, (name, _, _) => name == "sqlcipher" ? sqlcipherHandle : IntPtr.Zero);

var databasePath = Path.Combine(Path.GetTempPath(), $"recall-vault-sqlcipher-smoke-{Guid.NewGuid():N}.db");
try
{
    using (var database = Database.Open(databasePath))
    {
        database.Execute("PRAGMA key = 'recall-vault-build-smoke-key';");
        var cipherVersion = database.QueryText("PRAGMA cipher_version;");
        if (cipherVersion != "4.17.0 community") throw new InvalidOperationException($"Expected SQLCipher 4.17.0 Community Edition, received '{cipherVersion}'.");

        database.Execute("CREATE VIRTUAL TABLE smoke_fts USING fts5(content);");
        database.Execute("INSERT INTO smoke_fts(content) VALUES ('verified encrypted search');");
        var match = database.QueryText("SELECT content FROM smoke_fts WHERE smoke_fts MATCH 'encrypted';");
        if (match != "verified encrypted search") throw new InvalidOperationException("FTS5 smoke query failed.");
    }

    var header = File.ReadAllBytes(databasePath).AsSpan(0, 16);
    if (header.SequenceEqual("SQLite format 3\0"u8)) throw new InvalidOperationException("Database has a plaintext SQLite header.");

    using var unkeyed = Database.Open(databasePath);
    if (unkeyed.TryExecute("SELECT count(*) FROM sqlite_master;"))
        throw new InvalidOperationException("Encrypted schema was readable without a key.");

    Console.WriteLine("SQLCipher 4.17.0 Community Edition, FTS5, encrypted header, and unkeyed-read rejection verified.");
}
finally
{
    if (File.Exists(databasePath)) File.Delete(databasePath);
    NativeLibrary.Free(sqlcipherHandle);
    NativeLibrary.Free(cryptoHandle);
}

internal sealed class Database : IDisposable
{
    private IntPtr handle;

    private Database(IntPtr handle) => this.handle = handle;

    public static Database Open(string path)
    {
        var result = Native.sqlite3_open(path, out var handle);
        if (result != 0) throw new InvalidOperationException($"sqlite3_open failed with code {result}.");
        return new Database(handle);
    }

    public void Execute(string sql)
    {
        var result = Native.sqlite3_exec(handle, sql, IntPtr.Zero, IntPtr.Zero, out var error);
        if (result == 0) return;
        var message = error == IntPtr.Zero ? "unknown error" : Marshal.PtrToStringUTF8(error);
        if (error != IntPtr.Zero) Native.sqlite3_free(error);
        throw new InvalidOperationException($"SQL failed with code {result}: {message}");
    }

    public bool TryExecute(string sql)
    {
        var result = Native.sqlite3_exec(handle, sql, IntPtr.Zero, IntPtr.Zero, out var error);
        if (error != IntPtr.Zero) Native.sqlite3_free(error);
        return result == 0;
    }

    public string QueryText(string sql)
    {
        var result = Native.sqlite3_prepare_v2(handle, sql, -1, out var statement, IntPtr.Zero);
        if (result != 0) throw new InvalidOperationException($"sqlite3_prepare_v2 failed with code {result}.");
        try
        {
            result = Native.sqlite3_step(statement);
            if (result != 100) throw new InvalidOperationException($"sqlite3_step returned {result} instead of SQLITE_ROW.");
            return Marshal.PtrToStringUTF8(Native.sqlite3_column_text(statement, 0)) ?? string.Empty;
        }
        finally
        {
            Native.sqlite3_finalize(statement);
        }
    }

    public void Dispose()
    {
        if (handle == IntPtr.Zero) return;
        Native.sqlite3_close(handle);
        handle = IntPtr.Zero;
    }
}

internal static partial class Native
{
    [LibraryImport("sqlcipher", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int sqlite3_open(string filename, out IntPtr database);

    [LibraryImport("sqlcipher", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int sqlite3_exec(IntPtr database, string sql, IntPtr callback, IntPtr callbackArgument, out IntPtr errorMessage);

    [LibraryImport("sqlcipher", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int sqlite3_prepare_v2(IntPtr database, string sql, int byteCount, out IntPtr statement, IntPtr tail);

    [LibraryImport("sqlcipher")]
    internal static partial int sqlite3_step(IntPtr statement);

    [LibraryImport("sqlcipher")]
    internal static partial IntPtr sqlite3_column_text(IntPtr statement, int column);

    [LibraryImport("sqlcipher")]
    internal static partial int sqlite3_finalize(IntPtr statement);

    [LibraryImport("sqlcipher")]
    internal static partial int sqlite3_close(IntPtr database);

    [LibraryImport("sqlcipher")]
    internal static partial void sqlite3_free(IntPtr pointer);
}
