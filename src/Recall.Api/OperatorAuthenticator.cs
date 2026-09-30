namespace Recall.Api;

public sealed class OperatorAuthenticator(IConfiguration configuration)
{
    public void Authenticate(HttpContext context)
    {
        var expected = configuration["Recall:BootstrapToken"] ?? Environment.GetEnvironmentVariable("RECALL_BOOTSTRAP_TOKEN");
        var supplied = context.Request.Headers["X-Recall-Bootstrap-Token"].ToString();
        if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(supplied) || !TokenTools.Equals(expected, supplied))
            throw new UnauthorizedAccessException("Invalid operator credentials.");
    }
}
