using System.Net;
using AngleSharp.Html.Parser;
using Shouldly;

namespace ClaudeCodeProxy.IntegrationTests;

public sealed class PricingRouteTests : IClassFixture<ProxyWebApplicationFactory>
{
    private readonly ProxyWebApplicationFactory _factory;

    public PricingRouteTests(ProxyWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Pricing_RendersRateScheduleSection()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/pricing");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var document = await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStringAsync());
        document.QuerySelectorAll("h2").Select(h => h.TextContent).ShouldContain(h => h.StartsWith("Rate schedule", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pricing_DateQuery_SetsDateInput()
    {
        var client = _factory.CreateClient();

        var html = await client.GetStringAsync("/pricing?date=2026-10-06");
        var document = await new HtmlParser().ParseDocumentAsync(html);

        document.QuerySelector("input[name=date]")!.GetAttribute("value").ShouldBe("2026-10-06");
    }
}
