using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;

namespace ClaudeCodeProxy;

// Standard tier only — the 200K long-context threshold can't be detected without per-request input tracking.
// Promotions are date-bounded rate overrides; the first window containing the requested date wins.
public sealed class PricingCatalog
{
    private readonly FrozenDictionary<string, ModelPricing> _models;
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
            _models = FrozenDictionary<string, ModelPricing>.Empty;
            return;
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var models = new Dictionary<string, ModelPricing>();
        foreach (var prop in doc.RootElement.GetProperty("models").EnumerateObject())
        {
            var standard = ReadRates(prop.Value.GetProperty("standard"));
            var promotions = Array.Empty<Promotion>();
            if (prop.Value.TryGetProperty("promotions", out var promoArray))
            {
                promotions = promoArray.EnumerateArray().Select(p => new Promotion(
                    Starts: DateOnly.ParseExact(p.GetProperty("starts").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                    Ends: DateOnly.ParseExact(p.GetProperty("ends").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                    Rates: ReadRates(p.GetProperty("rates"))
                )).ToArray();
            }
            models[prop.Name] = new ModelPricing(standard, promotions);
        }
        _models = models.ToFrozenDictionary();
        _logger.LogInformation("Loaded pricing for {Count} models from {Path}", _models.Count, path);
    }

    private static RateBlock ReadRates(JsonElement e) => new(
        Input: e.GetProperty("input").GetDecimal(),
        Output: e.GetProperty("output").GetDecimal(),
        CacheWrite5m: e.GetProperty("cache_write_5m").GetDecimal(),
        CacheWrite1h: e.GetProperty("cache_write_1h").GetDecimal(),
        CacheHit: e.GetProperty("cache_hit").GetDecimal()
    );

    public decimal CalculateCostUsd(string? model, long inputUncached, long cacheRead,
                                    long cacheWrite5m, long cacheWrite1h, long output,
                                    DateOnly? onDate = null)
    {
        if (!TryGetRates(model, out var r, onDate)) return 0m;
        return (
            inputUncached * r.Input +
            cacheRead * r.CacheHit +
            cacheWrite5m * r.CacheWrite5m +
            cacheWrite1h * r.CacheWrite1h +
            output * r.Output
        ) / 1_000_000m;
    }

    public bool TryGetRates(string? model, out RateBlock rates, DateOnly? onDate = null)
    {
        rates = default;
        if (model is null) return false;
        var key = StripVersion(model);
        if (_models.TryGetValue(key, out var pricing))
        {
            // Snapshot dates roll at local midnight, so date-based rate selection uses local time too.
            var date = onDate ?? DateOnly.FromDateTime(DateTime.Now);
            rates = pricing.Standard;
            foreach (var promo in pricing.Promotions)
            {
                if (date >= promo.Starts && date <= promo.Ends)
                {
                    rates = promo.Rates;
                    break;
                }
            }
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

    private sealed record ModelPricing(RateBlock Standard, Promotion[] Promotions);

    private readonly record struct Promotion(DateOnly Starts, DateOnly Ends, RateBlock Rates);

    public readonly record struct RateBlock(
        decimal Input,
        decimal Output,
        decimal CacheWrite5m,
        decimal CacheWrite1h,
        decimal CacheHit
    );
}
