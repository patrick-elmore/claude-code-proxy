using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace ClaudeCodeProxy;

public sealed class StatsTracker
{
    private readonly string _outputPath;
    private readonly ILogger<StatsTracker> _logger;
    private readonly Lock _lock = new();

    private DateOnly _currentDate;
    private DateTimeOffset _startedAt;
    private long _requestCount;
    private long _failedCount;
    private long _totalInputUncached;
    private long _totalCacheRead;
    private long _totalCacheCreation5m;
    private long _totalCacheCreation1h;
    private long _totalOutput;
    private long _totalDurationMs;
    private readonly Dictionary<string, ModelBucket> _byModel = new();
    private readonly ConcurrentDictionary<DateTimeOffset, HourlyBucket> _hourly = new();

    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public StatsTracker(IOptions<StatsOptions> opts, ILogger<StatsTracker> logger)
    {
        _outputPath = opts.Value.OutputPath;
        _logger = logger;
        _currentDate = DateOnly.FromDateTime(DateTimeOffset.Now.LocalDateTime);
        _startedAt = DateTimeOffset.Now;
        Directory.CreateDirectory(_outputPath);
        LoadFromDisk();
    }

    public void Record(RequestStats stats)
    {
        lock (_lock)
        {
            MaybeRollover();

            _requestCount++;
            _totalInputUncached += stats.InputUncached;
            _totalCacheRead += stats.CacheRead;
            _totalCacheCreation5m += stats.CacheCreation5m;
            _totalCacheCreation1h += stats.CacheCreation1h;
            _totalOutput += stats.OutputTokens;
            _totalDurationMs += stats.DurationMs;

            if (stats.Model is not null)
            {
                var mb = GetOrAddModel(stats.Model);
                mb.Requests++;
                mb.InputUncached += stats.InputUncached;
                mb.CacheRead += stats.CacheRead;
                mb.CacheCreation5m += stats.CacheCreation5m;
                mb.CacheCreation1h += stats.CacheCreation1h;
                mb.Output += stats.OutputTokens;
                mb.TotalDurationMs += stats.DurationMs;
            }

            var bucket = _hourly.GetOrAdd(TruncateToHour(DateTimeOffset.Now), _ => new HourlyBucket());
            bucket.Requests++;
            bucket.InputUncached += stats.InputUncached;
            bucket.CacheRead += stats.CacheRead;
            bucket.CacheCreation5m += stats.CacheCreation5m;
            bucket.CacheCreation1h += stats.CacheCreation1h;
            bucket.Output += stats.OutputTokens;
            bucket.TotalDurationMs += stats.DurationMs;

            PersistToDisk();
        }
    }

    public void RecordFailed(RequestStats stats)
    {
        lock (_lock)
        {
            MaybeRollover();

            _failedCount++;
            _totalDurationMs += stats.DurationMs;

            if (stats.Model is not null)
            {
                var mb = GetOrAddModel(stats.Model);
                mb.Failed++;
                mb.TotalDurationMs += stats.DurationMs;
            }

            var bucket = _hourly.GetOrAdd(TruncateToHour(DateTimeOffset.Now), _ => new HourlyBucket());
            bucket.FailedCount++;
            bucket.TotalDurationMs += stats.DurationMs;

            PersistToDisk();
        }
    }

    public StatsSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            var totalCacheCreation = _totalCacheCreation5m + _totalCacheCreation1h;
            var totalIn = _totalInputUncached + _totalCacheRead + totalCacheCreation;
            var hitPct = totalIn > 0 ? 100.0 * _totalCacheRead / totalIn : 0;
            var totalRequests = _requestCount + _failedCount;
            var avgDuration = totalRequests > 0 ? (double)_totalDurationMs / totalRequests : 0;

