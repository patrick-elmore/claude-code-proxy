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

    [Fact]
    public void Reload_WithValidJson_SwapsRatesLive()
    {
        var catalog = Build();

        var result = catalog.Reload("""
            {
              "models": {
                "test-model": {
                  "standard": {
                    "input": 5.0, "output": 25.0,
                    "cache_write_5m": 6.25, "cache_write_1h": 10.0, "cache_hit": 0.5
                  }
                }
              }
            }
            """);

        result.ShouldBeTrue();
        catalog.TryGetRates("test-model", out var rates).ShouldBeTrue();
        rates.Input.ShouldBe(5.0m);
        rates.Output.ShouldBe(25.0m);
    }

    [Fact]
    public void Reload_WithInvalidJson_ReturnsFalseAndKeepsOldRates()
    {
        var catalog = Build();

        var result = catalog.Reload("not valid json {{");

        result.ShouldBeFalse();
        // original rates still live
        catalog.TryGetRates("test-model", out var rates).ShouldBeTrue();
        rates.Input.ShouldBe(3.0m);
    }

    private const string TieredJson = """
        {
          "models": {
            "tiered": {
              "standard": {
                "input": 0.10, "output": 0.50,
                "cache_write_5m": 0.125, "cache_write_1h": 0.20, "cache_hit": 0.01
              },
              "long_context": {
                "input": 0.50, "output": 2.50,
                "cache_write_5m": 0.625, "cache_write_1h": 1.00, "cache_hit": 0.05
              },
              "long_context_threshold_input_tokens": 100000
            },
            "untiered": {
              "standard": {
                "input": 1.00, "output": 5.00,
                "cache_write_5m": 1.25, "cache_write_1h": 2.00, "cache_hit": 0.10
              },
              "long_context": {
                "input": 2.00, "output": 10.00,
                "cache_write_5m": 2.50, "cache_write_1h": 4.00, "cache_hit": 0.20
              }
            }
          }
        }
        """;

    [Fact]
    public void CalculateCostUsd_PromptAtTierThreshold_UsesStandardRates()
    {
        var catalog = Build();
        catalog.Reload(TieredJson);

        catalog.CalculateCostUsd("tiered", inputUncached: 100_000, cacheRead: 0, cacheWrite5m: 0, cacheWrite1h: 0, output: 0)
            .ShouldBe(0.01m);
    }

    [Fact]
    public void CalculateCostUsd_PromptOverTierThreshold_UsesLongContextRatesForAllTokens()
    {
        var catalog = Build();
        catalog.Reload(TieredJson);

        catalog.CalculateCostUsd("tiered", inputUncached: 100_001, cacheRead: 0, cacheWrite5m: 0, cacheWrite1h: 0, output: 0)
            .ShouldBe(0.0500005m);
    }

    [Fact]
    public void CalculateCostUsd_CacheReadsCountTowardPromptSize()
    {
        var catalog = Build();
        catalog.Reload(TieredJson);

        catalog.CalculateCostUsd("tiered", inputUncached: 50_000, cacheRead: 50_001, cacheWrite5m: 0, cacheWrite1h: 0, output: 0)
            .ShouldBe(0.02750005m);
    }

    private const string DatedChangeJson = """
        {
          "models": {
            "dated": {
              "standard": {
                "input": 2.00, "output": 10.00,
                "cache_write_5m": 2.50, "cache_write_1h": 4.00, "cache_hit": 0.10
              },
              "promotions": [
                {
                  "description": "Standard rates before 2026-10-07 update",
                  "starts": "2000-01-01",
                  "ends": "2026-10-06",
                  "rates": {
                    "input": 2.00, "output": 10.00,
                    "cache_write_5m": 2.50, "cache_write_1h": 4.00, "cache_hit": 0.20
                  }
                }
              ]
            }
          }
        }
        """;

    [Fact]
    public void CalculateCostUsd_DayBeforeChange_UsesPreviousRates()
    {
        var catalog = Build();
        catalog.Reload(DatedChangeJson);

        catalog.CalculateCostUsd("dated", inputUncached: 0, cacheRead: 1_000_000, cacheWrite5m: 0, cacheWrite1h: 0, output: 0,
            onDate: new DateOnly(2026, 10, 6)).ShouldBe(0.20m);
    }

    [Fact]
    public void CalculateCostUsd_ChangeDay_UsesNewRates()
    {
        var catalog = Build();
        catalog.Reload(DatedChangeJson);

        catalog.CalculateCostUsd("dated", inputUncached: 0, cacheRead: 1_000_000, cacheWrite5m: 0, cacheWrite1h: 0, output: 0,
            onDate: new DateOnly(2026, 10, 7)).ShouldBe(0.10m);
    }

    [Fact]
    public void CalculateCostUsd_ModelWithoutPerModelThreshold_IgnoresLongContext()
    {
        var catalog = Build();
        catalog.Reload(TieredJson);

        catalog.CalculateCostUsd("untiered", inputUncached: 200_000, cacheRead: 0, cacheWrite5m: 0, cacheWrite1h: 0, output: 0)
            .ShouldBe(0.20m);
    }
}
