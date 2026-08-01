using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeCodeProxy;
using Microsoft.Extensions.Options;
using Serilog;

const string ProviderVertex    = "vertex";
const string ProviderAnthropic = "anthropic";

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
builder.Services.Configure<HeaderDumpOptions>(builder.Configuration.GetSection("HeaderDump"));

builder.Services.AddHttpClient("vertex", client =>
{
    client.Timeout = Timeout.InfiniteTimeSpan;
});

builder.Services.AddSingleton<PromptRewriter>();
builder.Services.AddSingleton<RequestDumper>();
builder.Services.AddSingleton<HeaderDumper>();
builder.Services.AddSingleton<PricingCatalog>();
builder.Services.AddSingleton<StatsTracker>();
builder.Services.AddSingleton<UsageLogger>();

var app = builder.Build();

var version = (System.Reflection.AssemblyInformationalVersionAttribute?)
    Attribute.GetCustomAttribute(
        typeof(Program).Assembly,
        typeof(System.Reflection.AssemblyInformationalVersionAttribute))
    is { } infoAttr ? infoAttr.InformationalVersion : "unknown";

// Read favicon from wwwroot/favicon.svg once at startup; serve explicitly to avoid static-file MIME/path issues.
var faviconPath = Path.Combine(app.Environment.ContentRootPath, "wwwroot", "favicon.svg");
var faviconSvg = File.ReadAllText(faviconPath);
app.MapGet("/favicon.svg", () => Results.Content(faviconSvg, "image/svg+xml"));

var statsJsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    WriteIndented = true,
};
app.MapPost("/stats/dismiss/{mode}", (string mode, StatsTracker st) =>
{
    if (!Enum.TryParse<PromptFailureMode>(mode, ignoreCase: true, out var parsed))
        return Results.BadRequest($"Unknown failure mode: {mode}");
    st.AcknowledgeFailureMode(parsed);
    return Results.Redirect("/stats");
});

app.MapGet("/stats", (HttpContext ctx, StatsTracker st, PricingCatalog pricing) =>
{
    var provider = ParseProvider(ctx);
    var today = DateOnly.FromDateTime(DateTime.Now);
    var (prev, next) = StatsNavigation.AdjacentDates(st.GetAvailableDates(), today, today);
    return Results.Content(StatsHtmlRenderer.Render(st.GetSnapshot(), pricing, version, prev, next, provider), "text/html; charset=utf-8");
});

app.MapGet("/stats.json", (StatsTracker st) => Results.Json(st.GetSnapshot(), statsJsonOptions));

app.MapGet("/stats/{date}", (string date, HttpContext ctx, StatsTracker st, PricingCatalog pricing) =>
{
    if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var d))
        return Results.BadRequest("Date must be yyyy-MM-dd.");
    var snapshot = st.GetSnapshotForDate(d);
    if (snapshot is null) return Results.NotFound($"No stats for {date}.");
    var provider = ParseProvider(ctx);
    var today = DateOnly.FromDateTime(DateTime.Now);
    var (prev, next) = StatsNavigation.AdjacentDates(st.GetAvailableDates(), d, today);
    return Results.Content(StatsHtmlRenderer.Render(snapshot, pricing, version, prev, next, provider), "text/html; charset=utf-8");
});

app.MapGet("/stats/{date}.json", (string date, StatsTracker st) =>
{
    if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var d))
        return Results.BadRequest("Date must be yyyy-MM-dd.");
    var snapshot = st.GetSnapshotForDate(d);
    return snapshot is null ? Results.NotFound($"No stats for {date}.") : Results.Json(snapshot, statsJsonOptions);
});

app.MapGet("/stats/week", (HttpContext ctx, StatsTracker st, PricingCatalog pricing) =>
    Results.Content(StatsHtmlRenderer.RenderRange(LoadRange(st, 7), pricing, "Last 7 days", "/stats/week", version, ParseProvider(ctx)), "text/html; charset=utf-8"));

app.MapGet("/stats/month", (HttpContext ctx, StatsTracker st, PricingCatalog pricing) =>
    Results.Content(StatsHtmlRenderer.RenderRange(LoadRange(st, 30), pricing, "Last 30 days", "/stats/month", version, ParseProvider(ctx)), "text/html; charset=utf-8"));

app.MapGet("/internal/auth-token", () => Results.Text(AuthTokenStore.Value ?? string.Empty));

