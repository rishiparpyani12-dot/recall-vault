using Recall.Infrastructure;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

if (args.Length != 3 || args[0] is not ("backup" or "restore"))
{
    Console.Error.WriteLine("Usage: Recall.Worker <backup|restore> <data-directory> <backup-file>");
    return 2;
}

SqlCipherConnectionFactory.InitializeProvider();
var dataDirectory = Path.GetFullPath(args[1]);
var databasePath = Path.Combine(dataDirectory, "recall.db");
var credentialStore = new WindowsCredentialStore();
var service = new VaultBackupService(databasePath, new RecallDatabaseKeyProvider(databasePath, credentialStore), credentialStore);
var password = ReadPassword("Backup password: ");
try
{
    if (args[0] == "backup")
    {
        await service.CreateAsync(args[2], password, CancellationToken.None);
        Console.WriteLine("Encrypted recovery backup created.");
    }
    else
    {
        Console.Error.Write("Type RESTORE to replace the target vault after validation: ");
        if (!string.Equals(Console.ReadLine(), "RESTORE", StringComparison.Ordinal)) throw new InvalidOperationException("Restore cancelled; the vault was not changed.");
        await service.RestoreAsync(args[2], password, CancellationToken.None);
        Console.WriteLine("Backup restored. Any previous vault was preserved as an encrypted rollback package beside the input backup.");
    }
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
finally { CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(password.Span)); }

static Memory<char> ReadPassword(string prompt)
{
    if (Console.IsInputRedirected) throw new InvalidOperationException("Password input must come from an interactive console.");
    Console.Error.Write(prompt);
    var buffer = new char[1024];
    var length = 0;
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) break;
        if (key.Key == ConsoleKey.Backspace) { if (length > 0) length--; continue; }
        if (!char.IsControl(key.KeyChar) && length < buffer.Length) buffer[length++] = key.KeyChar;
    }
    Console.Error.WriteLine();
    var result = new char[length];
    buffer.AsSpan(0, length).CopyTo(result);
    CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(buffer.AsSpan()));
    return result;
}
