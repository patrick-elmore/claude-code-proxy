using System.Net;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace ClaudeCodeProxy.IntegrationTests;

public sealed class StatsRouteTests : IClassFixture<ProxyWebApplicationFactory>
{
    private readonly ProxyWebApplicationFactory _factory;

    public StatsRouteTests(ProxyWebApplicationFactory factory) => _factory = factory;

    [Theory]
    [InlineData("/stats")]
    [InlineData("/stats/week")]
    [InlineData("/stats/month")]
    public async Task StatsPages_ReturnRazorHtml(string route)
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync(route);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.ToString().ShouldBe("text/html; charset=utf-8");
        var document = await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStringAsync());
        var stylesheet = document.QuerySelector("link[rel=stylesheet]")!.GetAttribute("href");
        stylesheet.ShouldNotBeNull();
        stylesheet!.ShouldStartWith("/css/stats");
        stylesheet.ShouldEndWith(".css");
    }

    [Fact]
    public async Task StatsRouteStatusAndStaticAssetContracts_AreExplicit()
    {
        var client = _factory.CreateClient();

        (await client.GetAsync("/stats/not-a-date")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.GetAsync("/stats/2000-01-01")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.GetAsync("/stats/week.json")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var css = await client.GetAsync("/css/stats.css");
        css.StatusCode.ShouldBe(HttpStatusCode.OK);
        css.Content.Headers.ContentType!.MediaType.ShouldBe("text/css");

        var favicon = await client.GetAsync("/favicon.svg");
        favicon.StatusCode.ShouldBe(HttpStatusCode.OK);
        favicon.Content.Headers.ContentType!.MediaType.ShouldBe("image/svg+xml");
    }

    [Fact]
    public void RazorPageEndpoints_AreExposedOnlyUnderStats()
    {
        var pageRoutes = _factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .Where(endpoint => endpoint.Metadata.GetMetadata<PageActionDescriptor>() is not null)
            .Select(endpoint => ((RouteEndpoint)endpoint).RoutePattern.RawText)
            .ToList();

        pageRoutes.Count.ShouldBe(5);
        pageRoutes.ShouldAllBe(route => route!.TrimStart('/').StartsWith("stats", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StatsNavigationAndForms_StayUnderStatsPrefix()
    {
        var tracker = _factory.Services.GetRequiredService<StatsTracker>();
        tracker.RecordRewriteOutcome(RewriteOutcome.Failure(PromptFailureMode.IdentityNotRecognized));
        var client = _factory.CreateClient();

        var html = await client.GetStringAsync("/stats?provider=vertex");
        var document = await new HtmlParser().ParseDocumentAsync(html);

        var pageLinks = document.QuerySelectorAll("nav a, footer a")
            .Select(link => link.GetAttribute("href"))
            .Where(href => href is not null && !href.StartsWith("/stats.json", StringComparison.Ordinal))
            .ToList();
        pageLinks.ShouldNotBeEmpty();
        pageLinks.ShouldAllBe(href => href!.StartsWith("/stats", StringComparison.Ordinal));

        var form = document.QuerySelector("form");
        form.ShouldNotBeNull();
        form!.GetAttribute("action").ShouldStartWith("/stats/dismiss/");
        form.QuerySelector("input[name=__RequestVerificationToken]").ShouldNotBeNull();
    }

    [Fact]
    public async Task StatsDismiss_RequiresAntiforgeryAndRedirectsToStats()
    {
        var tracker = _factory.Services.GetRequiredService<StatsTracker>();
        tracker.RecordRewriteOutcome(RewriteOutcome.Failure(PromptFailureMode.NoBulkBlock));
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var html = await client.GetStringAsync("/stats");
        var document = await new HtmlParser().ParseDocumentAsync(html);
        var form = document.QuerySelector("form[action*='NoBulkBlock']");
        form.ShouldNotBeNull();
        var token = form!.QuerySelector("input[name=__RequestVerificationToken]")!.GetAttribute("value")!;

        var response = await client.PostAsync(form.GetAttribute("action"),
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
            }));

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().ShouldBe("/stats");
    }

    [Fact]
    public async Task StatsRazor_EncodesHostileStatsAndUsesValidUnknownPathMarkup()
    {
        var tracker = _factory.Services.GetRequiredService<StatsTracker>();
        var metadata = _factory.Services.GetRequiredService<SessionMetadataStore>();
        tracker.Record(new RequestStats("hostile-request", DateTimeOffset.Now)
        {
            Model = "<script>alert('model')</script>",
            Provider = "vertex",
            UpstreamBaseUrl = "https://example.test/<img src=x onerror=alert(1)>",
            SessionId = "hostile-session",
            InputUncached = 1,
            HasUsage = true,
        });
        metadata.Upsert(new SessionMetadataUpdate(SessionMetadataEvent.StatusLine, "hostile-session",
            null, "<img src=x onerror=alert('session')>", null, null, null, DateTimeOffset.Now));
        tracker.RecordUnknownPath("/<svg onload=alert('path')>");
        var client = _factory.CreateClient();

        var html = await client.GetStringAsync("/stats");
        var document = await new HtmlParser().ParseDocumentAsync(html);

        document.QuerySelector("script").ShouldBeNull();
        document.QuerySelector("img").ShouldBeNull();
        document.QuerySelector("svg[onload]").ShouldBeNull();
        document.Body!.TextContent.ShouldContain("<script>alert('model')</script>");
        document.Body.TextContent.ShouldContain("<img src=x onerror=alert('session')>");
        var unknownPathList = document.QuerySelector("[data-failure-mode=UnknownPath] ul");
        unknownPathList.ShouldNotBeNull();
        unknownPathList!.ParentElement!.LocalName.ShouldBe("div");
    }

    [Fact]
    public async Task DailyStats_RendersExpectedSectionsSummariesAndTables()
    {
        var tracker = _factory.Services.GetRequiredService<StatsTracker>();
        tracker.Record(new RequestStats("parity-request", DateTimeOffset.Now)
        {
            Model = "claude-sonnet-4-5",
            Provider = "vertex",
            UpstreamBaseUrl = "https://example.test",
            InputUncached = 1_000,
            CacheRead = 2_000,
            CacheCreation5m = 300,
            CacheCreation1h = 400,
            OutputTokens = 500,
            DurationMs = 125,
            HasUsage = true,
        });
        tracker.RecordRewriteOutcome(RewriteOutcome.Success());
        var client = _factory.CreateClient();
        var document = await new HtmlParser().ParseDocumentAsync(await client.GetStringAsync("/stats"));

        Headings(document).ShouldContain("Today");
        Headings(document).ShouldContain("By model");
        SummaryPairs(document).ContainsKey("requests").ShouldBeTrue();
        TableMatrices(document).ShouldContain(table =>
            table.Any(row => row.Contains("claude-sonnet-4-5", StringComparison.Ordinal)));
    }

    private static string[] Headings(AngleSharp.Dom.IDocument document) =>
        document.QuerySelectorAll("h2").Select(element => Normalize(element.TextContent)).ToArray();

    private static Dictionary<string, string> SummaryPairs(AngleSharp.Dom.IDocument document) =>
        document.QuerySelectorAll(".pair").ToDictionary(
            element => Normalize(element.QuerySelector(".k")!.TextContent),
            element => Normalize(element.QuerySelector(".v")!.TextContent),
            StringComparer.Ordinal);

    private static string[][] TableMatrices(AngleSharp.Dom.IDocument document) =>
        document.QuerySelectorAll("table").Select(table =>
            table.QuerySelectorAll("tr").Select(row => string.Join("|",
                row.QuerySelectorAll("th,td").Select(cell => NormalizeCell(cell.TextContent)))).ToArray()).ToArray();

    private static string Normalize(string value) => string.Join(" ",
        value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string NormalizeCell(string value) => string.Concat(
        value.Where(character => !char.IsWhiteSpace(character)));
}
