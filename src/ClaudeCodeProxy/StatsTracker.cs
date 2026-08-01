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
    private readonly Dictionary<PromptFailureMode, long> _rewriteFailures = InitFailures();
    private readonly Dictionary<PromptFailureMode, long> _acknowledgedFailures = InitFailures();
    private readonly HashSet<string> _unknownPaths = new();
    private const int MaxUnknownPaths = 20;
    private readonly Dictionary<string, ModelBucket> _byModel = new();
    private readonly Dictionary<string, EndpointBucket> _byEndpoint = new();
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
                var mb = GetOrAddModel(stats.Model, stats.Provider);
                mb.Requests++;
                mb.InputUncached += stats.InputUncached;
                mb.CacheRead += stats.CacheRead;
                mb.CacheCreation5m += stats.CacheCreation5m;
                mb.CacheCreation1h += stats.CacheCreation1h;
                mb.Output += stats.OutputTokens;
                mb.TotalDurationMs += stats.DurationMs;
            }

            if (stats.UpstreamBaseUrl is not null)
            {
                if (!_byEndpoint.TryGetValue(stats.UpstreamBaseUrl, out var eb))
                    _byEndpoint[stats.UpstreamBaseUrl] = eb = new EndpointBucket { Provider = stats.Provider };
                eb.Requests++;
            }

            var bucket = _hourly.GetOrAdd(TruncateToHour(DateTimeOffset.Now), _ => new HourlyBucket());
            bucket.Requests++;
            bucket.InputUncached += stats.InputUncached;
            bucket.CacheRead += stats.CacheRead;
            bucket.CacheCreation5m += stats.CacheCreation5m;
            bucket.CacheCreation1h += stats.CacheCreation1h;
            bucket.Output += stats.OutputTokens;
            bucket.TotalDurationMs += stats.DurationMs;

            if (stats.Provider is not null)
            {
                if (!bucket.ByProvider.TryGetValue(stats.Provider, out var pb))
                    bucket.ByProvider[stats.Provider] = pb = new ProviderHourlyBucket();
                pb.Requests++;
                pb.InputUncached   += stats.InputUncached;
                pb.CacheRead       += stats.CacheRead;
                pb.CacheCreation5m += stats.CacheCreation5m;
                pb.CacheCreation1h += stats.CacheCreation1h;
                pb.Output          += stats.OutputTokens;
                pb.TotalDurationMs += stats.DurationMs;
            }

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
                var mb = GetOrAddModel(stats.Model, stats.Provider);
                mb.Failed++;
                mb.TotalDurationMs += stats.DurationMs;
            }

            if (stats.UpstreamBaseUrl is not null)
            {
                if (!_byEndpoint.TryGetValue(stats.UpstreamBaseUrl, out var eb))
                    _byEndpoint[stats.UpstreamBaseUrl] = eb = new EndpointBucket { Provider = stats.Provider };
                eb.Failed++;
            }

            var bucket = _hourly.GetOrAdd(TruncateToHour(DateTimeOffset.Now), _ => new HourlyBucket());
            bucket.FailedCount++;
            bucket.TotalDurationMs += stats.DurationMs;

            if (stats.Provider is not null)
            {
                if (!bucket.ByProvider.TryGetValue(stats.Provider, out var pb))
                    bucket.ByProvider[stats.Provider] = pb = new ProviderHourlyBucket();
                pb.FailedCount++;
                pb.TotalDurationMs += stats.DurationMs;
            }

            PersistToDisk();
        }
    }

    // Returns null when no stats file exists for the date.
    public StatsSnapshot? GetSnapshotForDate(DateOnly date)
    {
        lock (_lock)
        {
            if (date == _currentDate) return GetSnapshot();
        }

        var path = FilePath(date);
        if (!File.Exists(path)) return null;

        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path));
            if (node is not JsonObject root) return null;

            MigrateCacheCreationKey(root["totals"] as JsonObject);
            if (root["by_model"] is JsonObject byModel)
                foreach (var prop in byModel)
                    MigrateCacheCreationKey(prop.Value as JsonObject);
            if (root["hourly"] is JsonArray hourly)
                foreach (var item in hourly)
                    MigrateCacheCreationKey(item as JsonObject);

            var file = root.Deserialize<FileSnapshot>(WriteOpts);
            if (file is null) return null;

            var totalCacheCreation = file.Totals.CacheCreation5m + file.Totals.CacheCreation1h;
            var totalIn = file.Totals.InputUncached + file.Totals.CacheRead + totalCacheCreation;
            var hitPct = totalIn > 0 ? 100.0 * file.Totals.CacheRead / totalIn : 0;
            var totalRequests = file.RequestCount + file.FailedCount;
            var avgDuration = totalRequests > 0 ? (double)file.TotalDurationMs / totalRequests : 0;

            return new StatsSnapshot(
                Date: file.Date,
                StartedAt: file.StartedAt,
                UptimeSeconds: -1, // sentinel: historical, uptime not meaningful
                RequestCount: file.RequestCount,
                FailedCount: file.FailedCount,
                Totals: file.Totals,
                HitRatePct: Math.Round(hitPct, 1),
                AvgDurationMs: Math.Round(avgDuration, 0),
                RewriteFailures: (file.RewriteFailures ?? [])
                    .Where(kvp => Enum.TryParse<PromptFailureMode>(kvp.Key, out _))
                    .ToDictionary(kvp => Enum.Parse<PromptFailureMode>(kvp.Key), kvp => kvp.Value),
                AcknowledgedFailures: (file.AcknowledgedFailures ?? [])
                    .Where(kvp => Enum.TryParse<PromptFailureMode>(kvp.Key, out _))
                    .ToDictionary(kvp => Enum.Parse<PromptFailureMode>(kvp.Key), kvp => kvp.Value),
                UnknownPaths: file.UnknownPaths ?? [],
                ByModel: file.ByModel.ToDictionary(
                    kvp => kvp.Key,
                    kvp =>
                    {
                        var mb = kvp.Value;
                        var mCacheCreate = mb.CacheCreation5m + mb.CacheCreation1h;
                        var mTotal = mb.InputUncached + mb.CacheRead + mCacheCreate;
                        var mHit = mTotal > 0 ? 100.0 * mb.CacheRead / mTotal : 0;
                        var mReqs = mb.Requests + mb.Failed;
                        var mAvg = mReqs > 0 ? (double)mb.TotalDurationMs / mReqs : 0;
                        return new ModelSnapshot(mb.Model, mb.Requests, mb.Failed, mb.InputUncached, mb.CacheRead, mb.CacheCreation5m, mb.CacheCreation1h, mb.Output, Math.Round(mHit, 1), Math.Round(mAvg, 0), mb.Provider);
                    }),
                Hourly: file.Hourly
                    .OrderByDescending(h => h.Hour)
                    .Select(h =>
                    {
                        var bCacheCreate = h.CacheCreation5m + h.CacheCreation1h;
                        var totalHourIn = h.InputUncached + h.CacheRead + bCacheCreate;
                        var hourHit = totalHourIn > 0 ? 100.0 * h.CacheRead / totalHourIn : 0;
                        var hourReqs = h.Requests + h.FailedCount;
                        var hourAvg = hourReqs > 0 ? (double)h.TotalDurationMs / hourReqs : 0;
                        return new HourlyStats(h.Hour, h.Requests, h.FailedCount, Math.Round(hourHit, 1),
                            Math.Round(hourAvg, 0), h.CacheRead, h.CacheCreation5m, h.CacheCreation1h,
                            h.InputUncached, h.Output,
                            ByProvider: h.ByProvider is { Count: > 0 }
                                ? h.ByProvider.ToDictionary(
                                      x => x.Key,
                                      x =>
                                      {
                                          var pCc = x.Value.CacheCreation5m + x.Value.CacheCreation1h;
                                          var pTotal = x.Value.InputUncached + x.Value.CacheRead + pCc;
                                          var pHit = pTotal > 0 ? 100.0 * x.Value.CacheRead / pTotal : 0;
                                          var pReqs = x.Value.Requests + x.Value.FailedCount;
                                          var pAvg = pReqs > 0 ? (double)x.Value.TotalDurationMs / pReqs : 0;
                                          return new ProviderHourlyStats(x.Value.Requests, x.Value.FailedCount,
                                              x.Value.InputUncached, x.Value.CacheRead, x.Value.CacheCreation5m,
                                              x.Value.CacheCreation1h, x.Value.Output,
                                              Math.Round(pHit, 1), Math.Round(pAvg, 0));
                                      })
                                : null);
                    })
                    .ToList(),
                ByProvider: file.ByModel
                    .Where(kvp => kvp.Value.Provider is not null)
                    .GroupBy(kvp => kvp.Value.Provider!)
                    .ToDictionary(
                        g => g.Key,
                        g => new ProviderTotals(
                            Requests:       g.Sum(x => x.Value.Requests),
                            Failed:         g.Sum(x => x.Value.Failed),
                            InputUncached:  g.Sum(x => x.Value.InputUncached),
                            CacheRead:      g.Sum(x => x.Value.CacheRead),
                            CacheCreation5m: g.Sum(x => x.Value.CacheCreation5m),
                            CacheCreation1h: g.Sum(x => x.Value.CacheCreation1h),
                            Output:         g.Sum(x => x.Value.Output)
                        )),
                ByEndpoint: (file.ByEndpoint ?? []).ToDictionary(
                    kvp => kvp.Key,
                    kvp => new EndpointSnapshot(kvp.Value.Requests, kvp.Value.Failed, kvp.Value.Provider))
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load historical stats for {Date}", date);
            return null;
        }
    }

    public IReadOnlyList<DateOnly> GetAvailableDates()
    {
        var dates = new List<DateOnly>();
        foreach (var file in Directory.EnumerateFiles(_outputPath, "stats-????????.json"))
        {
            var name = Path.GetFileNameWithoutExtension(file); // "stats-20260504"
            if (name.Length == 14 && DateOnly.TryParseExact(
                    name[6..], "yyyyMMdd", null,
                    System.Globalization.DateTimeStyles.None, out var date))
                dates.Add(date);
        }
        dates.Sort();
        return dates;
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
                RewriteFailures: new Dictionary<PromptFailureMode, long>(_rewriteFailures),
                AcknowledgedFailures: new Dictionary<PromptFailureMode, long>(_acknowledgedFailures),
                UnknownPaths: [.. _unknownPaths],
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
                        return new ModelSnapshot(mb.Model, mb.Requests, mb.Failed, mb.InputUncached, mb.CacheRead, mb.CacheCreation5m, mb.CacheCreation1h, mb.Output, Math.Round(mHit, 1), Math.Round(mAvg, 0), mb.Provider);
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
                            Output: b.Output,
                            ByProvider: b.ByProvider.Count > 0
                                ? b.ByProvider.ToDictionary(
                                      x => x.Key,
                                      x =>
                                      {
                                          var pCc = x.Value.CacheCreation5m + x.Value.CacheCreation1h;
                                          var pTotal = x.Value.InputUncached + x.Value.CacheRead + pCc;
                                          var pHit = pTotal > 0 ? 100.0 * x.Value.CacheRead / pTotal : 0;
                                          var pReqs = x.Value.Requests + x.Value.FailedCount;
                                          var pAvg = pReqs > 0 ? (double)x.Value.TotalDurationMs / pReqs : 0;
                                          return new ProviderHourlyStats(x.Value.Requests, x.Value.FailedCount,
                                              x.Value.InputUncached, x.Value.CacheRead, x.Value.CacheCreation5m,
                                              x.Value.CacheCreation1h, x.Value.Output,
                                              Math.Round(pHit, 1), Math.Round(pAvg, 0));
                                      })
                                : null
                        );
                    })
                    .ToList(),
                ByProvider: _byModel
                    .Where(kvp => kvp.Value.Provider is not null)
                    .GroupBy(kvp => kvp.Value.Provider!)
                    .ToDictionary(
                        g => g.Key,
                        g => new ProviderTotals(
                            Requests:       g.Sum(x => x.Value.Requests),
                            Failed:         g.Sum(x => x.Value.Failed),
                            InputUncached:  g.Sum(x => x.Value.InputUncached),
                            CacheRead:      g.Sum(x => x.Value.CacheRead),
                            CacheCreation5m: g.Sum(x => x.Value.CacheCreation5m),
                            CacheCreation1h: g.Sum(x => x.Value.CacheCreation1h),
                            Output:         g.Sum(x => x.Value.Output)
                        )),
                ByEndpoint: _byEndpoint.ToDictionary(
                    kvp => kvp.Key,
                    kvp => new EndpointSnapshot(kvp.Value.Requests, kvp.Value.Failed, kvp.Value.Provider))
            );
        }
    }

    public void RecordRewriteOutcome(RewriteOutcome outcome)
    {
        // NotApplicable outcomes (no FailureMode, not mutated, no identity flag) are not tracked.
        if (!outcome.Mutated && outcome.FailureMode is null && !outcome.IdentityNotRecognized) return;

        lock (_lock)
        {
            MaybeRollover();
            if (outcome.FailureMode is { } mode) _rewriteFailures[mode]++;
            if (outcome.IdentityNotRecognized) _rewriteFailures[PromptFailureMode.IdentityNotRecognized]++;
            PersistToDisk();
        }
    }

    public void RecordUnknownPath(string path)
    {
        lock (_lock)
        {
            MaybeRollover();
            _rewriteFailures[PromptFailureMode.UnknownPath]++;
            if (_unknownPaths.Count < MaxUnknownPaths) _unknownPaths.Add(path);
            PersistToDisk();
        }
    }

    public void AcknowledgeFailureMode(PromptFailureMode mode)
    {
        lock (_lock)
        {
            _acknowledgedFailures[mode] = _rewriteFailures[mode];
            PersistToDisk();
        }
    }

    private static Dictionary<PromptFailureMode, long> InitFailures() =>
        Enum.GetValues<PromptFailureMode>().ToDictionary(m => m, _ => 0L);

    private ModelBucket GetOrAddModel(string model, string? provider)
    {
        var key = $"{provider}|{model}";
        if (!_byModel.TryGetValue(key, out var mb))
            _byModel[key] = mb = new ModelBucket { Model = model, Provider = provider };
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
        foreach (var mode in Enum.GetValues<PromptFailureMode>()) _rewriteFailures[mode] = 0;
        foreach (var mode in Enum.GetValues<PromptFailureMode>()) _acknowledgedFailures[mode] = 0;
        _unknownPaths.Clear();
        _byModel.Clear();
        _byEndpoint.Clear();
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

            // Legacy files pre-2026-05-04 used a single cache_creation total; attribute it to 5m (default TTL).
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
            foreach (var (key, count) in snapshot.RewriteFailures ?? [])
                if (Enum.TryParse<PromptFailureMode>(key, out var mode))
                    _rewriteFailures[mode] = count;

            foreach (var (key, count) in snapshot.AcknowledgedFailures ?? [])
                if (Enum.TryParse<PromptFailureMode>(key, out var mode))
                    _acknowledgedFailures[mode] = count;

            foreach (var p in snapshot.UnknownPaths ?? [])
                if (_unknownPaths.Count < MaxUnknownPaths) _unknownPaths.Add(p);

            if (snapshot.StartedAt != default)
                _startedAt = snapshot.StartedAt;

            // Pre-fix files use bare-model keys; they load as-is and render unattributed. New traffic
            // re-keys by "{provider}|{model}". The two coexist for the current day; rollover clears it.
            foreach (var (model, mb) in snapshot.ByModel)
                _byModel[model] = mb;

            foreach (var (url, eb) in snapshot.ByEndpoint ?? [])
                _byEndpoint[url] = eb;

            foreach (var h in snapshot.Hourly)
            {
                var hb = new HourlyBucket
                {
                    Requests        = h.Requests,
                    FailedCount     = h.FailedCount,
                    InputUncached   = h.InputUncached,
                    CacheRead       = h.CacheRead,
                    CacheCreation5m = h.CacheCreation5m,
                    CacheCreation1h = h.CacheCreation1h,
                    Output          = h.Output,
                    TotalDurationMs = h.TotalDurationMs,
                };
                foreach (var (prov, pfe) in h.ByProvider ?? [])
                    hb.ByProvider[prov] = new ProviderHourlyBucket
                    {
                        Requests        = pfe.Requests,
                        FailedCount     = pfe.FailedCount,
                        InputUncached   = pfe.InputUncached,
                        CacheRead       = pfe.CacheRead,
                        CacheCreation5m = pfe.CacheCreation5m,
                        CacheCreation1h = pfe.CacheCreation1h,
                        Output          = pfe.Output,
                        TotalDurationMs = pfe.TotalDurationMs,
                    };
                _hourly[h.Hour] = hb;
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
        // SnakeCaseLower doesn't insert _ between letters and digits: CacheCreation5m → cache_creation5m.
        if (obj["cache_creation5m"] is not null) return;

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
        RewriteFailures: _rewriteFailures.ToDictionary(kvp => kvp.Key.ToString(), kvp => kvp.Value),
        AcknowledgedFailures: _acknowledgedFailures.ToDictionary(kvp => kvp.Key.ToString(), kvp => kvp.Value),
        UnknownPaths: [.. _unknownPaths],
        ByModel: new Dictionary<string, ModelBucket>(_byModel),
        Hourly: _hourly
            .Select(kvp =>
            {
                var b = kvp.Value;
                return new HourlyFileEntry(
                    kvp.Key, b.Requests, b.FailedCount, b.InputUncached, b.CacheRead,
                    b.CacheCreation5m, b.CacheCreation1h, b.Output, b.TotalDurationMs,
                    ByProvider: b.ByProvider.Count > 0
                        ? b.ByProvider.ToDictionary(
                              x => x.Key,
                              x => new ProviderHourlyFileEntry(x.Value.Requests, x.Value.FailedCount, x.Value.InputUncached,
                                  x.Value.CacheRead, x.Value.CacheCreation5m, x.Value.CacheCreation1h, x.Value.Output,
                                  x.Value.TotalDurationMs))
                        : null);
            })
            .OrderByDescending(h => h.Hour)
            .ToList(),
        ByEndpoint: new Dictionary<string, EndpointBucket>(_byEndpoint)
    );

    private string FilePath(DateOnly date) =>
        Path.Combine(_outputPath, $"stats-{date:yyyyMMdd}.json");

    private static DateTimeOffset TruncateToHour(DateTimeOffset dt) =>
        new(dt.Year, dt.Month, dt.Day, dt.Hour, 0, 0, dt.Offset);

    private sealed record FileSnapshot(
        string Date,
        DateTimeOffset StartedAt,
        long RequestCount,
        long FailedCount,
        TokenTotals Totals,
        long TotalDurationMs,
        Dictionary<string, long> RewriteFailures,
        Dictionary<string, long> AcknowledgedFailures,
        List<string> UnknownPaths,
        Dictionary<string, ModelBucket> ByModel,
        List<HourlyFileEntry> Hourly,
        Dictionary<string, EndpointBucket>? ByEndpoint
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
        long TotalDurationMs,
        Dictionary<string, ProviderHourlyFileEntry>? ByProvider = null
    );

    private sealed record ProviderHourlyFileEntry(
        long Requests,
        long FailedCount,
        long InputUncached,
        long CacheRead,
        long CacheCreation5m,
        long CacheCreation1h,
        long Output,
        long TotalDurationMs
    );

    private sealed class EndpointBucket
    {
        public long Requests { get; set; }
        public long Failed { get; set; }
        public string? Provider { get; set; }
    }

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
        public string? Model { get; set; }
        public string? Provider { get; set; }
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
        public Dictionary<string, ProviderHourlyBucket> ByProvider { get; } = new();
    }

    private sealed class ProviderHourlyBucket
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

