using FluentAssertions;
using Recall.Infrastructure;
using Xunit;

namespace Recall.UnitTests;

public sealed class RecallDatabaseKeyProviderTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "recall-vault-key-tests", Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(directory, "recall.db");

    [Fact]
    public async Task First_run_generates_and_persists_a_256_bit_key()
    {
        var store = new FakeCredentialStore();
        var provider = new RecallDatabaseKeyProvider(DatabasePath, store);

        var key = await provider.GetOrCreateKeyAsync(CancellationToken.None);

        key.Should().MatchRegex("^[0-9A-F]{64}$");
        store.Secret.Should().Be(key);
        store.WriteCount.Should().Be(1);
        (await provider.GetOrCreateKeyAsync(CancellationToken.None)).Should().Be(key);
        store.WriteCount.Should().Be(1);
    }

    [Fact]
    public async Task Existing_key_is_reused_without_a_write()
    {
        var expected = new string('A', 64);
        var store = new FakeCredentialStore { Secret = expected };

        (await new RecallDatabaseKeyProvider(DatabasePath, store).GetOrCreateKeyAsync(CancellationToken.None)).Should().Be(expected);
        store.WriteCount.Should().Be(0);
    }

    [Fact]
    public async Task Existing_database_with_missing_key_fails_without_writing()
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(DatabasePath, [1, 2, 3]);
        var store = new FakeCredentialStore();
        var read = async () => await new RecallDatabaseKeyProvider(DatabasePath, store).GetOrCreateKeyAsync(CancellationToken.None);

        await read.Should().ThrowAsync<InvalidOperationException>().WithMessage("*missing*no replacement key*");
        store.WriteCount.Should().Be(0);
    }

    [Fact]
    public async Task Legacy_plaintext_database_gets_a_key_for_explicit_migration()
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(DatabasePath, "SQLite format 3\0"u8.ToArray());
        var store = new FakeCredentialStore();

        var key = await new RecallDatabaseKeyProvider(DatabasePath, store).GetOrCreateKeyAsync(CancellationToken.None);

        key.Should().MatchRegex("^[0-9A-F]{64}$");
        store.Secret.Should().Be(key);
        store.WriteCount.Should().Be(1);
    }

    [Fact]
    public async Task Malformed_key_fails_closed()
    {
        var store = new FakeCredentialStore { Secret = "not-a-valid-key" };
        var read = async () => await new RecallDatabaseKeyProvider(DatabasePath, store).GetOrCreateKeyAsync(CancellationToken.None);

        await read.Should().ThrowAsync<InvalidOperationException>().WithMessage("*malformed*");
        store.WriteCount.Should().Be(0);
    }

    [Fact]
    public async Task Credential_write_failure_is_propagated()
    {
        var store = new FakeCredentialStore { WriteException = new InvalidOperationException("credential store unavailable") };
        var read = async () => await new RecallDatabaseKeyProvider(DatabasePath, store).GetOrCreateKeyAsync(CancellationToken.None);

        await read.Should().ThrowAsync<InvalidOperationException>().WithMessage("credential store unavailable");
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    private sealed class FakeCredentialStore : IRecallCredentialStore
    {
        public string? Secret { get; set; }
        public int WriteCount { get; private set; }
        public Exception? WriteException { get; init; }
        public bool TryRead(string targetName, out string secret)
        {
            secret = Secret ?? string.Empty;
            return Secret is not null;
        }
        public void Write(string targetName, string secret)
        {
            if (WriteException is not null) throw WriteException;
            WriteCount++;
            Secret = secret;
        }
    }
}
