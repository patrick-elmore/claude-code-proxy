namespace ClaudeCodeProxy;

// Latest Authorization header value seen across requests. String assignment is atomic in .NET — no lock needed.
public static class AuthTokenStore
{
    public static string? Value;
}
