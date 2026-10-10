using System.Text.Json.Nodes;
using Shouldly;

namespace ClaudeCodeProxy.Tests;

public sealed class PricingUpdaterGenerateTests
{
    private const string ExistingJson = """
        {
          "notes": ["promo windows stay — historical stats need them"],
          "models": {
            "claude-opus-5": {
              "display_name": "Claude Opus 5",
              "standard": {
                "input": 5.00, "output": 25.00,
                "cache_write_5m": 6.25, "cache_write_1h": 10.00, "cache_hit": 0.50
              },
              "long_context": {
                "input": 5.00, "output": 25.00,
                "cache_write_5m": 6.25, "cache_write_1h": 10.00, "cache_hit": 0.50
              },
              "promotions": [
                {
                  "description": "launch promo",
                  "starts": "2026-01-01",
                  "ends": "2026-01-31",
                  "rates": {
                    "input": 4.00, "output": 20.00,
                    "cache_write_5m": 5.00, "cache_write_1h": 8.00, "cache_hit": 0.40
                  }
                }
              ]
            },
            "retired-model": {
              "display_name": "Retired Model",
              "standard": {
                "input": 1.00, "output": 5.00,
                "cache_write_5m": 1.25, "cache_write_1h": 2.00, "cache_hit": 0.10
              }
            }
          }
        }
        """;

    private static ParsedModel Opus5(decimal input = 6m) =>
        new("claude-opus-5", "Claude Opus 5", input, 25m, 6.25m, 10m, 0.5m);

    private static ParsedModel NewModel() =>
        new("claude-new-1", "Claude New 1", 3m, 15m, 3.75m, 6m, 0.3m);

    private static ParsedModel TieredHaiku() =>
        new("claude-haiku-5-5", "Claude Haiku 5.5", 0.10m, 0.50m, 0.125m, 0.20m, 0.01m,
            new LongContextRates(100000, 0.50m, 2.50m, 0.625m, 1m, 0.05m));

    private static readonly DateOnly EffectiveDate = new(2026, 10, 7);

    private static string Generate(string? existing, params ParsedModel[] models) =>
        PricingUpdater.GeneratePricingJson(existing, models, EffectiveDate);

    private static JsonNode Parse(string json) => JsonNode.Parse(json)!;

    [Fact]
    public void GeneratePricingJson_ChangedRate_UpdatesStandard()
    {
        var result = Parse(Generate(ExistingJson, Opus5(input: 6m)));

        ((decimal)result["models"]!["claude-opus-5"]!["standard"]!["input"]!).ShouldBe(6m);
    }

    [Fact]
    public void GeneratePricingJson_ExistingPromotions_Preserved()
    {
        var result = Parse(Generate(ExistingJson, Opus5()));

        ((string)result["models"]!["claude-opus-5"]!["promotions"]![0]!["description"]!).ShouldBe("launch promo");
    }

    [Fact]
    public void GeneratePricingJson_ExistingLongContext_Preserved()
    {
        var result = Parse(Generate(ExistingJson, Opus5()));

        ((decimal)result["models"]!["claude-opus-5"]!["long_context"]!["input"]!).ShouldBe(5m);
    }

    [Fact]
    public void GeneratePricingJson_TieredModel_WritesLongContextRates()
    {
        var result = Parse(Generate(null, TieredHaiku()));

        ((decimal)result["models"]!["claude-haiku-5-5"]!["long_context"]!["input"]!).ShouldBe(0.50m);
    }

    [Fact]
    public void GeneratePricingJson_TieredModel_WritesPerModelThreshold()
    {
        var result = Parse(Generate(null, TieredHaiku()));

        ((int)result["models"]!["claude-haiku-5-5"]!["long_context_threshold_input_tokens"]!).ShouldBe(100000);
    }

    [Fact]
    public void GeneratePricingJson_TieredModelWithExistingLongContext_PageValuesReplaceExisting()
    {
        var result = Parse(Generate(ExistingJson, Opus5() with { LongContext = new LongContextRates(200000, 7m, 25m, 6.25m, 10m, 0.5m) }));

        ((decimal)result["models"]!["claude-opus-5"]!["long_context"]!["input"]!).ShouldBe(7m);
    }

    [Fact]
    public void GeneratePricingJson_StandardChanged_SetsEffectiveFrom()
    {
        var result = Parse(Generate(ExistingJson, Opus5(input: 6m)));

        ((string)result["models"]!["claude-opus-5"]!["standard_effective_from"]!).ShouldBe("2026-10-07");
    }

