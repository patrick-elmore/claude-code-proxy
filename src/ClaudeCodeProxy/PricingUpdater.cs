using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeCodeProxy;

// Hourly: downloads Anthropic's pricing page as markdown, parses the model pricing table, regenerates pricing.json
// in memory, and rewrites the file only when the SHA-256 of the new content differs from the file on disk.
// A change invalidates PricingCatalog so the next lookup reloads it.
//
// Generation is a pure function of (page, existing file, effective date), so an unchanged page always yields identical bytes.
// When a model's standard rates change, the previous rates become a dated promotion window ending the day before the
// effective date, so requests before the update keep their old price.
// Promotions, other per-model fields, top-level metadata, and models absent from the page are carried over from
// the existing file (expired promotions and retired models are needed for historical stats). long_context comes
// from the page when the page lists an over-threshold row for the model, otherwise it is carried over.
//
// Any guard failure (fetch, parse, sanity check, validation) leaves pricing.json and the in-memory table untouched.
public sealed partial class PricingUpdater : BackgroundService
{
    private const string PricingDocUrl = "https://platform.claude.com/docs/en/about-claude/pricing.md";
    private const int MinModelCount = 4;
    private const decimal MaxRateFactor = 10m;
    private static readonly DateOnly LegacyStandardStart = new(2000, 1, 1);

    private readonly PricingCatalog _catalog;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PricingUpdater> _logger;
    private readonly string _pricingPath;

    public PricingUpdater(PricingCatalog catalog, IHttpClientFactory httpClientFactory, ILogger<PricingUpdater> logger)
        : this(catalog, httpClientFactory, logger, Path.Combine(AppContext.BaseDirectory, "pricing.json"))
    {
    }

    internal PricingUpdater(PricingCatalog catalog, IHttpClientFactory httpClientFactory,
        ILogger<PricingUpdater> logger, string pricingPath)
    {
        _catalog = catalog;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _pricingPath = pricingPath;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Run once on startup so a freshly deployed service doesn't wait a full hour.
        await TryUpdateAsync(ct);

        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        while (await timer.WaitForNextTickAsync(ct))
            await TryUpdateAsync(ct);
    }

    internal async Task TryUpdateAsync(CancellationToken ct)
    {
        string content;
        try
        {
            var client = _httpClientFactory.CreateClient("pricing-docs");
            using var response = await client.GetAsync(PricingDocUrl, ct);
            response.EnsureSuccessStatusCode();
            content = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Pricing update: failed to fetch {Url}; keeping existing pricing", PricingDocUrl);
            return;
        }

        List<ParsedModel> models;
        try
        {
            models = ParsePricingPage(content);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Pricing update: pricing page did not parse; keeping existing pricing");
            return;
        }

        var problem = FindProblem(models, _catalog);
        if (problem is not null)
        {
            _logger.LogWarning("Pricing update: {Problem}; keeping existing pricing", problem);
            return;
        }

        var path = _pricingPath;
        byte[]? existingBytes;
        byte[] newBytes;
        try
        {
            existingBytes = File.Exists(path) ? await File.ReadAllBytesAsync(path, ct) : null;
            var existingJson = existingBytes is null ? null : Encoding.UTF8.GetString(existingBytes);
            var newJson = GeneratePricingJson(existingJson, models, DateOnly.FromDateTime(DateTime.Now));

            if (!PricingCatalog.IsValid(newJson))
            {
                _logger.LogWarning("Pricing update: generated pricing.json failed validation; keeping existing pricing");
                return;
            }

            newBytes = Encoding.UTF8.GetBytes(newJson);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Pricing update: failed to generate pricing.json; keeping existing pricing");
            return;
        }

        var newHash = Convert.ToHexString(SHA256.HashData(newBytes));
        var existingHash = existingBytes is null ? null : Convert.ToHexString(SHA256.HashData(existingBytes));
        if (newHash == existingHash)
        {
            _logger.LogDebug("Pricing update: no changes (sha256 {Hash})", newHash[..12]);
            return;
        }

        // Temp file + move keeps a concurrent reader from ever seeing a partial file.
        try
        {
            var tempPath = path + ".tmp";
            await File.WriteAllBytesAsync(tempPath, newBytes, ct);
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "Pricing update: failed to write {Path}; keeping existing pricing", path);
            return;
        }

