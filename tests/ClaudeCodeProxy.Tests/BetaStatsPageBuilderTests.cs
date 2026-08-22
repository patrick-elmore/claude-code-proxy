using ClaudeCodeProxy.Beta;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace ClaudeCodeProxy.Tests;

public sealed class BetaStatsPageBuilderTests
{
    private readonly PricingCatalog _pricing = new(NullLogger<PricingCatalog>.Instance);

    [Fact]
    public void BuildDaily_ComputesPromptCountsAndProviderFilteredTotals()
    {
        var snapshot = Snapshot() with
        {
            InjectedRequests = 12,
            PassedThroughRequests = 3,
            ByModel = new Dictionary<string, ModelSnapshot>
            {
                ["vertex|claude-sonnet-4-5"] = Model("claude-sonnet-4-5", "vertex", requests: 2, input: 100),
                ["anthropic|claude-sonnet-4-5"] = Model("claude-sonnet-4-5", "anthropic", requests: 5, input: 200),
            },
        };

        var view = new BetaStatsPageBuilder(_pricing).BuildDaily(snapshot, null, null, "vertex",
            null, 5500m, null);

        view.Summary.Requests.ShouldBe(2);
        view.Summary.InputTokens.ShouldBe(100);
        view.Summary.InjectedRequests.ShouldBe(12);
        view.Summary.PassedThroughRequests.ShouldBe(3);
        view.Summary.PromptCountsAreGlobal.ShouldBeTrue();
        view.Models.Count.ShouldBe(1);
        view.Models[0].Provider.ShouldBe("vertex");
    }

    [Fact]
    public void BuildRange_AggregatesEachSnapshotAndRetainsGlobalPromptCounts()
    {
        var first = Snapshot("2026-08-18", historical: true) with
        {
            InjectedRequests = 2,
            PassedThroughRequests = 1,
        };
        var second = Snapshot("2026-08-19") with
        {
            InjectedRequests = 4,
            PassedThroughRequests = 3,
        };

        var view = new BetaStatsPageBuilder(_pricing).BuildRange(
            [first, second], "Last 7 days", "/Beta/Stats/Week", null, null);

        view.Summary.Requests.ShouldBe(2);
        view.Summary.InjectedRequests.ShouldBe(6);
        view.Summary.PassedThroughRequests.ShouldBe(4);
        view.Days.Select(day => day.Date).ShouldBe(["2026-08-19", "2026-08-18"]);
    }

    [Fact]
    public void BuildDaily_FlattensNestedMissingAndCyclicAgentsWithoutDroppingRows()
    {
        var snapshot = Snapshot() with
        {
            Sessions =
            [
                new SessionSnapshot("session-a", 4, 0, new TokenTotals(4, 0, 0, 0, 0),
                [
                    Agent(null, null),
                    Agent("parent", null),
                    Agent("child", "parent"),
                    Agent("orphan", "missing"),
                    Agent("cycle-a", "cycle-b"),
                    Agent("cycle-b", "cycle-a"),
                ]),
            ],
        };

        var view = new BetaStatsPageBuilder(_pricing).BuildDaily(snapshot, null, null, null,
            null, 5500m, null);

        view.Sessions.Count.ShouldBe(7);
        view.Sessions.Single(row => row.Id == "child").Depth.ShouldBe(2);
        view.Sessions.Single(row => row.Id == "orphan").ParentMissing.ShouldBeTrue();
        view.Sessions.Count(row => row.Id is "cycle-a" or "cycle-b").ShouldBe(2);
    }

    private static StatsSnapshot Snapshot(string date = "2026-08-19", bool historical = false) => new(
        date,
        DateTimeOffset.UtcNow,
        historical ? -1 : 1,
        1,
        0,
        new TokenTotals(1_000_000, 0, 0, 0, 0),
        0,
        25,
        new Dictionary<PromptFailureMode, long>(),
        new Dictionary<PromptFailureMode, long>(),
        [],
        new Dictionary<string, ModelSnapshot>
        {
            ["vertex|claude-sonnet-4-5"] = Model("claude-sonnet-4-5", "vertex", 1, 1_000_000),
        },
        [],
        new Dictionary<string, ProviderTotals>(),
        new Dictionary<string, EndpointSnapshot>());

    private static ModelSnapshot Model(string name, string provider, long requests, long input) =>
        new(name, requests, 0, input, 0, 0, 0, 0, 0, 25, provider);

    private static AgentSnapshot Agent(string? id, string? parent) =>
        new(id, parent, 1, 0, new TokenTotals(1, 0, 0, 0, 0),
            new Dictionary<string, ModelSnapshot>
            {
                ["vertex|claude-sonnet-4-5"] = Model("claude-sonnet-4-5", "vertex", 1, 1),
            },
            new Dictionary<string, ProviderTotals>
            {
                ["vertex"] = new(1, 0, 1, 0, 0, 0, 0),
            });
}
