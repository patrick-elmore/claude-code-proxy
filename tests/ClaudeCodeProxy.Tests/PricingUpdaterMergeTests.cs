using System.Text.Json.Nodes;
using Shouldly;

namespace ClaudeCodeProxy.Tests;

public sealed class PricingUpdaterMergeTests
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

    private static ParsedModel Opus5(decimal input = 5m) =>
        new("claude-opus-5", "Claude Opus 5", input, 25m, 6.25m, 10m, 0.5m);

    private static ParsedModel NewModel() =>
        new("claude-new-1", "Claude New 1", 3m, 15m, 3.75m, 6m, 0.3m);

    private static JsonNode Merge(string? existing, params ParsedModel[] models) =>
        JsonNode.Parse(PricingUpdater.MergeIntoExisting(existing, [.. models])!)!;

    [Fact]
    public void MergeIntoExisting_ChangedRate_UpdatesStandard()
    {
        var result = Merge(ExistingJson, Opus5(input: 6m));

        ((decimal)result["models"]!["claude-opus-5"]!["standard"]!["input"]!).ShouldBe(6m);
    }

    [Fact]
    public void MergeIntoExisting_ExistingPromotions_Preserved()
    {
        var result = Merge(ExistingJson, Opus5(input: 6m));

        ((string)result["models"]!["claude-opus-5"]!["promotions"]![0]!["description"]!).ShouldBe("launch promo");
    }

    [Fact]
    public void MergeIntoExisting_ExistingLongContext_Preserved()
    {
        var result = Merge(ExistingJson, Opus5(input: 6m));

        ((decimal)result["models"]!["claude-opus-5"]!["long_context"]!["input"]!).ShouldBe(5m);
    }

    [Fact]
    public void MergeIntoExisting_ModelAbsentFromPage_Kept()
    {
        var result = Merge(ExistingJson, Opus5(input: 6m));

        result["models"]!["retired-model"].ShouldNotBeNull();
    }

    [Fact]
    public void MergeIntoExisting_NewModel_InsertedFirst()
    {
        var result = Merge(ExistingJson, NewModel());

        result["models"]!.AsObject().First().Key.ShouldBe("claude-new-1");
    }

    [Fact]
    public void MergeIntoExisting_NotesWithNonAsciiText_WrittenUnescaped()
    {
        var json = PricingUpdater.MergeIntoExisting(ExistingJson, [Opus5(input: 6m)]);

        json!.ShouldContain("promo windows stay — historical stats need them");
    }

    [Fact]
    public void MergeIntoExisting_RatesAlreadyMatch_ReturnsNull()
    {
        PricingUpdater.MergeIntoExisting(ExistingJson, [Opus5()]).ShouldBeNull();
    }

    [Fact]
    public void MergeIntoExisting_NoExistingFile_BuildsDefaultRoot()
    {
        var result = Merge(null, NewModel());

        ((string)result["currency"]!).ShouldBe("USD");
    }
}
