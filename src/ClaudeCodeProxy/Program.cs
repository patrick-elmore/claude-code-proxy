using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeCodeProxy;
using Microsoft.Extensions.Options;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWindowsService(options =>
{
    options.ServiceName = "ClaudeCodeProxy";
});

builder.Host.UseSerilog((context, loggerConfig) =>
    loggerConfig.ReadFrom.Configuration(context.Configuration));

builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(IPAddress.Loopback, 9996);
});

builder.Services.Configure<ProxyOptions>(builder.Configuration.GetSection("Proxy"));
builder.Services.Configure<DumpOptions>(builder.Configuration.GetSection("Dump"));
builder.Services.Configure<StatsOptions>(builder.Configuration.GetSection("Stats"));

builder.Services.AddHttpClient("vertex", client =>
{
    client.Timeout = Timeout.InfiniteTimeSpan;
});

builder.Services.AddSingleton<PromptRewriter>();
builder.Services.AddSingleton<RequestDumper>();
builder.Services.AddSingleton<PricingCatalog>();
builder.Services.AddSingleton<StatsTracker>();
builder.Services.AddSingleton<UsageLogger>();

var app = builder.Build();

// Stats endpoints — must be registered before the catch-all so ASP.NET's
// specificity-based route matching picks these literal paths over `{**path}`.
// The `/__` prefix avoids any collision with Vertex paths (which start with /projects/).
var statsJsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    WriteIndented = true,
};
app.MapGet("/stats", (StatsTracker st, PricingCatalog pricing) => Results.Content(StatsHtmlRenderer.Render(st.GetSnapshot(), pricing), "text/html; charset=utf-8"));
app.MapGet("/stats.json", (StatsTracker st) => Results.Json(st.GetSnapshot(), statsJsonOptions));

// Single catch-all. Mutation is body-shape-driven, not path-driven —
// any request whose body parses as JSON with a `system` array is a
// mutation candidate. Everything else is forwarded verbatim.
app.Map("{**path}", async (
    HttpContext ctx,
    IHttpClientFactory httpClientFactory,
    PromptRewriter rewriter,
    RequestDumper dumper,
    UsageLogger usageLogger,
    IOptionsMonitor<ProxyOptions> proxyOpts,
    ILogger<Program> log) =>
{
    var requestPath = ctx.Request.Path.Value + ctx.Request.QueryString.Value;
    var hasBody = ctx.Request.ContentLength is > 0
        || ctx.Request.Headers.ContainsKey("Transfer-Encoding");

    byte[] forwardBytes = Array.Empty<byte>();
    JsonNode? node = null;
    var mutated = false;

    if (hasBody)
    {
        // Buffer the body so we can both inspect/mutate and forward.
        using var ms = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
        var originalBytes = ms.ToArray();

        if (!proxyOpts.CurrentValue.Enabled)
        {
            // Disabled mode: forward bytes verbatim. Still dump if requested
            // (lets you inspect raw traffic without mutation interfering).
            await dumper.DumpAsync("original", null, originalBytes, requestPath, ctx.RequestAborted);
            forwardBytes = originalBytes;
        }
        else
        {
            try { node = JsonNode.Parse(originalBytes); }
            catch (JsonException) { /* not JSON — leave node null, forward as bytes */ }

            await dumper.DumpAsync("original", node, originalBytes, requestPath, ctx.RequestAborted);

            if (node is not null)
            {
                mutated = await rewriter.RewriteAsync(node);
                if (mutated)
                {
                    forwardBytes = Encoding.UTF8.GetBytes(node.ToJsonString());
                    await dumper.DumpAsync("mutated", node, null, null, ctx.RequestAborted);
                }
                else
                {
                    forwardBytes = originalBytes;
                }
            }
            else
            {
                forwardBytes = originalBytes;
            }
        }
    }

    var baseUrl = proxyOpts.CurrentValue.VertexBaseUrl?.TrimEnd('/');
    if (string.IsNullOrWhiteSpace(baseUrl))
    {
        log.LogError("Proxy:VertexBaseUrl is not configured");
        ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await ctx.Response.WriteAsync("Proxy misconfigured: Proxy:VertexBaseUrl is empty.");
        return;
    }

    var upstream = new HttpRequestMessage(
        new HttpMethod(ctx.Request.Method),
        baseUrl + requestPath);

    if (forwardBytes.Length > 0)
    {
        upstream.Content = new ByteArrayContent(forwardBytes);
        if (ctx.Request.ContentType is { Length: > 0 } ct)
            upstream.Content.Headers.TryAddWithoutValidation("Content-Type", ct);
    }

    HeaderCopying.CopyRequestHeaders(ctx.Request, upstream);

    var client = httpClientFactory.CreateClient("vertex");
    using var response = await client.SendAsync(
        upstream, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);

    ctx.Response.StatusCode = (int)response.StatusCode;
    HeaderCopying.CopyResponseHeaders(response, ctx.Response);

    // Baseline visibility: one log line per request regardless of whether the
    // response is SSE. UsageLogger only emits when it parses a message_start;
    // this gives us proof-of-life for every forwarded request.
    log.LogInformation("{Method} {Path} -> {Status}",
        ctx.Request.Method, requestPath, (int)response.StatusCode);

    // Forward the response stream to the client byte-for-byte while passively
    // tapping SSE events for prompt-cache usage data. Logs one line per request.
    var model = ExtractModel(requestPath);
    await using var upstreamStream = await response.Content.ReadAsStreamAsync(ctx.RequestAborted);
    await usageLogger.TapAndForwardAsync(upstreamStream, ctx.Response.Body, model, ctx.RequestAborted);
});

app.Run();

// Extracts the model name from a Vertex path like:
// /projects/.../models/claude-opus-4-7:streamRawPredict
static string? ExtractModel(string? path)
{
    if (path is null) return null;
    var idx = path.IndexOf("/models/", StringComparison.Ordinal);
    if (idx < 0) return null;
    var start = idx + 8;
    var end = path.IndexOfAny([':', '?', '/'], start);
    return end > start ? path[start..end] : path[start..];
}
