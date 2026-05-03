using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ClaudeCodeProxy;
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

builder.Services.AddHttpClient("anthropic", client =>
{
    client.BaseAddress = new Uri("https://api.anthropic.com");
    client.Timeout = Timeout.InfiniteTimeSpan;
});

builder.Services.AddSingleton<PromptRewriter>();
builder.Services.AddSingleton<RequestDumper>();

var app = builder.Build();

// Primary route — mutate system prompt then forward
app.MapPost("/v1/messages", async (
    HttpContext ctx,
    IHttpClientFactory httpClientFactory,
    PromptRewriter rewriter,
    RequestDumper dumper) =>
{
    var node = await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);

    await dumper.DumpAsync("original", node, ctx.RequestAborted);
    await rewriter.RewriteAsync(node);
    await dumper.DumpAsync("mutated", node, ctx.RequestAborted);

    var upstream = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
    {
        Content = new StringContent(node?.ToJsonString() ?? "{}", Encoding.UTF8, "application/json")
    };
    HeaderCopying.CopyRequestHeaders(ctx.Request, upstream);

    var client = httpClientFactory.CreateClient("anthropic");
    using var response = await client.SendAsync(
        upstream, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);

    ctx.Response.StatusCode = (int)response.StatusCode;
    HeaderCopying.CopyResponseHeaders(response, ctx.Response);
    await response.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
});

// Transparent passthrough for all other paths (e.g. /v1/models, Vertex paths)
// Buffers the body when dumping is enabled so the wire format can be inspected.
app.Map("{**catchAll}", async (HttpContext ctx, IHttpClientFactory httpClientFactory, RequestDumper dumper) =>
{
    var upstream = new HttpRequestMessage(
        new HttpMethod(ctx.Request.Method),
        ctx.Request.Path.Value + ctx.Request.QueryString.Value);

    var hasBody = ctx.Request.ContentLength is > 0
        || ctx.Request.Headers.ContainsKey("Transfer-Encoding");

    if (hasBody)
    {
        // Buffer so we can both dump and forward
        using var ms = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
        var bytes = ms.ToArray();

        await dumper.DumpRawAsync(
            ctx.Request.Path.Value + ctx.Request.QueryString.Value,
            bytes, ctx.RequestAborted);

        upstream.Content = new ByteArrayContent(bytes);
        if (ctx.Request.ContentType is { Length: > 0 } ct)
            upstream.Content.Headers.TryAddWithoutValidation("Content-Type", ct);
    }

    HeaderCopying.CopyRequestHeaders(ctx.Request, upstream);

    var client = httpClientFactory.CreateClient("anthropic");
    using var response = await client.SendAsync(
        upstream, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);

    ctx.Response.StatusCode = (int)response.StatusCode;
    HeaderCopying.CopyResponseHeaders(response, ctx.Response);
    await response.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
});

app.Run();
