namespace Recall.Infrastructure;

public interface IRecallCredentialStore
{
    bool TryRead(string targetName, out string secret);
    void Write(string targetName, string secret);
}
