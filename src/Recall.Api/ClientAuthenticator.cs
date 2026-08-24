using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Recall.Application;
using Recall.Infrastructure;

namespace Recall.Api;

public sealed class ClientAuthenticator(RecallDbContext db)
{
    public async Task<Caller> AuthenticateAsync(HttpContext context, CancellationToken ct)
    {
        if (!Guid.TryParse(context.Request.Headers["X-Recall-Client-Id"], out var id)) throw new UnauthorizedAccessException("Missing client identity.");
        var token = context.Request.Headers["Authorization"].ToString();
        if (!token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("Missing bearer token.");
        var client = await db.Clients.SingleOrDefaultAsync(x => x.Id == id && x.IsEnabled, ct);
        if (client is null || !TokenTools.Equals(client.TokenHash, TokenTools.Hash(token[7..]))) throw new UnauthorizedAccessException("Invalid client credentials.");
        client.LastSeenAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return new Caller(client.Id, client.Name);
    }
}

internal static class TokenTools
{
    public static string Create() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static bool Equals(string left, string right) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));
}
