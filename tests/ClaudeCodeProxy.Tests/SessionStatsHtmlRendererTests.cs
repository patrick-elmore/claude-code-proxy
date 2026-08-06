using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace ClaudeCodeProxy.Tests;

public sealed class SessionStatsHtmlRendererTests
{
    [Fact]
    public void Render_Sessions_ShowsAgentHierarchyAndHonorsProviderFilter()
    {
        var pricing = new PricingCatalog(NullLogger<PricingCatalog>.Instance);
        var today = Snapshot("2026-08-03", 1) with
        {
            Sessions =
            [
                new SessionSnapshot("session-a", 3, 0, new TokenTotals(3_000_000, 0, 0, 0, 0),
                [
                    Agent(null, null, "vertex"),
                    Agent("parent-agent", null, "vertex"),
                    Agent("child-agent", "parent-agent", "anthropic"),
                ]),
                new SessionSnapshot(null, 1, 0, new TokenTotals(1_000_000, 0, 0, 0, 0),
                [
                    Agent("unattributed-agent", null, "vertex"),
                ]),
            ]
        };

        var metadata = new SessionMetadataSnapshot(new Dictionary<string, SessionMetadataSessionSnapshot>
        {
            ["session-a"] = new("Auth refactor", "status_line", "C:\\work\\auth", DateTimeOffset.UtcNow,
                new Dictionary<string, AgentMetadataSnapshot>
                {
                    ["parent-agent"] = new("Plan", DateTimeOffset.UtcNow),
                    ["child-agent"] = new("Explore", DateTimeOffset.UtcNow),
                }),
        });

        var allHtml = StatsHtmlRenderer.Render(today, pricing, "test", sessionMetadata: metadata);
        allHtml.ShouldContain("Claude Code sessions");
        allHtml.ShouldContain("Auth refactor");
        allHtml.ShouldContain("session-a");
        allHtml.ShouldContain("Main agent");
        allHtml.ShouldContain("Plan");
        allHtml.ShouldContain("Explore");
        allHtml.IndexOf("parent-agent", StringComparison.Ordinal)
            .ShouldBeLessThan(allHtml.IndexOf("child-agent", StringComparison.Ordinal));

        var vertexHtml = StatsHtmlRenderer.Render(today, pricing, "test", provider: "vertex", sessionMetadata: metadata);
        vertexHtml.ShouldContain("session-a");
        vertexHtml.ShouldContain("Plan");
        vertexHtml.ShouldContain("Unattributed");
        vertexHtml.ShouldNotContain("child-agent");
        vertexHtml.ShouldNotContain("Explore");
        vertexHtml.ShouldNotContain("unattributed-agent");
    }

    private static StatsSnapshot Snapshot(string date, double uptimeSeconds) => new(
        Date: date,
        StartedAt: DateTimeOffset.UtcNow,
        UptimeSeconds: uptimeSeconds,
        RequestCount: 1,
        FailedCount: 0,
        Totals: new TokenTotals(1_000_000, 0, 0, 0, 0),
        HitRatePct: 0,
        AvgDurationMs: 0,
        RewriteFailures: new Dictionary<PromptFailureMode, long>(),
        AcknowledgedFailures: new Dictionary<PromptFailureMode, long>(),
        UnknownPaths: [],
        ByModel: new Dictionary<string, ModelSnapshot>
        {
            ["test-model"] = new("test-model", 1, 0, 1_000_000, 0, 0, 0, 0, 0, 0, null)
        },
        Hourly: [],
        ByProvider: new Dictionary<string, ProviderTotals>(),
        ByEndpoint: new Dictionary<string, EndpointSnapshot>());

    private static AgentSnapshot Agent(string? agentId, string? parentAgentId, string provider)
    {
        var model = new ModelSnapshot("test-model", 1, 0, 1_000_000, 0, 0, 0, 0, 0, 0, provider);
        var providerTotals = new ProviderTotals(1, 0, 1_000_000, 0, 0, 0, 0);
        return new AgentSnapshot(agentId, parentAgentId, 1, 0,
            new TokenTotals(1_000_000, 0, 0, 0, 0),
            new Dictionary<string, ModelSnapshot> { [$"{provider}|test-model"] = model },
            new Dictionary<string, ProviderTotals> { [provider] = providerTotals });
    }
}
