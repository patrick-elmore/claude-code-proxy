using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace ClaudeCodeProxy.Tests;

// PricingCatalog loads pricing.json from AppContext.BaseDirectory.
// The tests/pricing.json fixture (CopyToOutputDirectory=Always) provides known rates.
public sealed class PricingCatalogTests
{
    private static PricingCatalog Build() => new(NullLogger<PricingCatalog>.Instance);

    [Fact]
    public void CalculateCostUsd_KnownModel_CorrectCost()
    {
        var catalog = Build();
        // rates: input=3.0, output=15.0, cache_write_5m=3.75, cache_write_1h=4.5, cache_hit=0.30 (per 1M tokens)
        var cost = catalog.CalculateCostUsd("test-model",
            inputUncached: 1_000_000,
            cacheRead:     1_000_000,
            cacheWrite5m:  1_000_000,
            cacheWrite1h:  1_000_000,
            output:        1_000_000);

        cost.ShouldBe(3.0m + 0.30m + 3.75m + 4.5m + 15.0m);
    }

    [Fact]
    public void CalculateCostUsd_UnknownModel_ReturnsZero()
    {
        var catalog = Build();

        catalog.CalculateCostUsd("no-such-model", 1_000_000, 0, 0, 0, 0).ShouldBe(0m);
    }

    [Fact]
    public void CalculateCostUsd_NullModel_ReturnsZero()
    {
        var catalog = Build();

        catalog.CalculateCostUsd(null, 1_000_000, 0, 0, 0, 0).ShouldBe(0m);
    }

    [Fact]
    public void TryGetRates_KnownModel_ReturnsTrueWithRates()
    {
        var catalog = Build();

        var found = catalog.TryGetRates("test-model", out var rates);

        found.ShouldBeTrue();
        rates.Input.ShouldBe(3.0m);
        rates.Output.ShouldBe(15.0m);
        rates.CacheHit.ShouldBe(0.30m);
        rates.CacheWrite5m.ShouldBe(3.75m);
        rates.CacheWrite1h.ShouldBe(4.5m);
    }

    [Fact]
    public void TryGetRates_VersionSuffix_Stripped()
    {
        var catalog = Build();

        var found = catalog.TryGetRates("test-model@20260101", out var rates);

        found.ShouldBeTrue();
        rates.Input.ShouldBe(3.0m);
    }

    [Fact]
    public void TryGetRates_UnknownModel_ReturnsFalse()
    {
        var catalog = Build();

        catalog.TryGetRates("no-such-model", out _).ShouldBeFalse();
    }
}