            return new StatsSnapshot(
                Date: _currentDate.ToString("yyyy-MM-dd"),
                StartedAt: _startedAt,
                UptimeSeconds: (DateTimeOffset.Now - _startedAt).TotalSeconds,
                RequestCount: _requestCount,
                FailedCount: _failedCount,
                Totals: new TokenTotals(_totalInputUncached, _totalCacheRead, _totalCacheCreation5m, _totalCacheCreation1h, _totalOutput),
                HitRatePct: Math.Round(hitPct, 1),
                AvgDurationMs: Math.Round(avgDuration, 0),
                ByModel: _byModel.ToDictionary(
                    kvp => kvp.Key,
                    kvp =>
                    {
                        var mb = kvp.Value;
                        var mCacheCreate = mb.CacheCreation5m + mb.CacheCreation1h;
                        var mTotal = mb.InputUncached + mb.CacheRead + mCacheCreate;
                        var mHit = mTotal > 0 ? 100.0 * mb.CacheRead / mTotal : 0;
                        var mReqs = mb.Requests + mb.Failed;
                        var mAvg = mReqs > 0 ? (double)mb.TotalDurationMs / mReqs : 0;
                        return new ModelSnapshot(mb.Requests, mb.Failed, mb.InputUncached, mb.CacheRead, mb.CacheCreation5m, mb.CacheCreation1h, mb.Output, Math.Round(mHit, 1), Math.Round(mAvg, 0));
                    }),
                Hourly: _hourly
                    .OrderByDescending(kvp => kvp.Key)
                    .Select(kvp =>
                    {
                        var b = kvp.Value;
                        var bCacheCreate = b.CacheCreation5m + b.CacheCreation1h;
                        var totalHourIn = b.InputUncached + b.CacheRead + bCacheCreate;
                        var hourHit = totalHourIn > 0 ? 100.0 * b.CacheRead / totalHourIn : 0;
                        var hourReqs = b.Requests + b.FailedCount;
                        var hourAvg = hourReqs > 0 ? (double)b.TotalDurationMs / hourReqs : 0;
                        return new HourlyStats(
                            Hour: kvp.Key,
                            Requests: b.Requests,
                            FailedCount: b.FailedCount,
                            HitRatePct: Math.Round(hourHit, 1),
                            AvgDurationMs: Math.Round(hourAvg, 0),
                            CacheRead: b.CacheRead,
                            CacheCreation5m: b.CacheCreation5m,
                            CacheCreation1h: b.CacheCreation1h,
                            InputUncached: b.InputUncached,
                            Output: b.Output
                        );
                    })
                    .ToList()
            );
        }
    }

    private ModelBucket GetOrAddModel(string model)
    {
        if (!_byModel.TryGetValue(model, out var mb))
            _byModel[model] = mb = new ModelBucket();
        return mb;
    }

    private void MaybeRollover()
    {
        var today = DateOnly.FromDateTime(DateTimeOffset.Now.LocalDateTime);
        if (today == _currentDate) return;

        _logger.LogInformation("Stats rolling over from {OldDate} to {NewDate}", _currentDate, today);
        _currentDate = today;
        _startedAt = DateTimeOffset.Now;
        _requestCount = 0;
        _failedCount = 0;
        _totalInputUncached = 0;
        _totalCacheRead = 0;
        _totalCacheCreation5m = 0;
        _totalCacheCreation1h = 0;
        _totalOutput = 0;
        _totalDurationMs = 0;
        _byModel.Clear();
        _hourly.Clear();
    }

    private void PersistToDisk()
    {
        try
        {
            File.WriteAllText(FilePath(_currentDate), JsonSerializer.Serialize(BuildFileSnapshot(), WriteOpts));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist stats to disk");
        }
    }

    private void LoadFromDisk()
    {
        var path = FilePath(_currentDate);
        if (!File.Exists(path)) return;

        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path));
            if (node is not JsonObject root) return;

            // Migrate legacy schema: pre-2026-05-04 files used a single `cache_creation`
            // total instead of the 5m / 1h split. Attribute legacy totals to 5m
            // (the default TTL) so they survive the upgrade.
            MigrateCacheCreationKey(root["totals"] as JsonObject);
            if (root["by_model"] is JsonObject byModel)
                foreach (var prop in byModel)
                    MigrateCacheCreationKey(prop.Value as JsonObject);
            if (root["hourly"] is JsonArray hourly)
                foreach (var item in hourly)
                    MigrateCacheCreationKey(item as JsonObject);

            var snapshot = root.Deserialize<FileSnapshot>(WriteOpts);
            if (snapshot is null) return;

            _requestCount = snapshot.RequestCount;
            _failedCount = snapshot.FailedCount;
            _totalInputUncached = snapshot.Totals.InputUncached;
            _totalCacheRead = snapshot.Totals.CacheRead;
            _totalCacheCreation5m = snapshot.Totals.CacheCreation5m;
            _totalCacheCreation1h = snapshot.Totals.CacheCreation1h;
            _totalOutput = snapshot.Totals.Output;
            _totalDurationMs = snapshot.TotalDurationMs;

            if (snapshot.StartedAt != default)
                _startedAt = snapshot.StartedAt;

            foreach (var (model, mb) in snapshot.ByModel)
                _byModel[model] = mb;

            foreach (var h in snapshot.Hourly)
            {
                _hourly[h.Hour] = new HourlyBucket
                {
                    Requests = h.Requests,
                    FailedCount = h.FailedCount,
                    InputUncached = h.InputUncached,
                    CacheRead = h.CacheRead,
                    CacheCreation5m = h.CacheCreation5m,
                    CacheCreation1h = h.CacheCreation1h,
                    Output = h.Output,
                    TotalDurationMs = h.TotalDurationMs,
                };
            }

            _logger.LogInformation("Loaded stats from disk: {Requests} requests for {Date}", _requestCount, _currentDate);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load stats from disk; starting fresh");
        }
    }

    private static void MigrateCacheCreationKey(JsonObject? obj)
    {
        if (obj is null) return;
        // Note: snake_case naming policy does NOT insert an underscore between
        // letters and digits, so CacheCreation5m serializes as `cache_creation5m`.
        if (obj["cache_creation5m"] is not null) return; // already new schema

        if (obj["cache_creation"] is { } legacy)
        {
            var val = (long?)legacy ?? 0;
            obj.Remove("cache_creation");
            obj["cache_creation5m"] = val;
            obj["cache_creation1h"] = 0;
        }
    }

    private FileSnapshot BuildFileSnapshot() => new(
        Date: _currentDate.ToString("yyyy-MM-dd"),
        StartedAt: _startedAt,
        RequestCount: _requestCount,
        FailedCount: _failedCount,
        Totals: new TokenTotals(_totalInputUncached, _totalCacheRead, _totalCacheCreation5m, _totalCacheCreation1h, _totalOutput),
        TotalDurationMs: _totalDurationMs,
        ByModel: new Dictionary<string, ModelBucket>(_byModel),
        Hourly: _hourly
            .Select(kvp =>
            {
                var b = kvp.Value;
                return new HourlyFileEntry(kvp.Key, b.Requests, b.FailedCount, b.InputUncached, b.CacheRead, b.CacheCreation5m, b.CacheCreation1h, b.Output, b.TotalDurationMs);
            })
            .OrderByDescending(h => h.Hour)
            .ToList()
    );

    private string FilePath(DateOnly date) =>
        Path.Combine(_outputPath, $"stats-{date:yyyyMMdd}.json");

    private static DateTimeOffset TruncateToHour(DateTimeOffset dt) =>
        new(dt.Year, dt.Month, dt.Day, dt.Hour, 0, 0, dt.Offset);

    // ── File persistence models ───────────────────────────────────────────────

    private sealed record FileSnapshot(
        string Date,
        DateTimeOffset StartedAt,
        long RequestCount,
        long FailedCount,
        TokenTotals Totals,
        long TotalDurationMs,
        Dictionary<string, ModelBucket> ByModel,
        List<HourlyFileEntry> Hourly
    );

    private sealed record HourlyFileEntry(
        DateTimeOffset Hour,
        long Requests,
        long FailedCount,
        long InputUncached,
        long CacheRead,
        long CacheCreation5m,
        long CacheCreation1h,
        long Output,
        long TotalDurationMs
    );

    private sealed class ModelBucket
    {
        public long Requests { get; set; }
        public long Failed { get; set; }
        public long InputUncached { get; set; }
        public long CacheRead { get; set; }
        public long CacheCreation5m { get; set; }
        public long CacheCreation1h { get; set; }
        public long Output { get; set; }
        public long TotalDurationMs { get; set; }
    }

    private sealed class HourlyBucket
    {
        public long Requests { get; set; }
        public long FailedCount { get; set; }
        public long InputUncached { get; set; }
        public long CacheRead { get; set; }
        public long CacheCreation5m { get; set; }
        public long CacheCreation1h { get; set; }
        public long Output { get; set; }
        public long TotalDurationMs { get; set; }
    }
}

