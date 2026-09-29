using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace ClaudeCodeProxy.Tests;

// The tests/pricing.json fixture gives the catalog one known model: test-model (input 3.0, output 15.0,
// cache_write_5m 3.75, cache_write_1h 4.5, cache_hit 0.30).
public sealed class PricingUpdaterGuardTests
{
    private static readonly PricingCatalog Catalog = new(NullLogger<PricingCatalog>.Instance);

    private static ParsedModel Model(string id, decimal input = 3m) =>
        new(id, id, input, 15m, 3.75m, 4.5m, 0.3m);

    private static List<ParsedModel> Healthy() => [Model("a"), Model("b"), Model("c"), Model("d")];

    [Fact]
    public void FindProblem_HealthyModels_ReturnsNull()
    {
        PricingUpdater.FindProblem(Healthy(), Catalog).ShouldBeNull();
    }

    [Fact]
    public void FindProblem_TooFewModels_ReportsCount()
    {
        PricingUpdater.FindProblem([Model("a"), Model("b")], Catalog)!.ShouldContain("only 2 models");
    }

    [Fact]
    public void FindProblem_DuplicateModelId_ReportsDuplicate()
    {
        PricingUpdater.FindProblem([.. Healthy(), Model("a")], Catalog)!.ShouldContain("'a' appears more than once");
    }

    [Fact]
    public void FindProblem_NonPositiveRate_ReportsModel()
    {
        PricingUpdater.FindProblem([.. Healthy(), Model("e", input: 0m)], Catalog)!.ShouldContain("'e' has a non-positive rate");
    }

    [Fact]
    public void FindProblem_RateChangedMoreThanTenfoldForKnownModel_ReportsModel()
    {
        PricingUpdater.FindProblem([.. Healthy(), Model("test-model", input: 40m)], Catalog)!.ShouldContain("'test-model' has a rate change exceeding");
    }

    [Fact]
    public void FindProblem_RateChangedWithinTenfoldForKnownModel_ReturnsNull()
    {
        PricingUpdater.FindProblem([.. Healthy(), Model("test-model", input: 20m)], Catalog).ShouldBeNull();
    }
}
