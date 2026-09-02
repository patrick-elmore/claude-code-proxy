using Shouldly;

namespace ClaudeCodeProxy.Tests;

public sealed class PricingUpdaterTests
{
    // Realistic markdown matching the format returned by the Anthropic pricing docs page.
    private const string SampleMarkdown = """
        # Pricing

        Some intro text.

        | Model | Base input tokens | 5m cache writes | 1h cache writes | Cache hits and refreshes | Output tokens |
        | --- | --- | --- | --- | --- | --- |
        | Claude Fable 5.1 | $10 / MTok | $12.50 / MTok | $20 / MTok | $0.25 / MTok1 | $50 / MTok |
        | Claude Mythos 5.1 ([limited availability](https://anthropic.com/glasswing)) | $10 / MTok | $12.50 / MTok | $20 / MTok | $0.25 / MTok1 | $50 / MTok |
        | Claude Opus 5 | $5 / MTok | $6.25 / MTok | $10 / MTok | $0.50 / MTok | $25 / MTok |
        | Claude Sonnet 5 | $2 / MTok | $2.50 / MTok | $4 / MTok | $0.20 / MTok | $10 / MTok |
        | Claude Haiku 4.5 | $1 / MTok | $1.25 / MTok | $2 / MTok | $0.10 / MTok | $5 / MTok |
        | Claude Opus 4.1 ([retired, except on Bedrock and Google Cloud](https://platform.claude.com/docs/en/about-claude/model-deprecations)) | $15 / MTok | $18.75 / MTok | $30 / MTok | $1.50 / MTok | $75 / MTok |

        Some footnote text.
        """;


    // --- ParsePricingPage ---

    [Fact]
    public void ParsePricingPage_Markdown_ExtractsCorrectModelCount()
    {
        var models = PricingUpdater.ParsePricingPage(SampleMarkdown);

        models.Count.ShouldBe(6);
    }

    [Fact]
    public void ParsePricingPage_Markdown_CorrectRatesForFable51()
    {
        var models = PricingUpdater.ParsePricingPage(SampleMarkdown);
        var fable = models.Single(m => m.ModelId == "claude-fable-5-1");

        fable.Input.ShouldBe(10m);
        fable.Output.ShouldBe(50m);
        fable.CacheWrite5m.ShouldBe(12.50m);
        fable.CacheWrite1h.ShouldBe(20m);
        fable.CacheHit.ShouldBe(0.25m);
    }

    [Fact]
    public void ParsePricingPage_Markdown_FootnoteMarkerStrippedFromPrice()
    {
        // "$0.25 / MTok1" should parse as 0.25, not fail
        var models = PricingUpdater.ParsePricingPage(SampleMarkdown);
        var fable = models.Single(m => m.ModelId == "claude-fable-5-1");

        fable.CacheHit.ShouldBe(0.25m);
    }

    [Fact]
    public void ParsePricingPage_Markdown_ModelWithMarkdownLinkInParens_Parsed()
    {
        var models = PricingUpdater.ParsePricingPage(SampleMarkdown);
        var mythos = models.SingleOrDefault(m => m.ModelId == "claude-mythos-5-1");

        mythos.ShouldNotBeNull();
        mythos.DisplayName.ShouldBe("Claude Mythos 5.1");
    }

    [Fact]
    public void ParsePricingPage_Markdown_RetiredModelStripped()
    {
        var models = PricingUpdater.ParsePricingPage(SampleMarkdown);
        var opus41 = models.SingleOrDefault(m => m.ModelId == "claude-opus-4-1");

        opus41.ShouldNotBeNull();
        opus41.DisplayName.ShouldBe("Claude Opus 4.1");
    }

    // --- ToModelId ---

    [Theory]
    [InlineData("Claude Fable 5.1", "claude-fable-5-1")]
    [InlineData("Claude Fable 5", "claude-fable-5")]
    [InlineData("Claude Opus 5", "claude-opus-5")]
    [InlineData("Claude Sonnet 4.6", "claude-sonnet-4-6")]
    [InlineData("Claude Haiku 4.5", "claude-haiku-4-5")]
    [InlineData("Claude Opus 4.1", "claude-opus-4-1")]
    public void ToModelId_VariousDisplayNames_ConvertsCorrectly(string displayName, string expected)
    {
        PricingUpdater.ToModelId(displayName).ShouldBe(expected);
    }

    // --- CleanModelName ---

    [Fact]
    public void CleanModelName_PlainName_Unchanged()
    {
        PricingUpdater.CleanModelName("Claude Opus 5").ShouldBe("Claude Opus 5");
    }

    [Fact]
    public void CleanModelName_MarkdownLinkInParens_ParenAndLinkStripped()
    {
        var input = "Claude Mythos 5.1 ([limited availability](https://anthropic.com/glasswing))";
        PricingUpdater.CleanModelName(input).ShouldBe("Claude Mythos 5.1");
    }

    [Fact]
    public void CleanModelName_ComplexRetiredParenthetical_Stripped()
    {
        var input = "Claude Opus 4.1 ([retired, except on Bedrock and Google Cloud](https://example.com))";
        PricingUpdater.CleanModelName(input).ShouldBe("Claude Opus 4.1");
    }

    // --- ExtractPrice ---

    [Theory]
    [InlineData("$10 / MTok", 10)]
    [InlineData("$12.50 / MTok", 12.50)]
    [InlineData("$0.25 / MTok1", 0.25)]   // footnote marker after MTok
    [InlineData("$0.08 / MTok", 0.08)]
    public void ExtractPrice_ValidInputs_ParsesCorrectly(string cell, decimal expected)
    {
        PricingUpdater.ExtractPrice(cell).ShouldBe(expected);
    }

    [Fact]
    public void ExtractPrice_NoPriceInCell_ReturnsNull()
    {
        PricingUpdater.ExtractPrice("some text without a dollar amount").ShouldBeNull();
    }

    // --- RatesWithinFactor ---

    [Fact]
    public void RatesWithinFactor_NoChange_ReturnsTrue()
    {
        var existing = new PricingCatalog.RateBlock(10m, 50m, 12.5m, 20m, 1m);
        var updated = new ParsedModel("m", "M", 10m, 50m, 12.5m, 20m, 1m);

        PricingUpdater.RatesWithinFactor(existing, updated, 10m).ShouldBeTrue();
    }

    [Fact]
    public void RatesWithinFactor_DoubledRates_ReturnsTrue()
    {
        var existing = new PricingCatalog.RateBlock(10m, 50m, 12.5m, 20m, 1m);
        var updated = new ParsedModel("m", "M", 20m, 100m, 25m, 40m, 2m);

        PricingUpdater.RatesWithinFactor(existing, updated, 10m).ShouldBeTrue();
    }

    [Fact]
    public void RatesWithinFactor_OneRateOver10x_ReturnsFalse()
    {
        var existing = new PricingCatalog.RateBlock(10m, 50m, 12.5m, 20m, 1m);
        // output jumped 11×
        var updated = new ParsedModel("m", "M", 10m, 550m, 12.5m, 20m, 1m);

        PricingUpdater.RatesWithinFactor(existing, updated, 10m).ShouldBeFalse();
    }

    [Fact]
    public void RatesWithinFactor_RateDroppedBelow10thFactor_ReturnsFalse()
    {
        var existing = new PricingCatalog.RateBlock(10m, 50m, 12.5m, 20m, 1m);
        // input dropped to 1/11th
        var updated = new ParsedModel("m", "M", 0.9m, 50m, 12.5m, 20m, 1m);

        PricingUpdater.RatesWithinFactor(existing, updated, 10m).ShouldBeFalse();
    }
}
