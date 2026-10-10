using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;

namespace ClaudeCodeProxy;

// Promotions are date-bounded rate overrides; the first window containing the requested date wins.
// A model's long_context rates apply to every token of a request whose prompt exceeds that model's own
// long_context_threshold_input_tokens. Models without a per-model threshold are priced at standard rates only.
public sealed class PricingCatalog
{
    // volatile so PricingUpdater can atomically swap the catalog hourly without a lock on the read path.
    private volatile FrozenDictionary<string, ModelPricing> _models;
    private readonly ILogger<PricingCatalog> _logger;
    private readonly HashSet<string> _missingModels = new();
    private readonly Lock _missingLock = new();

    // Edits to pricing.json apply within this window without a restart; the check is one file stat.
    private static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromMinutes(5);
    private readonly string _path;
    private readonly long _refreshIntervalMs;
    private long _nextCheckTickMs;
    private int _forceReload;
    private DateTime _loadedWriteTimeUtc;

    public DateTimeOffset? LastLoadedAt { get; private set; }

    public PricingCatalog(ILogger<PricingCatalog> logger)
        : this(logger, Path.Combine(AppContext.BaseDirectory, "pricing.json"), DefaultRefreshInterval)
    {
    }

    internal PricingCatalog(ILogger<PricingCatalog> logger, string path, TimeSpan refreshInterval)
    {
        _logger = logger;
        _path = path;
        _refreshIntervalMs = (long)refreshInterval.TotalMilliseconds;
        _nextCheckTickMs = Environment.TickCount64 + _refreshIntervalMs;
        if (!File.Exists(path))
        {
            _logger.LogWarning("pricing.json not found at {Path}; cost calculation will return $0", path);
            _models = FrozenDictionary<string, ModelPricing>.Empty;
            return;
        }

        _loadedWriteTimeUtc = File.GetLastWriteTimeUtc(path);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        _models = ParseModels(doc.RootElement.GetProperty("models"));
        LastLoadedAt = DateTimeOffset.UtcNow;
        _logger.LogInformation("Loaded pricing for {Count} models from {Path}", _models.Count, path);
    }

    // Marks the cached table stale: the next lookup re-reads the file regardless of the refresh interval or write time.
    public void Invalidate()
    {
        Volatile.Write(ref _forceReload, 1);
        Volatile.Write(ref _nextCheckTickMs, 0);
    }

