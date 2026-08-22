using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeCodeProxy;
using ClaudeCodeProxy.Presentation.Stats;
using Microsoft.AspNetCore.Http.Features;
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
builder.Services.AddSingleton<SessionMetadataStore>();
builder.Services.AddSingleton<UsageLogger>();
builder.Services.AddRazorPages();
builder.Services.AddSingleton<StatsPageViewModelFactory>();
builder.Services.AddSingleton(new ApplicationVersion(
    (System.Reflection.AssemblyInformationalVersionAttribute?)
        Attribute.GetCustomAttribute(
            typeof(Program).Assembly,
            typeof(System.Reflection.AssemblyInformationalVersionAttribute))
        is { } applicationVersion ? applicationVersion.InformationalVersion : "unknown"));

var app = builder.Build();

var statsJsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    WriteIndented = true,
};

app.MapGet("/stats.json", (StatsTracker st) => Results.Json(st.GetSnapshot(), statsJsonOptions));

app.MapGet("/stats/{date}.json", (string date, StatsTracker st) =>
{
    if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var d))
        return Results.BadRequest("Date must be yyyy-MM-dd.");
    var snapshot = st.GetSnapshotForDate(d);
    return snapshot is null ? Results.NotFound($"No stats for {date}.") : Results.Json(snapshot, statsJsonOptions);
});

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

app.MapGet("/internal/auth-token", () => Results.Text(AuthTokenStore.Value ?? string.Empty));

app.MapPost("/internal/session-metadata", async (
    HttpContext ctx,
    SessionMetadataStore metadata,
    ILogger<Program> log) =>
{
    const long MaxRequestBodyBytes = 16 * 1024;
    if (ctx.Request.ContentLength is > MaxRequestBodyBytes)
        return Results.BadRequest("Session metadata request body is too large.");

    var maxBodySizeFeature = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
    if (maxBodySizeFeature is { IsReadOnly: false })
        maxBodySizeFeature.MaxRequestBodySize = MaxRequestBodyBytes;

    SessionMetadataRequest? request;
    try
    {
        request = await JsonSerializer.DeserializeAsync<SessionMetadataRequest>(ctx.Request.Body, statsJsonOptions, ctx.RequestAborted);
    }
    catch (JsonException)
    {
        return Results.BadRequest("Session metadata must be valid JSON with known fields.");
    }
    catch (BadHttpRequestException)
    {
        return Results.BadRequest("Session metadata request body is too large.");
    }

    if (request is null)
        return Results.BadRequest("Session metadata request body is required.");

    if (!request.TryCreateUpdate(out var update, out var validationError))
        return Results.BadRequest(validationError);

    try
    {
        metadata.Upsert(update!);
    }
    catch (Exception ex)
    {
        // Side-channel metadata must never become a Claude Code failure, even if an unexpected
        // local storage error escapes the store's own best-effort handling.
        log.LogWarning(ex, "Failed to accept session metadata update");
    }

    return Results.NoContent();
});

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
    // Claude Code attaches these to every request (agent IDs only appear on subagent requests).
    // Capture them before streaming the response so its usage can be attributed after completion.
    var sessionId = HeaderValue(ctx.Request, "x-claude-code-session-id");
    var agentId = HeaderValue(ctx.Request, "x-claude-code-agent-id");
    var parentAgentId = HeaderValue(ctx.Request, "x-claude-code-parent-agent-id");
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

            // Strip empty/whitespace-only text content blocks from messages[]. Claude Code
            // emits these on compacted/resumed assistant turns and Anthropic rejects them with
            // 400 "messages: text content blocks must be non-empty". Runs on every parsed
            // message-shaped request, independent of the prompt rewriter, and forces the
            // sanitized body to be forwarded (mutated=true) when it removes anything.
            if (node is not null && MessageSanitizer.Sanitize(node))
            {
                mutated = true;
                log.LogInformation("Removed empty text content block(s) from messages[]");
            }

            if (model is null)
            {
                // Unknown path — not a model API call. Record and forward unchanged, unless
                // sanitization already cleaned the body (mutated), in which case forward that.
                log.LogWarning("Request to unknown path {Path} — forwarding unchanged", requestPath);
                st.RecordUnknownPath(requestPath);
                await dumper.DumpFailureAsync("UnknownPath", originalBytes, requestPath, ctx.RequestAborted);
                forwardBytes = mutated && node is not null
                    ? Encoding.UTF8.GetBytes(node.ToJsonString())
                    : originalBytes;
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
                    // Preserve sanitization (mutated already true if MessageSanitizer removed blocks).
                    mutated = mutated || outcome.Mutated;
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
    await usageLogger.TapAndForwardAsync(upstreamStream, ctx.Response.Body, model, provider, baseUrl,
        sessionId, agentId, parentAgentId, requestStartedAt, ctx.RequestAborted);
});

app.Run();

static string? ExtractModel(string? path)
{
    if (path is null) return null;
    var idx = path.IndexOf("/models/", StringComparison.Ordinal);
    if (idx < 0) return null;
    var start = idx + 8;
    var end = path.IndexOfAny([':', '?', '/'], start);
    return end > start ? path[start..end] : path[start..];
}

static string? HeaderValue(HttpRequest request, string name)
{
    var value = request.Headers[name].FirstOrDefault();
    return string.IsNullOrWhiteSpace(value) ? null : value;
}

public partial class Program { }
