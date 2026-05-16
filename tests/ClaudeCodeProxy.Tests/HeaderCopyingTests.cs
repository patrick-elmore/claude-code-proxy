using Microsoft.AspNetCore.Http;
using Shouldly;

namespace ClaudeCodeProxy.Tests;

public sealed class HeaderCopyingTests
{
    // -----------------------------------------------------------------------
    // CopyRequestHeaders
    // -----------------------------------------------------------------------

    [Fact]
    public void CopyRequestHeaders_HopByHop_Filtered()
    {
        var source = BuildRequest(("Connection", "keep-alive"), ("Transfer-Encoding", "chunked"));
        var dest = new HttpRequestMessage();

        HeaderCopying.CopyRequestHeaders(source, dest);

        dest.Headers.Contains("Connection").ShouldBeFalse();
        dest.Headers.Contains("Transfer-Encoding").ShouldBeFalse();
    }

    [Fact]
    public void CopyRequestHeaders_SkipList_Filtered()
    {
        var source = BuildRequest(("Host", "example.com"), ("Content-Length", "42"), ("Accept-Encoding", "gzip"));
        var dest = new HttpRequestMessage();

        HeaderCopying.CopyRequestHeaders(source, dest);

        dest.Headers.Contains("Host").ShouldBeFalse();
        dest.Headers.Contains("Accept-Encoding").ShouldBeFalse();
    }

    [Fact]
    public void CopyRequestHeaders_OtherHeaders_Copied()
    {
        var source = BuildRequest(("Authorization", "Bearer token123"), ("X-Custom", "value"));
        var dest = new HttpRequestMessage();

        HeaderCopying.CopyRequestHeaders(source, dest);

        dest.Headers.Contains("Authorization").ShouldBeTrue();
        dest.Headers.Contains("X-Custom").ShouldBeTrue();
    }

    // -----------------------------------------------------------------------
    // CopyResponseHeaders
    // -----------------------------------------------------------------------

    [Fact]
    public void CopyResponseHeaders_HopByHop_Filtered()
    {
        var source = BuildResponse(responseHeaders: [("Connection", "close")], contentHeaders: []);
        var dest = new DefaultHttpContext().Response;

        HeaderCopying.CopyResponseHeaders(source, dest);

        dest.Headers.ContainsKey("Connection").ShouldBeFalse();
    }

    [Fact]
    public void CopyResponseHeaders_ContentLength_Skipped()
    {
        var source = BuildResponse(responseHeaders: [], contentHeaders: [("Content-Length", "100")]);
        var dest = new DefaultHttpContext().Response;

        HeaderCopying.CopyResponseHeaders(source, dest);

        dest.Headers.ContainsKey("Content-Length").ShouldBeFalse();
    }

    [Fact]
    public void CopyResponseHeaders_ContentType_Copied()
    {
        var source = BuildResponse(responseHeaders: [], contentHeaders: [("Content-Type", "application/json")]);
        var dest = new DefaultHttpContext().Response;

        HeaderCopying.CopyResponseHeaders(source, dest);

        dest.Headers.ContainsKey("Content-Type").ShouldBeTrue();
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static HttpRequest BuildRequest(params (string key, string value)[] headers)
    {
        var ctx = new DefaultHttpContext();
        foreach (var (key, value) in headers)
            ctx.Request.Headers[key] = value;
        return ctx.Request;
    }

    private static HttpResponseMessage BuildResponse(
        (string key, string value)[] responseHeaders,
        (string key, string value)[] contentHeaders)
    {
        var msg = new HttpResponseMessage();
        foreach (var (key, value) in responseHeaders)
            msg.Headers.TryAddWithoutValidation(key, value);
        msg.Content = new StringContent("");
        foreach (var (key, value) in contentHeaders)
            msg.Content.Headers.TryAddWithoutValidation(key, value);
        return msg;
    }
}
