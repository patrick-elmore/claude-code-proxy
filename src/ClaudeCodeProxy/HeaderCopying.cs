using System.Collections.Frozen;

namespace ClaudeCodeProxy;

public static class HeaderCopying
{
    private static readonly FrozenSet<string> HopByHop = new[]
    {
        "Connection", "Keep-Alive", "Transfer-Encoding", "TE",
        "Trailers", "Upgrade", "Proxy-Authorization", "Proxy-Authenticate"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // First three are HttpClient-managed; Accept-Encoding stripped so Vertex returns plain-text SSE.
    private static readonly FrozenSet<string> RequestSkip = new[]
    {
        "Host", "Content-Type", "Content-Length", "Accept-Encoding"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

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
