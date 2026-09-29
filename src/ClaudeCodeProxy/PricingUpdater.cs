using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ClaudeCodeProxy;

// Fetches the Anthropic pricing docs page hourly and hot-reloads PricingCatalog when rates change.
// The platform.claude.com/docs/en/ URL serves markdown directly — no HTML parsing needed.
// Four safety gates: valid parse, minimum model count, no zero/negative rates, no rate changed > 10×.
// Any gate failure leaves the existing pricing.json and in-memory catalog untouched.
// Parsed rates are merged into the existing file: promotions, long_context, and models absent from the page are kept.
public sealed partial class PricingUpdater : BackgroundService
{
    private const string PricingDocUrl = "https://platform.claude.com/docs/en/about-claude/pricing";
    private const int MinModelCount = 4;
    private const decimal MaxRateFactor = 10m;

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly PricingCatalog _catalog;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PricingUpdater> _logger;

    public PricingUpdater(PricingCatalog catalog, IHttpClientFactory httpClientFactory, ILogger<PricingUpdater> logger)
    {
        _catalog = catalog;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Run once on startup so a freshly deployed service doesn't wait a full hour.
        await TryUpdateAsync(ct);

        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        while (await timer.WaitForNextTickAsync(ct))
            await TryUpdateAsync(ct);
    }

    private async Task TryUpdateAsync(CancellationToken ct)
    {
        string content;
        try
        {
            var client = _httpClientFactory.CreateClient("pricing-docs");
            content = await client.GetStringAsync(PricingDocUrl, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Pricing update: failed to fetch {Url}", PricingDocUrl);
            return;
        }

        List<ParsedModel> models;
        try
        {
            models = ParsePricingPage(content);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Pricing update: failed to parse pricing page content");
            return;
        }

        // Gate 1: enough models to be a plausible parse
        if (models.Count < MinModelCount)
        {
            _logger.LogWarning("Pricing update: only {Count} models parsed (min {Min}); skipping", models.Count, MinModelCount);
            return;
        }

        // Gate 2: no zero or negative rates
        var badRate = models.Find(m => m.Input <= 0 || m.Output <= 0 || m.CacheWrite5m <= 0 || m.CacheWrite1h <= 0 || m.CacheHit <= 0);
        if (badRate is not null)
        {
            _logger.LogWarning("Pricing update: model '{Model}' has a non-positive rate; skipping", badRate.ModelId);
            return;
        }

        // Gate 3: continuity — for models in both old and new data, no single rate may change by > 10×
        foreach (var m in models)
        {
            if (!_catalog.TryGetRates(m.ModelId, out var existing)) continue;
            if (!RatesWithinFactor(existing, m, MaxRateFactor))
            {
                _logger.LogWarning("Pricing update: model '{Model}' has a rate change exceeding {Factor}×; skipping", m.ModelId, MaxRateFactor);
                return;
            }
        }

        var path = Path.Combine(AppContext.BaseDirectory, "pricing.json");
        string? newJson;
        try
        {
            var existingJson = File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : null;
            newJson = MergeIntoExisting(existingJson, models);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Pricing update: failed to merge parsed rates into {Path}; skipping", path);
            return;
        }

        if (newJson is null)
        {
            _logger.LogDebug("Pricing update: no changes");
            return;
        }

        // Write before reload so the new prices survive a restart. Temp file + move keeps readers from seeing a partial file.
        try
        {
            var tempPath = path + ".tmp";
            await File.WriteAllTextAsync(tempPath, newJson, ct);
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pricing update: failed to write {Path}; catalog not reloaded", path);
            return;
        }

        if (_catalog.Reload(newJson))
            _logger.LogInformation("Pricing update: applied changes ({Count} models parsed)", models.Count);
        else
            _logger.LogWarning("Pricing update: file written but catalog reload failed");
    }

    internal static List<ParsedModel> ParsePricingPage(string content) =>
        ParseTableLines(content.Split('\n'));

    // Returns the updated file content, or null when the parsed rates match what the file already has.
    internal static string? MergeIntoExisting(string? existingJson, List<ParsedModel> models)
    {
        JsonObject root;
        try
        {
            root = (existingJson is null ? null : JsonNode.Parse(existingJson) as JsonObject) ?? BuildDefaultRoot();
        }
        catch (JsonException)
        {
            root = BuildDefaultRoot();
        }

        if (root["models"] is not JsonObject modelsObj)
        {
            modelsObj = new JsonObject();
            root["models"] = modelsObj;
        }

        var changed = false;
        var inserted = 0;
        foreach (var m in models)
        {
            if (modelsObj[m.ModelId] is JsonObject entry)
            {
                if ((string?)entry["display_name"] == m.DisplayName
                    && entry["standard"] is JsonObject standard
                    && StandardMatches(standard, m))
                    continue;

                entry["display_name"] = m.DisplayName;
                entry["standard"] = StandardNode(m);
                changed = true;
            }
            else
            {
                // Newest models are listed first on the page and in the file.
                modelsObj.Insert(inserted++, m.ModelId, new JsonObject
                {
                    ["display_name"] = m.DisplayName,
                    ["standard"] = StandardNode(m)
                });
                changed = true;
            }
        }

        return changed ? root.ToJsonString(WriteOptions) : null;
    }

    private static bool StandardMatches(JsonObject standard, ParsedModel m) =>
        (decimal?)standard["input"] == m.Input
        && (decimal?)standard["output"] == m.Output
        && (decimal?)standard["cache_write_5m"] == m.CacheWrite5m
        && (decimal?)standard["cache_write_1h"] == m.CacheWrite1h
        && (decimal?)standard["cache_hit"] == m.CacheHit;

    private static JsonObject StandardNode(ParsedModel m) => new()
    {
        ["input"]          = JsonValue.Create(m.Input),
        ["output"]         = JsonValue.Create(m.Output),
        ["cache_write_5m"] = JsonValue.Create(m.CacheWrite5m),
        ["cache_write_1h"] = JsonValue.Create(m.CacheWrite1h),
        ["cache_hit"]      = JsonValue.Create(m.CacheHit)
    };

    private static JsonObject BuildDefaultRoot() => new()
    {
        ["currency"] = "USD",
        ["unit_per_million_tokens"] = true,
        ["long_context_threshold_input_tokens"] = 200000
    };

    private static List<ParsedModel> ParseTableLines(string[] lines)
    {
        int headerIdx = -1;
        int colInput = -1, colCw5m = -1, colCw1h = -1, colCh = -1, colOutput = -1;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (!line.StartsWith('|')) continue;

            var cells = SplitRow(line);
            if (cells.Count < 6) continue;

            if (cells.Any(c => c.Contains("Base input", StringComparison.OrdinalIgnoreCase)))
            {
                headerIdx = i;
                for (int j = 0; j < cells.Count; j++)
                {
                    var h = cells[j].ToLowerInvariant();
                    if (h.Contains("base input")) colInput = j;
                    else if (h.Contains("5m cache")) colCw5m = j;
                    else if (h.Contains("1h cache")) colCw1h = j;
                    else if (h.Contains("cache hit")) colCh = j;
                    else if (h.Contains("output")) colOutput = j;
                }
                break;
            }
        }

        if (headerIdx < 0 || new[] { colInput, colCw5m, colCw1h, colCh, colOutput }.Any(c => c < 0))
            throw new InvalidOperationException("Could not locate pricing table header or required columns");

        // Markdown tables have a separator row (|---|---|) after the header; skip it if present.
        int dataStart = headerIdx + 1;
        if (dataStart < lines.Length && IsSeparatorRow(lines[dataStart]))
            dataStart++;

        var result = new List<ParsedModel>();
        int maxCol = new[] { colInput, colCw5m, colCw1h, colCh, colOutput }.Max();

        for (int i = dataStart; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (!line.StartsWith('|')) break;

            var cells = SplitRow(line);
            if (cells.Count <= maxCol) continue;

            var displayName = CleanModelName(cells[0]);
            if (string.IsNullOrWhiteSpace(displayName)) continue;

            var input  = ExtractPrice(cells[colInput]);
            var cw5m   = ExtractPrice(cells[colCw5m]);
            var cw1h   = ExtractPrice(cells[colCw1h]);
            var ch     = ExtractPrice(cells[colCh]);
            var output = ExtractPrice(cells[colOutput]);

            if (input is null || cw5m is null || cw1h is null || ch is null || output is null) continue;

            result.Add(new ParsedModel(ToModelId(displayName), displayName,
                input.Value, output.Value, cw5m.Value, cw1h.Value, ch.Value));
        }

        return result;
    }

