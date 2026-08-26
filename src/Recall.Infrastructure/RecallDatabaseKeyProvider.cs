using System.Security.Cryptography;

namespace Recall.Infrastructure;

public sealed class RecallDatabaseKeyProvider(string databasePath, IRecallCredentialStore credentialStore) : IRecallDatabaseKeyProvider
{
    public const string CredentialTargetName = "RecallVault/DatabaseKey/v1";
    private readonly SemaphoreSlim gate = new(1, 1);
    private string? cachedKey;

    public async ValueTask<string> GetOrCreateKeyAsync(CancellationToken cancellationToken)
    {
        if (cachedKey is not null) return cachedKey;
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (cachedKey is not null) return cachedKey;
            if (credentialStore.TryRead(CredentialTargetName, out var storedKey))
            {
                if (!IsValidKey(storedKey))
                    throw new InvalidOperationException("The Recall Vault database credential is malformed. Restore the credential from backup; the vault was not opened.");
                return cachedKey = storedKey;
            }

            if (File.Exists(databasePath))
                throw new InvalidOperationException("The Recall Vault database exists but its protected credential is missing. Restore the credential from backup; no replacement key was generated.");

            var generated = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            credentialStore.Write(CredentialTargetName, generated);
            return cachedKey = generated;
        }
        finally
        {
            gate.Release();
        }
    }

    private static bool IsValidKey(string key) =>
        key.Length == 64 && key.All(char.IsAsciiHexDigit);
}
