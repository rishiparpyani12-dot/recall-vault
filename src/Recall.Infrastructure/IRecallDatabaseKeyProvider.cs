namespace Recall.Infrastructure;

public interface IRecallDatabaseKeyProvider
{
    ValueTask<string> GetOrCreateKeyAsync(CancellationToken cancellationToken);
}