    private static List<string> SplitRow(string line) =>
        line.Split('|').Skip(1).SkipLast(1).Select(c => c.Trim()).ToList();

    // A separator row contains only |, -, :, and whitespace — no letters, digits, or $.
    private static bool IsSeparatorRow(string line) =>
        line.Trim().Length > 0 && !line.Any(c => char.IsLetterOrDigit(c) || c == '$');

    internal static string CleanModelName(string cell)
    {
        var s = MarkdownLinkRegex().Replace(cell, "$1"); // [text](url) → text
        s = ParenRegex().Replace(s, "");                 // strip (parentheticals)
        return s.Trim();
    }

    // "Claude Fable 5.1" → "claude-fable-5-1"
    internal static string ToModelId(string displayName) =>
        displayName.ToLowerInvariant().Replace(' ', '-').Replace('.', '-');

    internal static decimal? ExtractPrice(string cell)
    {
        var m = PriceRegex().Match(cell);
        return m.Success ? decimal.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    internal static bool RatesWithinFactor(PricingCatalog.RateBlock existing, ParsedModel updated, decimal factor)
    {
        return WithinFactor(existing.Input,        updated.Input,        factor)
            && WithinFactor(existing.Output,       updated.Output,       factor)
            && WithinFactor(existing.CacheWrite5m, updated.CacheWrite5m, factor)
            && WithinFactor(existing.CacheWrite1h, updated.CacheWrite1h, factor)
            && WithinFactor(existing.CacheHit,     updated.CacheHit,     factor);
    }

    private static bool WithinFactor(decimal existing, decimal updated, decimal factor)
    {
        if (existing <= 0) return true;
        var ratio = updated / existing;
        return ratio <= factor && ratio >= 1m / factor;
    }

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]+\)")]
    private static partial Regex MarkdownLinkRegex();

    [GeneratedRegex(@"\s*\([^)]+\)")]
    private static partial Regex ParenRegex();

    [GeneratedRegex(@"\$(\d+(?:\.\d+)?)")]
    private static partial Regex PriceRegex();
}

internal sealed record ParsedModel(
    string ModelId, string DisplayName,
    decimal Input, decimal Output,
    decimal CacheWrite5m, decimal CacheWrite1h, decimal CacheHit);
