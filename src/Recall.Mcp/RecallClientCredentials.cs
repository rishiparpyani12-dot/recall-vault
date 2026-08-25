namespace Recall.Mcp;

public sealed record RecallClientCredentials(Guid ClientId, string Token);

public interface IRecallCredentialProvider
{
    RecallClientCredentials GetCredentials();
}

public sealed class EnvironmentRecallCredentialProvider : IRecallCredentialProvider
{
    public RecallClientCredentials GetCredentials()
    {
        var id = Environment.GetEnvironmentVariable("RECALL_CLIENT_ID");
        var token = Environment.GetEnvironmentVariable("RECALL_CLIENT_TOKEN");
        if (!Guid.TryParse(id, out var clientId))
            throw new InvalidOperationException("RECALL_CLIENT_ID must be a valid UUID.");
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("RECALL_CLIENT_TOKEN is required.");
        return new(clientId, token);
    }
}
