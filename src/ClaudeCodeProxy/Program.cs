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
const string FaviconSvg =
    """<svg height="1em" style="flex:none;line-height:1" viewBox="0 0 24 24" width="1em" xmlns="http://www.w3.org/2000/svg"><title>Claude Code</title><path clip-rule="evenodd" d="M20.998 10.949H24v3.102h-3v3.028h-1.487V20H18v-2.921h-1.487V20H15v-2.921H9V20H7.488v-2.921H6V20H4.487v-2.921H3V14.05H0V10.95h3V5h17.998v5.949zM6 10.949h1.488V8.102H6v2.847zm10.51 0H18V8.102h-1.49v2.847z" fill="#D97757" fill-rule="evenodd"></path></svg>""";

app.MapGet("/favicon.svg", () => Results.Content(FaviconSvg, "image/svg+xml"));

var statsJsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    WriteIndented = true,
};
app.MapGet("/stats", (StatsTracker st, PricingCatalog pricing) =>
{
    var (prev, next) = AdjacentDates(st, DateOnly.FromDateTime(DateTime.Now));
    return Results.Content(StatsHtmlRenderer.Render(st.GetSnapshot(), pricing, prev, next), "text/html; charset=utf-8");
});

app.MapGet("/stats.json", (StatsTracker st) => Results.Json(st.GetSnapshot(), statsJsonOptions));

app.MapGet("/stats/{date}", (string date, StatsTracker st, PricingCatalog pricing) =>
{
    if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var d))
        return Results.BadRequest("Date must be yyyy-MM-dd.");
    var snapshot = st.GetSnapshotForDate(d);
    if (snapshot is null) return Results.NotFound($"No stats for {date}.");
    var (prev, next) = AdjacentDates(st, d);
    return Results.Content(StatsHtmlRenderer.Render(snapshot, pricing, prev, next), "text/html; charset=utf-8");
});

app.MapGet("/stats/{date}.json", (string date, StatsTracker st) =>
{
    if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var d))
        return Results.BadRequest("Date must be yyyy-MM-dd.");
    var snapshot = st.GetSnapshotForDate(d);
    return snapshot is null ? Results.NotFound($"No stats for {date}.") : Results.Json(snapshot, statsJsonOptions);
});

app.MapGet("/stats/week", (StatsTracker st, PricingCatalog pricing) =>
    Results.Content(StatsHtmlRenderer.RenderRange(LoadRange(st, 7), pricing, "Last 7 days"), "text/html; charset=utf-8"));

app.MapGet("/stats/month", (StatsTracker st, PricingCatalog pricing) =>
    Results.Content(StatsHtmlRenderer.RenderRange(LoadRange(st, 30), pricing, "Last 30 days"), "text/html; charset=utf-8"));

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

    // The .NET Vertex SDK includes /v1/ in its paths; the Node SDK does not.
    // VertexBaseUrl ends with /v1 to cover the Node SDK case. Strip it when
    // the incoming path already supplies the version segment so both SDKs route
    // to the same upstream without a doubled /v1/v1/ prefix.
    if (requestPath.StartsWith("/v1/", StringComparison.Ordinal)
        && baseUrl.EndsWith("/v1", StringComparison.Ordinal))
    {
        baseUrl = baseUrl[..^3];
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

// Loads snapshots for the last N calendar days (today included), skipping days with no data
// except today which always returns the live snapshot.
static List<StatsSnapshot> LoadRange(StatsTracker st, int days)
{
    var today = DateOnly.FromDateTime(DateTime.Now);
    var list = new List<StatsSnapshot>(days);
    for (var i = days - 1; i >= 0; i--)
    {
        var s = st.GetSnapshotForDate(today.AddDays(-i));
        if (s is not null) list.Add(s);
    }
    return list;
}

// Returns the adjacent available dates for nav links. Today is always treated
// as the trailing edge (no future), so next is null when target == today.
static (DateOnly? prev, DateOnly? next) AdjacentDates(StatsTracker st, DateOnly target)
{
    var all = st.GetAvailableDates();
    var today = DateOnly.FromDateTime(DateTime.Now);
    var idx = ((IList<DateOnly>)all).IndexOf(target);
    DateOnly? prev = null, next = null;
    if (idx > 0) prev = all[idx - 1];
    else if (idx < 0 && all.Count > 0) prev = all[^1]; // target not in list (no file yet for today)
    if (idx >= 0 && idx < all.Count - 1 && target != today) next = all[idx + 1];
    return (prev, next);
}

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