public sealed record StatsSnapshot(
    string Date,
    DateTimeOffset StartedAt,
    double UptimeSeconds,
    long RequestCount,
    long FailedCount,
    TokenTotals Totals,
    double HitRatePct,
    double AvgDurationMs,
    IReadOnlyDictionary<PromptFailureMode, long> RewriteFailures,
    IReadOnlyDictionary<PromptFailureMode, long> AcknowledgedFailures,
    IReadOnlyList<string> UnknownPaths,
    IReadOnlyDictionary<string, ModelSnapshot> ByModel,
    IReadOnlyList<HourlyStats> Hourly,
    IReadOnlyDictionary<string, ProviderTotals> ByProvider,
    IReadOnlyDictionary<string, EndpointSnapshot> ByEndpoint
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
    string? Model,
    long Requests,
    long Failed,
    long InputUncached,
    long CacheRead,
    long CacheCreation5m,
    long CacheCreation1h,
    long Output,
    double HitRatePct,
    double AvgDurationMs,
    string? Provider
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
    long Output,
    IReadOnlyDictionary<string, ProviderHourlyStats>? ByProvider = null
)
{
    public long CacheCreation => CacheCreation5m + CacheCreation1h;
}

public sealed record ProviderHourlyStats(
    long Requests,
    long FailedCount,
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

public sealed record ProviderTotals(
    long Requests,
    long Failed,
    long InputUncached,
    long CacheRead,
    long CacheCreation5m,
    long CacheCreation1h,
    long Output
)
{
    public long CacheCreation => CacheCreation5m + CacheCreation1h;
}

public sealed record EndpointSnapshot(long Requests, long Failed, string? Provider);
