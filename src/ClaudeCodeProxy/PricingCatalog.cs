using System.Collections.Frozen;
using System.Text.Json;

namespace ClaudeCodeProxy;

// Standard tier only — the 200K long-context threshold can't be detected without per-request input tracking.
public sealed class PricingCatalog
{
    private readonly FrozenDictionary<string, RateBlock> _standardRates;
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
            _standardRates = FrozenDictionary<string, RateBlock>.Empty;
            return;
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var rates = new Dictionary<string, RateBlock>();
        foreach (var prop in doc.RootElement.GetProperty("models").EnumerateObject())
        {
            var s = prop.Value.GetProperty("standard");
            rates[prop.Name] = new RateBlock(
                Input: s.GetProperty("input").GetDecimal(),
                Output: s.GetProperty("output").GetDecimal(),
                CacheWrite5m: s.GetProperty("cache_write_5m").GetDecimal(),
                CacheWrite1h: s.GetProperty("cache_write_1h").GetDecimal(),
                CacheHit: s.GetProperty("cache_hit").GetDecimal()
            );
        }
        _standardRates = rates.ToFrozenDictionary();
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
