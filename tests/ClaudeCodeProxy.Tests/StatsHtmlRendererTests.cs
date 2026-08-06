using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace ClaudeCodeProxy.Tests;

public sealed class StatsHtmlRendererTests
{
    [Fact]
    public void Render_CurrentMonthSpend_ShowsDailyAverageAndConfiguredTarget()
    {
        var pricing = new PricingCatalog(NullLogger<PricingCatalog>.Instance);
        var historical = Snapshot("2026-08-01", -1);
        var today = Snapshot("2026-08-03", 1);

        var html = StatsHtmlRenderer.Render(today, pricing, "test",
            currentMonthSnapshots: [historical, today], monthlyTargetSpendUsd: 3100m);

        html.ShouldContain("August 2026 daily spend");
        html.ShouldContain("daily average cost</span><span class=\"v cost\">$2.00");
        html.ShouldContain("target daily spend</span><span class=\"v cost\">$100.00");
        html.ShouldContain("$3,100.00 monthly target / 31 days");
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
}
