namespace ClaudeCodeProxy;

public static class HeaderCopying
{
    private static readonly HashSet<string> HopByHop = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Transfer-Encoding", "TE",
        "Trailers", "Upgrade", "Proxy-Authorization", "Proxy-Authenticate"
    };

    // Headers managed by StringContent or HttpClient itself — skip on request copy.
    // Accept-Encoding is stripped so Vertex returns plain-text SSE, letting the
    // UsageLogger tap parse message_start events without decompressing.
    private static readonly HashSet<string> RequestSkip = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Content-Type", "Content-Length", "Accept-Encoding"
    };

    public static void CopyRequestHeaders(HttpRequest source, HttpRequestMessage dest)
    {
        foreach (var (key, values) in source.Headers)
        {
            if (HopByHop.Contains(key) || RequestSkip.Contains(key)) continue;
            if (!dest.Headers.TryAddWithoutValidation(key, (IEnumerable<string>)values))
                dest.Content?.Headers.TryAddWithoutValidation(key, (IEnumerable<string>)values);
        }
    }

    public static void CopyResponseHeaders(HttpResponseMessage source, HttpResponse dest)
    {
        foreach (var (key, values) in source.Headers)
        {
            if (HopByHop.Contains(key)) continue;
            dest.Headers[key] = values.ToArray();
        }
        foreach (var (key, values) in source.Content.Headers)
        {
            // Skip Content-Length — we stream so ASP.NET Core uses chunked encoding
            if (key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            dest.Headers[key] = values.ToArray();
        }
    }
}