    [Fact]
    public void GeneratePricingJson_StandardChanged_PreviousRatesEndDayBeforeEffectiveDate()
    {
        var result = Parse(Generate(ExistingJson, Opus5(input: 6m)));

        ((string)result["models"]!["claude-opus-5"]!["promotions"]![1]!["ends"]!).ShouldBe("2026-10-06");
    }

    [Fact]
    public void GeneratePricingJson_StandardChanged_PreviousRatesStartAtLegacyStart()
    {
        var result = Parse(Generate(ExistingJson, Opus5(input: 6m)));

        ((string)result["models"]!["claude-opus-5"]!["promotions"]![1]!["starts"]!).ShouldBe("2000-01-01");
    }

    [Fact]
    public void GeneratePricingJson_StandardChanged_PreviousRatesKeptAsWindowRates()
    {
        var result = Parse(Generate(ExistingJson, Opus5(input: 6m)));

        ((decimal)result["models"]!["claude-opus-5"]!["promotions"]![1]!["rates"]!["input"]!).ShouldBe(5m);
    }

    [Fact]
    public void GeneratePricingJson_StandardChangedAgain_WindowStartsAtPreviousEffectiveDate()
    {
        const string previouslyChanged = """
            {
              "models": {
                "claude-opus-5": {
                  "display_name": "Claude Opus 5",
                  "standard": {
                    "input": 6.00, "output": 25.00,
                    "cache_write_5m": 6.25, "cache_write_1h": 10.00, "cache_hit": 0.50
                  },
                  "standard_effective_from": "2026-09-01",
                  "promotions": []
                }
              }
            }
            """;

        var result = Parse(Generate(previouslyChanged, Opus5(input: 7m)));

        ((string)result["models"]!["claude-opus-5"]!["promotions"]![0]!["starts"]!).ShouldBe("2026-09-01");
    }

    [Fact]
    public void GeneratePricingJson_StandardUnchanged_NoHistoryWindowAdded()
    {
        var result = Parse(Generate(ExistingJson, Opus5(input: 5m)));

        result["models"]!["claude-opus-5"]!["promotions"]!.AsArray().Count.ShouldBe(1);
    }

    [Fact]
    public void GeneratePricingJson_NewModel_SetsEffectiveFromWithoutHistory()
    {
        var result = Parse(Generate(ExistingJson, NewModel()));

        ((string)result["models"]!["claude-new-1"]!["standard_effective_from"]!).ShouldBe("2026-10-07");
    }

    [Fact]
    public void GeneratePricingJson_ModelAbsentFromPage_Kept()
    {
        var result = Parse(Generate(ExistingJson, Opus5()));

        result["models"]!["retired-model"].ShouldNotBeNull();
    }

    [Fact]
    public void GeneratePricingJson_PageModelsListedBeforeCarriedModels()
    {
        var result = Parse(Generate(ExistingJson, NewModel(), Opus5()));

        result["models"]!.AsObject().Select(p => p.Key).ShouldBe(["claude-new-1", "claude-opus-5", "retired-model"]);
    }

    [Fact]
    public void GeneratePricingJson_TopLevelNotesWithNonAsciiText_WrittenUnescaped()
    {
        Generate(ExistingJson, Opus5()).ShouldContain("promo windows stay — historical stats need them");
    }

    [Fact]
    public void GeneratePricingJson_NoExistingFile_UsesDefaultRoot()
    {
        var result = Parse(Generate(null, NewModel()));

        ((string)result["currency"]!).ShouldBe("USD");
    }

    [Fact]
    public void GeneratePricingJson_ExistingFileUnparseable_UsesDefaultRoot()
    {
        var result = Parse(Generate("not valid json {{", NewModel()));

        ((string)result["currency"]!).ShouldBe("USD");
    }

    [Fact]
    public void GeneratePricingJson_OutputFedBackAsExisting_IsIdentical()
    {
        var first = Generate(ExistingJson, NewModel(), Opus5());

        Generate(first, NewModel(), Opus5()).ShouldBe(first);
    }

    [Fact]
    public void GeneratePricingJson_Output_UsesLfNewlinesOnly()
    {
        Generate(ExistingJson, Opus5()).ShouldNotContain("\r");
    }

    [Fact]
    public void GeneratePricingJson_Rates_FormattedToAtLeastTwoDecimals()
    {
        Generate(null, Opus5(input: 6m)).ShouldContain("\"input\": 6.00");
    }

    [Fact]
    public void GeneratePricingJson_DuplicateModelIds_Throws()
    {
        Should.Throw<InvalidOperationException>(() => Generate(ExistingJson, Opus5(), Opus5()));
    }

    [Fact]
    public void GeneratePricingJson_Output_PassesCatalogValidation()
    {
        PricingCatalog.IsValid(Generate(ExistingJson, NewModel(), Opus5())).ShouldBeTrue();
    }
}