    internal static bool IsValid(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return ParseModels(doc.RootElement.GetProperty("models")).Count > 0;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException
                                       or FormatException)
        {
            return false;
        }
    }

    private void RefreshIfDue()
    {
        var now = Environment.TickCount64;
        var next = Volatile.Read(ref _nextCheckTickMs);
        if (now < next) return;
        // Only the thread that wins the swap performs the check for this interval.
        if (Interlocked.CompareExchange(ref _nextCheckTickMs, now + _refreshIntervalMs, next) != next) return;

        try
        {
            var forced = Interlocked.Exchange(ref _forceReload, 0) == 1;
            if (!File.Exists(_path)) return;
            var writeTime = File.GetLastWriteTimeUtc(_path);
            if (writeTime == _loadedWriteTimeUtc && !forced) return;
            // Recorded before parsing so a malformed file is retried only once it changes again.
            _loadedWriteTimeUtc = writeTime;
            Reload(File.ReadAllText(_path));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Pricing refresh check failed; keeping current rates");
        }
    }

    public bool Reload(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var newModels = ParseModels(doc.RootElement.GetProperty("models"));
            _models = newModels;
            LastLoadedAt = DateTimeOffset.UtcNow;
            _logger.LogInformation("Pricing reloaded: {Count} models", newModels.Count);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse pricing JSON for reload");
            return false;
        }
    }

    public IReadOnlyList<PricingRow> GetAllCurrentRates(DateOnly? onDate = null)
    {
        RefreshIfDue();
        var snap = _models; // single volatile read → consistent snapshot
        var date = onDate ?? DateOnly.FromDateTime(DateTime.Now);
        return snap
            .Select(kvp =>
            {
                var rates = kvp.Value.Standard;
                foreach (var promo in kvp.Value.Promotions)
                {
                    if (date >= promo.Starts && date <= promo.Ends) { rates = promo.Rates; break; }
                }
                var longContext = kvp.Value.LongContext is { } tier
                    ? new LongContextSchedule(tier.ThresholdInputTokens, tier.Rates)
                    : null;
                return new PricingRow(kvp.Key, kvp.Value.DisplayName, rates, longContext);
            })
            .OrderByDescending(r => r.Rates.Input)
            .ThenBy(r => r.ModelId)
            .ToList();
    }

    public IReadOnlyList<ModelSchedule> GetRateSchedule()
    {
        RefreshIfDue();
        return _models
            .OrderBy(kvp => kvp.Key)
            .Select(kvp =>
            {
                var p = kvp.Value;
                var windows = p.Promotions
                    .OrderBy(promo => promo.Starts)
                    .Select(promo => new RateWindow(promo.Starts, promo.Ends, promo.Rates, promo.Description))
                    .ToList();
                var longContext = p.LongContext is { } tier
                    ? new LongContextSchedule(tier.ThresholdInputTokens, tier.Rates)
                    : null;
                return new ModelSchedule(kvp.Key, p.DisplayName, p.StandardEffectiveFrom, p.Standard, windows, longContext);
            })
            .ToList();
    }

    private static FrozenDictionary<string, ModelPricing> ParseModels(JsonElement modelsElement)
    {
        var models = new Dictionary<string, ModelPricing>();
        foreach (var prop in modelsElement.EnumerateObject())
        {
            var displayName = prop.Value.TryGetProperty("display_name", out var dn)
                ? dn.GetString() ?? prop.Name : prop.Name;
            var standard = ReadRates(prop.Value.GetProperty("standard"));
            DateOnly? standardEffectiveFrom = prop.Value.TryGetProperty("standard_effective_from", out var sef)
                ? DateOnly.ParseExact(sef.GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture)
                : null;
            LongContextTier? longContext = null;
            if (prop.Value.TryGetProperty("long_context", out var longContextRates)
                && prop.Value.TryGetProperty("long_context_threshold_input_tokens", out var threshold))
            {
                longContext = new LongContextTier(threshold.GetInt64(), ReadRates(longContextRates));
            }
            var promotions = Array.Empty<Promotion>();
            if (prop.Value.TryGetProperty("promotions", out var promoArray))
            {
                promotions = promoArray.EnumerateArray().Select(p => new Promotion(
                    Starts: DateOnly.ParseExact(p.GetProperty("starts").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                    Ends: DateOnly.ParseExact(p.GetProperty("ends").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                    Rates: ReadRates(p.GetProperty("rates")),
                    Description: p.TryGetProperty("description", out var description) ? description.GetString() ?? "" : ""
                )).ToArray();
            }
            models[prop.Name] = new ModelPricing(displayName, standard, standardEffectiveFrom, promotions, longContext);
        }
        return models.ToFrozenDictionary();
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
        var promptInputTokens = inputUncached + cacheRead + cacheWrite5m + cacheWrite1h;
        if (!TryGetRates(model, out var r, onDate, promptInputTokens)) return 0m;
        return (
            inputUncached * r.Input +
            cacheRead * r.CacheHit +
            cacheWrite5m * r.CacheWrite5m +
            cacheWrite1h * r.CacheWrite1h +
            output * r.Output
        ) / 1_000_000m;
    }

    public bool TryGetRates(string? model, out RateBlock rates, DateOnly? onDate = null, long promptInputTokens = 0)
    {
        rates = default;
        if (model is null) return false;
        RefreshIfDue();
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
            // The page's tier boundaries read "up to 100,000" and "over 100,000", so the tier starts above the threshold.
            if (pricing.LongContext is { } tier && promptInputTokens > tier.ThresholdInputTokens)
                rates = tier.Rates;
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

    public sealed record PricingRow(string ModelId, string DisplayName, RateBlock Rates, LongContextSchedule? LongContext);

    private sealed record ModelPricing(string DisplayName, RateBlock Standard, DateOnly? StandardEffectiveFrom,
        Promotion[] Promotions, LongContextTier? LongContext);

    private readonly record struct LongContextTier(long ThresholdInputTokens, RateBlock Rates);

    private readonly record struct Promotion(DateOnly Starts, DateOnly Ends, RateBlock Rates, string Description);

    public sealed record RateWindow(DateOnly Starts, DateOnly Ends, RateBlock Rates, string Description);

    public sealed record LongContextSchedule(long ThresholdInputTokens, RateBlock Rates);

    public sealed record ModelSchedule(string ModelId, string DisplayName, DateOnly? StandardEffectiveFrom,
        RateBlock Standard, IReadOnlyList<RateWindow> Windows, LongContextSchedule? LongContext);

    public readonly record struct RateBlock(
        decimal Input,
        decimal Output,
        decimal CacheWrite5m,
        decimal CacheWrite1h,
        decimal CacheHit
    );
}
