using System.Text.Json;

namespace ClaudeCodeProxy;

/// <summary>
/// Loads <c>pricing.json</c> from the app base directory once at startup and exposes
/// per-model rate lookup + cost calculation. Vertex paths sometimes carry an
/// <c>@version</c> suffix on the model name; lookup strips that before matching.
///
/// Only the standard tier is applied — the proxy doesn't track per-request input totals,
/// so it can't tell which requests crossed the 200K long-context threshold. For the
/// dominant model (Opus 4.7) the long-context rate is identical, so this is exact.
/// For Sonnet 4.5 it would undercount cost on >=200K requests.
/// </summary>
public sealed class PricingCatalog
{
    private readonly Dictionary<string, RateBlock> _standardRates;
    private readonly ILogger<PricingCatalog> _logger;
    private readonly HashSet<string> _missingModels = new();
    private readonly Lock _missingLock = new();

    public PricingCatalog(ILogger<PricingCatalog> logger)
    {
        _logger = logger;
        var path = Path.Combine(AppContext.BaseDirectory, "pricing.json");
        if (!File.Exists(path))
        {
            _logger.LogWarning("pricing.json not found at {Path}; cost calculation will return $0", path);
            _standardRates = new Dictionary<string, RateBlock>();
            return;
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        _standardRates = new Dictionary<string, RateBlock>();
        foreach (var prop in doc.RootElement.GetProperty("models").EnumerateObject())
        {
            var s = prop.Value.GetProperty("standard");
            _standardRates[prop.Name] = new RateBlock(
                Input: s.GetProperty("input").GetDecimal(),
                Output: s.GetProperty("output").GetDecimal(),
                CacheWrite5m: s.GetProperty("cache_write_5m").GetDecimal(),
                CacheWrite1h: s.GetProperty("cache_write_1h").GetDecimal(),
                CacheHit: s.GetProperty("cache_hit").GetDecimal()
            );
        }
        _logger.LogInformation("Loaded pricing for {Count} models from {Path}", _standardRates.Count, path);
    }

    public decimal CalculateCostUsd(string? model, long inputUncached, long cacheRead,
                                    long cacheWrite5m, long cacheWrite1h, long output)
    {
        if (!TryGetRates(model, out var r)) return 0m;
        return (
            inputUncached * r.Input +
            cacheRead * r.CacheHit +
            cacheWrite5m * r.CacheWrite5m +
            cacheWrite1h * r.CacheWrite1h +
            output * r.Output
        ) / 1_000_000m;
    }

    public bool TryGetRates(string? model, out RateBlock rates)
    {
        rates = default;
        if (model is null) return false;
        var key = StripVersion(model);
        if (_standardRates.TryGetValue(key, out var r))
        {
            rates = r;
            return true;
        }
        // Log unknown models once so a typo or new model surfaces without spamming.
        lock (_missingLock)
        {
            if (_missingModels.Add(key))
                _logger.LogWarning("No pricing entry for model '{Model}'", key);
        }
        return false;
    }

    private static string StripVersion(string model)
    {
        var idx = model.IndexOf('@');
        return idx > 0 ? model[..idx] : model;
    }

    public readonly record struct RateBlock(
        decimal Input,
        decimal Output,
        decimal CacheWrite5m,
        decimal CacheWrite1h,
        decimal CacheHit
    );
}
