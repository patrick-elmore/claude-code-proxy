using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace ClaudeCodeProxy.Tests;

public sealed class PricingCatalogRefreshTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public PricingCatalogRefreshTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "pricing.json");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string PricingJson(decimal input) => $$"""
        {
          "models": {
            "test-model": {
              "standard": {
                "input": {{input}}, "output": 15.0,
                "cache_write_5m": 3.75, "cache_write_1h": 4.5, "cache_hit": 0.30
              }
            }
          }
        }
        """;

    // A fixed timestamp per revision guarantees the write time differs even on coarse-resolution filesystems.
    private void WriteRevision(string json, int revision)
    {
        File.WriteAllText(_path, json);
        File.SetLastWriteTimeUtc(_path, new DateTime(2026, 1, 1, 0, 0, revision, DateTimeKind.Utc));
    }

    private PricingCatalog Build(TimeSpan interval) =>
        new(NullLogger<PricingCatalog>.Instance, _path, interval);

    private static decimal InputRate(PricingCatalog catalog)
    {
        catalog.TryGetRates("test-model", out var rates);
        return rates.Input;
    }

    [Fact]
    public void TryGetRates_FileChangedAfterInterval_ReturnsNewRates()
    {
        WriteRevision(PricingJson(3.0m), 1);
        var catalog = Build(TimeSpan.Zero);

        WriteRevision(PricingJson(7.0m), 2);

        InputRate(catalog).ShouldBe(7.0m);
    }

    [Fact]
    public void TryGetRates_FileChangedBeforeIntervalElapses_KeepsOldRates()
    {
        WriteRevision(PricingJson(3.0m), 1);
        var catalog = Build(TimeSpan.FromHours(1));

        WriteRevision(PricingJson(7.0m), 2);

        InputRate(catalog).ShouldBe(3.0m);
    }

    [Fact]
    public void TryGetRates_FileRewrittenInvalid_KeepsOldRates()
    {
        WriteRevision(PricingJson(3.0m), 1);
        var catalog = Build(TimeSpan.Zero);

        WriteRevision("not valid json {{", 2);

        InputRate(catalog).ShouldBe(3.0m);
    }

    [Fact]
    public void TryGetRates_FileFixedAfterInvalidRewrite_ReturnsNewRates()
    {
        WriteRevision(PricingJson(3.0m), 1);
        var catalog = Build(TimeSpan.Zero);
        WriteRevision("not valid json {{", 2);
        InputRate(catalog);

        WriteRevision(PricingJson(9.0m), 3);

        InputRate(catalog).ShouldBe(9.0m);
    }

    [Fact]
    public void TryGetRates_FileCreatedAfterMissingAtStartup_ReturnsRates()
    {
        var catalog = Build(TimeSpan.Zero);

        WriteRevision(PricingJson(4.0m), 1);

        InputRate(catalog).ShouldBe(4.0m);
    }
}