// ── Public snapshot types ─────────────────────────────────────────────────────

public sealed record StatsSnapshot(
    string Date,
    DateTimeOffset StartedAt,
    double UptimeSeconds,
    long RequestCount,
    long FailedCount,
    TokenTotals Totals,
    double HitRatePct,
    double AvgDurationMs,
    IReadOnlyDictionary<string, ModelSnapshot> ByModel,
    IReadOnlyList<HourlyStats> Hourly
);

public sealed record TokenTotals(
    long InputUncached,
    long CacheRead,
    long CacheCreation5m,
    long CacheCreation1h,
    long Output
)
{
    public long CacheCreation => CacheCreation5m + CacheCreation1h;
}

public sealed record ModelSnapshot(
    long Requests,
    long Failed,
    long InputUncached,
    long CacheRead,
    long CacheCreation5m,
    long CacheCreation1h,
    long Output,
    double HitRatePct,
    double AvgDurationMs
)
{
    public long CacheCreation => CacheCreation5m + CacheCreation1h;
}

public sealed record HourlyStats(
    DateTimeOffset Hour,
    long Requests,
    long FailedCount,
    double HitRatePct,
    double AvgDurationMs,
    long CacheRead,
    long CacheCreation5m,
    long CacheCreation1h,
    long InputUncached,
    long Output
)
{
    public long CacheCreation => CacheCreation5m + CacheCreation1h;
}
