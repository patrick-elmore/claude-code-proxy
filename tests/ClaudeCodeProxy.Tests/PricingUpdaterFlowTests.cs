using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace ClaudeCodeProxy.Tests;

public sealed class PricingUpdaterFlowTests : IDisposable
{
    private const string InitialJson = """
        {
          "currency": "USD",
          "models": {
            "claude-opus-5": {
              "display_name": "Claude Opus 5",
              "standard": {
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
            }
          }
        }
        """;

    // Raw markdown in the shape platform.claude.com serves, including <sup> footnote markers and link-bearing names.
    private const string Page = """
        # Pricing

        | Model | Base input tokens | 5m cache writes | 1h cache writes | Cache hits and refreshes | Output tokens |
        | :--- | :--- | :--- | :--- | :--- | :--- |
        | Claude Fable 5.1 | $10 / MTok | $12.50 / MTok | $20 / MTok | $0.25 / MTok<sup>1</sup> | $50 / MTok |
        | Claude Opus 5 | $5 / MTok | $6.25 / MTok | $10 / MTok | $0.50 / MTok | $25 / MTok |
        | Claude Sonnet 5 | $2 / MTok<sup>3</sup> | $2.50 / MTok | $4 / MTok | $0.20 / MTok | $10 / MTok<sup>3</sup> |
        | Claude Haiku 4.5 | $1 / MTok | $1.25 / MTok | $2 / MTok | $0.10 / MTok | $5 / MTok |
        | Claude Opus 4.1 ([retired](https://example.com/deprecations)) | $15 / MTok | $18.75 / MTok | $30 / MTok | $1.50 / MTok | $75 / MTok |

        *<sup>1 footnote</sup>*
        """;

    private readonly string _dir;
    private readonly string _path;
    private readonly PricingCatalog _catalog;

    public PricingUpdaterFlowTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "pricing.json");
        File.WriteAllText(_path, InitialJson);
        // A one-hour refresh interval proves updates reach the catalog through Invalidate, not the periodic check.
        _catalog = new PricingCatalog(NullLogger<PricingCatalog>.Instance, _path, TimeSpan.FromHours(1));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private Task Run(HttpStatusCode status, string body) => new PricingUpdater(
        _catalog, new StubFactory(status, body), NullLogger<PricingUpdater>.Instance, _path)
        .TryUpdateAsync(CancellationToken.None);

    [Fact]
    public async Task TryUpdateAsync_UnparseableResponse_LeavesFileUntouched()
    {
        await Run(HttpStatusCode.OK, "<html><body>Access denied</body></html>");

        File.ReadAllText(_path).ShouldBe(InitialJson);
    }

    [Fact]
    public async Task TryUpdateAsync_EmptyResponse_LeavesFileUntouched()
    {
        await Run(HttpStatusCode.OK, "");

        File.ReadAllText(_path).ShouldBe(InitialJson);
    }

    [Fact]
    public async Task TryUpdateAsync_ServerError_LeavesFileUntouched()
    {
        await Run(HttpStatusCode.InternalServerError, Page);

        File.ReadAllText(_path).ShouldBe(InitialJson);
    }

    [Fact]
    public async Task TryUpdateAsync_TableWithTooFewModels_LeavesFileUntouched()
    {
        var truncated = string.Join('\n', Page.Split('\n').Take(5));

        await Run(HttpStatusCode.OK, truncated);

        File.ReadAllText(_path).ShouldBe(InitialJson);
    }

    [Fact]
    public async Task TryUpdateAsync_RateJumpOverTenfold_LeavesFileUntouched()
    {
        await Run(HttpStatusCode.OK, Page.Replace("| Claude Opus 5 | $5 / MTok", "| Claude Opus 5 | $500 / MTok"));

        File.ReadAllText(_path).ShouldBe(InitialJson);
    }

    [Fact]
    public async Task TryUpdateAsync_UnparseableResponse_KeepsServingExistingRates()
    {
        await Run(HttpStatusCode.OK, "<html><body>Access denied</body></html>");

        _catalog.TryGetRates("claude-opus-5", out var rates);
        rates.Input.ShouldBe(5m);
    }

    [Fact]
    public async Task TryUpdateAsync_ChangedPage_WritesNewModelToFile()
    {
        await Run(HttpStatusCode.OK, Page);

        File.ReadAllText(_path).ShouldContain("\"claude-fable-5-1\"");
    }

    [Fact]
    public async Task TryUpdateAsync_ChangedPage_PreservesExistingPromotions()
    {
        await Run(HttpStatusCode.OK, Page);

        File.ReadAllText(_path).ShouldContain("launch promo");
    }

    [Fact]
    public async Task TryUpdateAsync_ChangedPage_InvalidatesCatalogSoNewModelResolves()
    {
        await Run(HttpStatusCode.OK, Page);

        _catalog.TryGetRates("claude-fable-5-1", out var rates);
        rates.CacheHit.ShouldBe(0.25m);
    }

    [Fact]
    public async Task TryUpdateAsync_ChangedPage_LeavesNoTempFile()
    {
        await Run(HttpStatusCode.OK, Page);

        File.Exists(_path + ".tmp").ShouldBeFalse();
    }

    [Fact]
    public async Task TryUpdateAsync_SamePageTwice_DoesNotRewriteFile()
    {
        await Run(HttpStatusCode.OK, Page);
        var stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(_path, stamp);

        await Run(HttpStatusCode.OK, Page);

        File.GetLastWriteTimeUtc(_path).ShouldBe(stamp);
    }

    private sealed class StubFactory(HttpStatusCode status, string body) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHandler(status, body), disposeHandler: true);
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }
}
