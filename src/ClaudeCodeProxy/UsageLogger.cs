using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace ClaudeCodeProxy;

public sealed class UsageLogger(
    ILogger<UsageLogger> logger,
    IOptionsMonitor<DumpOptions> dumpOpts,
    StatsTracker stats)
{
    public async Task TapAndForwardAsync(Stream upstream, Stream downstream, string? model, CancellationToken ct)
    {
        var current = new RequestStats(
            Guid.NewGuid().ToString("N")[..8],
            DateTimeOffset.UtcNow)
        {
            Model = model,
        };

        var buffer = new byte[8192];
        var decoder = Encoding.UTF8.GetDecoder();
        var sb = new StringBuilder();

        var dumpOptsCurrent = dumpOpts.CurrentValue;
        var captureForDump = dumpOptsCurrent.Enabled ? new StringBuilder() : null;

        while (true)
        {
            var n = await upstream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            if (n == 0) break;

            await downstream.WriteAsync(buffer.AsMemory(0, n), ct);
            await downstream.FlushAsync(ct);

            var charCount = decoder.GetCharCount(buffer, 0, n, flush: false);
            if (charCount > 0)
            {
                var chars = new char[charCount];
                decoder.GetChars(buffer, 0, n, chars, 0, flush: false);
                sb.Append(chars);
                captureForDump?.Append(chars);

                while (true)
                {
                    var s = sb.ToString();
                    var idx = s.IndexOf("\n\n", StringComparison.Ordinal);
                    if (idx < 0) break;

                    var eventBlock = s[..idx];
                    sb.Remove(0, idx + 2);
                    ProcessEvent(eventBlock, current);
                }
            }
        }

        current.DurationMs = (long)(DateTimeOffset.UtcNow - current.Timestamp).TotalMilliseconds;

        // rawPredict returns plain JSON rather than SSE; fall back when no SSE events were found.
        if (!current.HasUsage && sb.Length > 0)
            TryParseJsonBody(sb.ToString(), current);

        if (current.HasUsage)
        {
            stats.Record(current);
            var totalInput = current.InputUncached + current.CacheRead + current.CacheCreation;
            var pct = totalInput > 0 ? 100.0 * current.CacheRead / totalInput : 0;
            logger.LogInformation(
                "[{ReqId}] cache: read={CacheRead} created={CacheCreated} uncached={Uncached} output={Output} ({HitPct:F0}% hit on {TotalIn} input) {DurationMs}ms",
                current.RequestId, current.CacheRead, current.CacheCreation, current.InputUncached, current.OutputTokens, pct, totalInput, current.DurationMs);
        }
        else
        {
            stats.RecordFailed(current);
            logger.LogInformation(
                "[{ReqId}] no usage extracted from response (HasUsage=false, response chars={Chars}) {DurationMs}ms",
                current.RequestId, captureForDump?.Length ?? -1, current.DurationMs);
        }

        if (captureForDump is not null)
        {
            try
            {
                Directory.CreateDirectory(dumpOptsCurrent.OutputPath);
                var path = Path.Combine(
                    dumpOptsCurrent.OutputPath,
                    $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}-{current.RequestId}-response.txt");
                await File.WriteAllTextAsync(path, captureForDump.ToString(), ct);
                logger.LogInformation("[{ReqId}] response dumped to {File}", current.RequestId, path);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[{ReqId}] failed to dump response", current.RequestId);
            }
        }
    }

    private static void TryParseJsonBody(string body, RequestStats current)
    {
        try
        {
            var node = JsonNode.Parse(body.Trim());
            if (node is null) return;

            var usage = node["usage"];
            if (usage is not null)
            {
                current.InputUncached = (int?)usage["input_tokens"]             ?? 0;
                current.CacheRead     = (int?)usage["cache_read_input_tokens"] ?? 0;
                var ccTotal           = (int?)usage["cache_creation_input_tokens"] ?? 0;
                var ccBreakdown       = usage["cache_creation"];
                if (ccBreakdown is not null)
                {
                    current.CacheCreation5m = (int?)ccBreakdown["ephemeral_5m_input_tokens"] ?? 0;
                    current.CacheCreation1h = (int?)ccBreakdown["ephemeral_1h_input_tokens"] ?? 0;
                }
                else
                {
                    current.CacheCreation5m = ccTotal;
                    current.CacheCreation1h = 0;
                }
                current.OutputTokens = (int?)usage["output_tokens"] ?? 0;
                current.HasUsage     = true;
                return;
            }

            // count-tokens response: { "input_tokens": N } — no output or cache breakdown.
            var inputTokens = node["input_tokens"];
            if (inputTokens is not null)
            {
                current.InputUncached = (int?)inputTokens ?? 0;
                current.HasUsage      = true;
            }
        }
        catch
        {
        }
    }

    private static void ProcessEvent(string eventBlock, RequestStats current)
    {
        foreach (var line in eventBlock.Split('\n'))
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            var json = line[6..].Trim();
            if (json.Length == 0 || json == "[DONE]") continue;

            try
            {
                var node = JsonNode.Parse(json);
                if (node is null) continue;

                var type = node["type"]?.GetValue<string>();
                if (type == "message_start")
                {
                    var usage = node["message"]?["usage"];
                    if (usage is not null)
                    {
                        current.InputUncached = (int?)usage["input_tokens"]             ?? 0;
                        current.CacheRead     = (int?)usage["cache_read_input_tokens"] ?? 0;
                        var ccTotal           = (int?)usage["cache_creation_input_tokens"] ?? 0;
                        var ccBreakdown       = usage["cache_creation"];
                        if (ccBreakdown is not null)
                        {
                            current.CacheCreation5m = (int?)ccBreakdown["ephemeral_5m_input_tokens"] ?? 0;
                            current.CacheCreation1h = (int?)ccBreakdown["ephemeral_1h_input_tokens"] ?? 0;
                        }
                        else
                        {
                            // No breakdown present — attribute the total to default 5m TTL.
                            current.CacheCreation5m = ccTotal;
                            current.CacheCreation1h = 0;
                        }
                        current.OutputTokens = (int?)usage["output_tokens"] ?? current.OutputTokens;
                        current.HasUsage     = true;
                    }
                }
                else if (type == "message_delta")
                {
                    var usage = node["usage"];
                    if (usage is not null)
                    {
                        current.OutputTokens = (int?)usage["output_tokens"] ?? current.OutputTokens;
                    }
                }
            }
            catch
            {
                // Malformed event — ignore silently to avoid breaking response forwarding.
            }
        }
    }
}

public sealed class RequestStats
{
    public string RequestId { get; }
    public DateTimeOffset Timestamp { get; }
    public string? Model { get; set; }
    public int InputUncached { get; set; }
    public int CacheRead { get; set; }
    public int CacheCreation5m { get; set; }
    public int CacheCreation1h { get; set; }
    public int CacheCreation => CacheCreation5m + CacheCreation1h;
    public int OutputTokens { get; set; }
    public long DurationMs { get; set; }
    public bool HasUsage { get; set; }

    public RequestStats(string requestId, DateTimeOffset timestamp)
    {
        RequestId = requestId;
        Timestamp = timestamp;
    }
}
