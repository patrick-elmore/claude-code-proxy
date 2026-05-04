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

builder.Services.AddHttpClient("vertex", client =>
{
    client.Timeout = Timeout.InfiniteTimeSpan;
});

builder.Services.AddSingleton<PromptRewriter>();
builder.Services.AddSingleton<RequestDumper>();

var app = builder.Build();

// Single catch-all. Mutation is body-shape-driven, not path-driven —
// any request whose body parses as JSON with a `system` array is a
// mutation candidate. Everything else is forwarded verbatim.
app.Map("{**path}", async (
    HttpContext ctx,
    IHttpClientFactory httpClientFactory,
    PromptRewriter rewriter,
    RequestDumper dumper,
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
    await response.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
});

app.Run();
