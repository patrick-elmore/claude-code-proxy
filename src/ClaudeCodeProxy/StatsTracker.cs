using System.Collections.Concurrent;
using System.Text.Json;
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
    private long _totalInputUncached;
    private long _totalCacheRead;
    private long _totalCacheCreation;
    private long _totalOutput;
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
            _totalCacheCreation += stats.CacheCreation;
            _totalOutput += stats.OutputTokens;

            var hourKey = TruncateToHour(DateTimeOffset.Now);
            var bucket = _hourly.GetOrAdd(hourKey, _ => new HourlyBucket());
            lock (bucket)
            {
                bucket.Requests++;
                bucket.InputUncached += stats.InputUncached;
                bucket.CacheRead += stats.CacheRead;
                bucket.CacheCreation += stats.CacheCreation;
                bucket.Output += stats.OutputTokens;
            }

            PersistToDisk();
        }
    }

    public StatsSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            var totalIn = _totalInputUncached + _totalCacheRead + _totalCacheCreation;
            var hitPct = totalIn > 0 ? 100.0 * _totalCacheRead / totalIn : 0;

            return new StatsSnapshot(
                Date: _currentDate.ToString("yyyy-MM-dd"),
                StartedAt: _startedAt,
                UptimeSeconds: (DateTimeOffset.Now - _startedAt).TotalSeconds,
                RequestCount: _requestCount,
                Totals: new TokenTotals(_totalInputUncached, _totalCacheRead, _totalCacheCreation, _totalOutput),
                HitRatePct: Math.Round(hitPct, 1),
                Hourly: _hourly
                    .OrderByDescending(kvp => kvp.Key)
                    .Select(kvp =>
                    {
                        HourlyBucket b;
                        lock (kvp.Value) { b = new HourlyBucket { Requests = kvp.Value.Requests, InputUncached = kvp.Value.InputUncached, CacheRead = kvp.Value.CacheRead, CacheCreation = kvp.Value.CacheCreation, Output = kvp.Value.Output }; }
                        var totalHourIn = b.InputUncached + b.CacheRead + b.CacheCreation;
                        var hourHit = totalHourIn > 0 ? 100.0 * b.CacheRead / totalHourIn : 0;
                        return new HourlyStats(
                            Hour: kvp.Key,
                            Requests: b.Requests,
                            HitRatePct: Math.Round(hourHit, 1),
                            CacheRead: b.CacheRead,
                            CacheCreation: b.CacheCreation,
                            InputUncached: b.InputUncached,
                            Output: b.Output
                        );
                    })
                    .ToList()
            );
        }
    }

    private void MaybeRollover()
    {
        var today = DateOnly.FromDateTime(DateTimeOffset.Now.LocalDateTime);
        if (today == _currentDate) return;

        _logger.LogInformation("Stats rolling over from {OldDate} to {NewDate}", _currentDate, today);
        _currentDate = today;
        _startedAt = DateTimeOffset.Now;
        _requestCount = 0;
        _totalInputUncached = 0;
        _totalCacheRead = 0;
        _totalCacheCreation = 0;
        _totalOutput = 0;
        _hourly.Clear();
    }

    private void PersistToDisk()
    {
        try
        {
            var snapshot = BuildFileSnapshot();
            File.WriteAllText(FilePath(_currentDate), JsonSerializer.Serialize(snapshot, WriteOpts));
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
            var snapshot = JsonSerializer.Deserialize<FileSnapshot>(File.ReadAllText(path), WriteOpts);
            if (snapshot is null) return;

            _requestCount = snapshot.RequestCount;
            _totalInputUncached = snapshot.Totals.InputUncached;
            _totalCacheRead = snapshot.Totals.CacheRead;
            _totalCacheCreation = snapshot.Totals.CacheCreation;
            _totalOutput = snapshot.Totals.Output;

            if (snapshot.StartedAt != default)
                _startedAt = snapshot.StartedAt;

            foreach (var h in snapshot.Hourly)
            {
                _hourly[h.Hour] = new HourlyBucket
                {
                    Requests = h.Requests,
                    InputUncached = h.InputUncached,
                    CacheRead = h.CacheRead,
                    CacheCreation = h.CacheCreation,
                    Output = h.Output,
                };
            }

            _logger.LogInformation("Loaded stats from disk: {Requests} requests for {Date}", _requestCount, _currentDate);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load stats from disk; starting fresh");
        }
    }

    private FileSnapshot BuildFileSnapshot() => new(
        Date: _currentDate.ToString("yyyy-MM-dd"),
        StartedAt: _startedAt,
        RequestCount: _requestCount,
        Totals: new TokenTotals(_totalInputUncached, _totalCacheRead, _totalCacheCreation, _totalOutput),
        Hourly: _hourly
            .Select(kvp =>
            {
                HourlyBucket b;
                lock (kvp.Value) { b = new HourlyBucket { Requests = kvp.Value.Requests, InputUncached = kvp.Value.InputUncached, CacheRead = kvp.Value.CacheRead, CacheCreation = kvp.Value.CacheCreation, Output = kvp.Value.Output }; }
                return new HourlyFileEntry(Hour: kvp.Key, Requests: b.Requests, InputUncached: b.InputUncached, CacheRead: b.CacheRead, CacheCreation: b.CacheCreation, Output: b.Output);
            })
            .OrderByDescending(h => h.Hour)
            .ToList()
    );

    private string FilePath(DateOnly date) =>
        Path.Combine(_outputPath, $"stats-{date:yyyyMMdd}.json");

    private static DateTimeOffset TruncateToHour(DateTimeOffset dt) =>
        new(dt.Year, dt.Month, dt.Day, dt.Hour, 0, 0, dt.Offset);

    private sealed record FileSnapshot(
        string Date,
        DateTimeOffset StartedAt,
        long RequestCount,
        TokenTotals Totals,
        List<HourlyFileEntry> Hourly
    );

    private sealed record HourlyFileEntry(
        DateTimeOffset Hour,
        long Requests,
        long InputUncached,
        long CacheRead,
        long CacheCreation,
        long Output
    );

    private sealed class HourlyBucket
    {
        public long Requests { get; set; }
        public long InputUncached { get; set; }
        public long CacheRead { get; set; }
        public long CacheCreation { get; set; }
        public long Output { get; set; }
    }
}

public sealed record StatsSnapshot(
    string Date,
    DateTimeOffset StartedAt,
    double UptimeSeconds,
    long RequestCount,
    TokenTotals Totals,
    double HitRatePct,
    IReadOnlyList<HourlyStats> Hourly
);

public sealed record TokenTotals(
    long InputUncached,
    long CacheRead,
    long CacheCreation,
    long Output
);

public sealed record HourlyStats(
    DateTimeOffset Hour,
    long Requests,
    double HitRatePct,
    long CacheRead,
    long CacheCreation,
    long InputUncached,
    long Output
);