// Mutation is body-shape-driven, not path-driven — any JSON body with a system[] is a candidate.
app.Map("{**path}", async (
    HttpContext ctx,
    IHttpClientFactory httpClientFactory,
    PromptRewriter rewriter,
    RequestDumper dumper,
    HeaderDumper headerDumper,
    UsageLogger usageLogger,
    StatsTracker st,
    IOptionsMonitor<ProxyOptions> proxyOpts,
    ILogger<Program> log) =>
{
    await headerDumper.DumpAsync(ctx.Request, ctx.RequestAborted);

    if (ctx.Request.Headers.TryGetValue("Authorization", out var authHeaderValue))
    {
        var authValue = authHeaderValue.ToString();
        if (authValue != AuthTokenStore.Value)
            AuthTokenStore.Value = authValue;
    }

    var requestPath = ctx.Request.Path.Value + ctx.Request.QueryString.Value;
    var model = ExtractModel(requestPath);
    var hasBody = ctx.Request.ContentLength is > 0
        || ctx.Request.Headers.ContainsKey("Transfer-Encoding");

    byte[] forwardBytes = Array.Empty<byte>();
    JsonNode? node = null;
    var mutated = false;

    if (hasBody)
    {
        using var ms = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
        var originalBytes = ms.ToArray();

        if (!proxyOpts.CurrentValue.Enabled)
        {
            // Dump even when disabled — lets you compare raw vs mutated traffic.
            await dumper.DumpAsync("original", null, originalBytes, requestPath, ctx.RequestAborted);
            forwardBytes = originalBytes;
        }
        else
        {
            try { node = JsonNode.Parse(originalBytes); }
            catch (JsonException) { /* not JSON — forward verbatim */ }

            // Anthropic-format paths (/v1/messages) carry the model in the body, not the URL.
            if (node is not null && model is null)
                model = node["model"]?.GetValue<string>();

            if (model is null)
            {
                // Unknown path — not a model API call. Record and forward unchanged.
                log.LogWarning("Request to unknown path {Path} — forwarding unchanged", requestPath);
                st.RecordUnknownPath(requestPath);
                await dumper.DumpFailureAsync("UnknownPath", originalBytes, requestPath, ctx.RequestAborted);
                forwardBytes = originalBytes;
            }
            else
            {
                await dumper.DumpAsync("original", node, originalBytes, requestPath, ctx.RequestAborted);

                if (node is not null)
                {
                    RewriteOutcome outcome;
                    // Explicit bypass from direct SDK callers (e.g. second-brain) that set this header.
                    if (ctx.Request.Headers.ContainsKey("X-CC-Proxy-Bypass"))
                    {
                        outcome = RewriteOutcome.NotApplicable;
                    }
                    else try
                    {
                        outcome = await rewriter.RewriteAsync(node);
                    }
                    catch (Exception ex)
                    {
                        log.LogWarning(ex, "Unexpected exception during prompt rewrite — passing through original request");
                        outcome = RewriteOutcome.Failure(PromptFailureMode.UnhandledException);
                    }

                    st.RecordRewriteOutcome(outcome);
                    var dumpMode = outcome.FailureMode?.ToString()
                        ?? (outcome.IdentityNotRecognized ? "IdentityNotRecognized" : null);
                    if (dumpMode is not null)
                        await dumper.DumpFailureAsync(dumpMode, originalBytes, requestPath, ctx.RequestAborted);
                    mutated = outcome.Mutated;
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
    }

    var isAnthropicPath = requestPath.StartsWith("/v1/messages", StringComparison.OrdinalIgnoreCase);
    var anthropicBase = proxyOpts.CurrentValue.AnthropicBaseUrl?.TrimEnd('/');
    var vertexBase    = proxyOpts.CurrentValue.VertexBaseUrl?.TrimEnd('/');
    // Route to Anthropic only when the path matches AND AnthropicBaseUrl is configured.
    // A /v1/messages request with no AnthropicBaseUrl falls through to Vertex.
    var useAnthropic = isAnthropicPath && !string.IsNullOrWhiteSpace(anthropicBase);
    var baseUrl  = useAnthropic ? anthropicBase : vertexBase;
    var provider = useAnthropic ? ProviderAnthropic : ProviderVertex;
    if (string.IsNullOrWhiteSpace(baseUrl))
    {
        var whichUrl = useAnthropic ? "Proxy:AnthropicBaseUrl" : "Proxy:VertexBaseUrl";
        log.LogError("{ConfigKey} is not configured", whichUrl);
        ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await ctx.Response.WriteAsync($"Proxy misconfigured: {whichUrl} is empty.");
        return;
    }

    // .NET Vertex SDK paths include /v1; Node SDK doesn't. Strip from baseUrl when path already has it.
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
    var requestStartedAt = DateTimeOffset.UtcNow;
    using var response = await client.SendAsync(
        upstream, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);

    ctx.Response.StatusCode = (int)response.StatusCode;
    HeaderCopying.CopyResponseHeaders(response, ctx.Response);

    // UsageLogger only logs when it parses usage; this guarantees one log line per request.
    log.LogInformation("{Method} {Path} -> {Status}",
        ctx.Request.Method, requestPath, (int)response.StatusCode);

    await using var upstreamStream = await response.Content.ReadAsStreamAsync(ctx.RequestAborted);
    await usageLogger.TapAndForwardAsync(upstreamStream, ctx.Response.Body, model, provider, baseUrl, requestStartedAt, ctx.RequestAborted);
});

app.Run();

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

static string? ExtractModel(string? path)
{
    if (path is null) return null;
    var idx = path.IndexOf("/models/", StringComparison.Ordinal);
    if (idx < 0) return null;
    var start = idx + 8;
    var end = path.IndexOfAny([':', '?', '/'], start);
    return end > start ? path[start..end] : path[start..];
}

// Returns "vertex", "anthropic", or null (meaning all providers).
static string? ParseProvider(HttpContext ctx)
{
    var p = ctx.Request.Query["provider"].FirstOrDefault()?.ToLowerInvariant();
    return p is "vertex" or "anthropic" ? p : null;
}
