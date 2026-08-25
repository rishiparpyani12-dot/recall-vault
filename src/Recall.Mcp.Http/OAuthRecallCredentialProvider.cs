using System.Security.Claims;
using Recall.Mcp;

namespace Recall.Mcp.Http;

public sealed record RecallTenantCredentials(IReadOnlyDictionary<string, RecallClientCredentials> BySubject);

public sealed class OAuthRecallCredentialProvider(
    IHttpContextAccessor httpContextAccessor,
    RecallTenantCredentials tenants) : IRecallCredentialProvider
{
    public RecallClientCredentials GetCredentials()
    {
        var user = httpContextAccessor.HttpContext?.User;
        var subject = user?.FindFirstValue("sub") ?? user?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(subject) || !tenants.BySubject.TryGetValue(subject, out var credentials))
            throw new UnauthorizedAccessException("The authenticated subject has no Recall Vault client mapping.");
        return credentials;
    }
}