        _catalog.Invalidate();
        _logger.LogInformation("Pricing update: wrote {Path} ({Count} models parsed, sha256 {Old} -> {New})",
            path, models.Count, existingHash?[..12] ?? "none", newHash[..12]);
    }

    internal static List<ParsedModel> ParsePricingPage(string content) =>
        ParseTableLines(content.Split('\n'));

    // Returns a description of the first sanity-check failure, or null when the parsed data is safe to apply.
    internal static string? FindProblem(List<ParsedModel> models, PricingCatalog catalog)
    {
        if (models.Count < MinModelCount)
            return $"only {models.Count} models parsed (min {MinModelCount})";

        var duplicate = models.GroupBy(m => m.ModelId).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            return $"model id '{duplicate.Key}' appears more than once";

        var badRate = models.Find(HasNonPositiveRate);
        if (badRate is not null)
            return $"model '{badRate.ModelId}' has a non-positive rate";

        // For models in both old and new data, no single rate may change by more than MaxRateFactor.
        foreach (var m in models)
        {
            if (catalog.TryGetRates(m.ModelId, out var existing) && !RatesWithinFactor(existing, m, MaxRateFactor))
                return $"model '{m.ModelId}' has a rate change exceeding {MaxRateFactor}x";
        }

        return null;
    }

    // Deterministic: fixed property order, LF newlines, rates formatted to at least two decimals, one trailing newline.
    internal static string GeneratePricingJson(string? existingJson, IReadOnlyList<ParsedModel> models, DateOnly effectiveDate)
    {
        var duplicate = models.GroupBy(m => m.ModelId).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"Duplicate model id '{duplicate.Key}'");

        JsonDocument? existingDoc = null;
        try
        {
            if (existingJson is not null)
                existingDoc = JsonDocument.Parse(existingJson);
        }
        catch (JsonException)
        {
            // An unreadable existing file is regenerated from defaults.
        }

        using (existingDoc)
        {
            JsonElement? existingRoot = existingDoc is { RootElement.ValueKind: JsonValueKind.Object }
                ? existingDoc.RootElement
                : null;
            JsonElement? existingModels = existingRoot is { } root
                && root.TryGetProperty("models", out var m) && m.ValueKind == JsonValueKind.Object
                    ? m
                    : null;

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
            {
                Indented = true,
                IndentSize = 2,
                NewLine = "\n",
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }))
            {
                writer.WriteStartObject();

                if (existingRoot is { } top)
                {
                    foreach (var prop in top.EnumerateObject().Where(p => p.Name != "models"))
                        prop.WriteTo(writer);
                }
                else
                {
                    writer.WriteString("currency", "USD");
                    writer.WriteBoolean("unit_per_million_tokens", true);
                    writer.WriteNumber("long_context_threshold_input_tokens", 200000);
                }

                writer.WritePropertyName("models");
                writer.WriteStartObject();

                var pageIds = new HashSet<string>(models.Select(x => x.ModelId));
                foreach (var model in models)
                {
                    writer.WritePropertyName(model.ModelId);
                    writer.WriteStartObject();
                    writer.WriteString("display_name", model.DisplayName);
                    writer.WritePropertyName("standard");
                    WriteRates(writer, model.Input, model.Output, model.CacheWrite5m, model.CacheWrite1h, model.CacheHit);

                    if (model.LongContext is { } lc)
                    {
                        writer.WritePropertyName("long_context");
                        WriteRates(writer, lc.Input, lc.Output, lc.CacheWrite5m, lc.CacheWrite1h, lc.CacheHit);
                        writer.WriteNumber("long_context_threshold_input_tokens", lc.ThresholdInputTokens);
                    }

                    var entry = existingModels is { } em
                        && em.TryGetProperty(model.ModelId, out var found) && found.ValueKind == JsonValueKind.Object
                            ? found
                            : (JsonElement?)null;
                    var standardChanged = entry is { } previous && StandardChanged(previous, model);

                    if (entry is null || standardChanged)
                        writer.WriteString("standard_effective_from", FormatDate(effectiveDate));

                    if (standardChanged)
                        WriteStandardHistory(writer, entry!.Value, effectiveDate);

                    if (entry is { } existingEntry)
                    {
                        foreach (var prop in existingEntry.EnumerateObject()
                                     .Where(p => !PageOwnedProperty(p.Name, model, standardChanged)))
                            prop.WriteTo(writer);
                    }

                    writer.WriteEndObject();
                }

                if (existingModels is { } carried)
                {
                    foreach (var prop in carried.EnumerateObject().Where(p => !pageIds.Contains(p.Name)))
                        prop.WriteTo(writer);
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length) + "\n";
        }
    }

    private static bool PageOwnedProperty(string name, ParsedModel model, bool standardChanged) =>
        name is "display_name" or "standard"
            || (model.LongContext is not null && name is ("long_context" or "long_context_threshold_input_tokens"))
            || (standardChanged && name is ("standard_effective_from" or "promotions"));

    private static bool StandardChanged(JsonElement entry, ParsedModel model)
    {
        if (!entry.TryGetProperty("standard", out var s)) return false;
        return s.GetProperty("input").GetDecimal() != model.Input
            || s.GetProperty("output").GetDecimal() != model.Output
            || s.GetProperty("cache_write_5m").GetDecimal() != model.CacheWrite5m
            || s.GetProperty("cache_write_1h").GetDecimal() != model.CacheWrite1h
            || s.GetProperty("cache_hit").GetDecimal() != model.CacheHit;
    }

    // Keeps existing promotions first so they win over the history window where the two overlap.
    private static void WriteStandardHistory(Utf8JsonWriter writer, JsonElement entry, DateOnly effectiveDate)
    {
        var starts = entry.TryGetProperty("standard_effective_from", out var from)
            ? DateOnly.ParseExact(from.GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture)
            : LegacyStandardStart;
        var ends = effectiveDate.AddDays(-1);

        writer.WritePropertyName("promotions");
        writer.WriteStartArray();
        if (entry.TryGetProperty("promotions", out var promotions) && promotions.ValueKind == JsonValueKind.Array)
        {
            foreach (var promotion in promotions.EnumerateArray())
                promotion.WriteTo(writer);
        }
        if (starts <= ends)
        {
            writer.WriteStartObject();
            writer.WriteString("description", $"Standard rates before {FormatDate(effectiveDate)} update");
            writer.WriteString("starts", FormatDate(starts));
            writer.WriteString("ends", FormatDate(ends));
            writer.WritePropertyName("rates");
            entry.GetProperty("standard").WriteTo(writer);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static string FormatDate(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static void WriteRates(Utf8JsonWriter writer, decimal input, decimal output,
        decimal cacheWrite5m, decimal cacheWrite1h, decimal cacheHit)
    {
        writer.WriteStartObject();
        WriteRate(writer, "input", input);
        WriteRate(writer, "output", output);
        WriteRate(writer, "cache_write_5m", cacheWrite5m);
        WriteRate(writer, "cache_write_1h", cacheWrite1h);
        WriteRate(writer, "cache_hit", cacheHit);
        writer.WriteEndObject();
    }

    private static void WriteRate(Utf8JsonWriter writer, string name, decimal value)
    {
        writer.WritePropertyName(name);
        writer.WriteRawValue(value.ToString("0.00####", CultureInfo.InvariantCulture));
    }

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
        var overThreshold = new List<(ParsedModel Rates, int ThresholdInputTokens)>();
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

            var model = new ParsedModel(ToModelId(displayName), displayName,
                input.Value, output.Value, cw5m.Value, cw1h.Value, ch.Value);

            var qualifier = LongContextQualifierRegex().Match(cells[0]);
            if (qualifier.Success && qualifier.Groups[1].Value == "over")
                overThreshold.Add((model, int.Parse(qualifier.Groups[2].Value.Replace(",", ""), CultureInfo.InvariantCulture)));
            else
                result.Add(model);
        }

        // A model with prompt-length tiers has a standard row and an over-threshold row; the over row becomes LongContext.
        foreach (var (rates, threshold) in overThreshold)
        {
            var idx = result.FindIndex(m => m.ModelId == rates.ModelId && m.LongContext is null);
            if (idx < 0)
                throw new InvalidOperationException($"Over-threshold row for '{rates.ModelId}' has no standard row");

            var standard = result[idx];
            result[idx] = standard with
            {
                LongContext = new LongContextRates(threshold, rates.Input, rates.Output,
                    rates.CacheWrite5m, rates.CacheWrite1h, rates.CacheHit)
            };
        }

        return result;
    }

    private static bool HasNonPositiveRate(ParsedModel m) =>
        m.Input <= 0 || m.Output <= 0 || m.CacheWrite5m <= 0 || m.CacheWrite1h <= 0 || m.CacheHit <= 0
        || (m.LongContext is { } lc && (lc.Input <= 0 || lc.Output <= 0 || lc.CacheWrite5m <= 0 || lc.CacheWrite1h <= 0 || lc.CacheHit <= 0));

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

    [GeneratedRegex(@"\((?:for prompts )?(up to|over) ([\d,]+) tokens\)")]
    private static partial Regex LongContextQualifierRegex();
}

internal sealed record ParsedModel(
    string ModelId, string DisplayName,
    decimal Input, decimal Output,
    decimal CacheWrite5m, decimal CacheWrite1h, decimal CacheHit,
    LongContextRates? LongContext = null);

internal sealed record LongContextRates(
    int ThresholdInputTokens,
    decimal Input, decimal Output,
    decimal CacheWrite5m, decimal CacheWrite1h, decimal CacheHit);
