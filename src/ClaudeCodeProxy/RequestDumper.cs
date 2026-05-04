using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace ClaudeCodeProxy;

/// <summary>
/// Writes inbound request bodies (and the post-mutation body when applicable)
/// to disk for debugging. Auto-disables after Dump:MaxRequests requests.
///
/// File naming:
/// - {timestamp}-original.json   — body as received (pretty JSON if parsed, raw bytes otherwise)
/// - {timestamp}-mutated.json    — body after PromptRewriter, only when mutation happened
/// - {timestamp}-path.txt        — request path + query, written once per request
/// </summary>
public sealed class RequestDumper(IOptionsMonitor<DumpOptions> options, ILogger<RequestDumper> logger)
{
    private int _count;
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    /// <summary>
    /// Writes a labeled dump.
    /// Pass `node` for JSON content (pretty-printed), `rawBytes` for raw fallback,
    /// or both (node wins). `requestPath` is written as a sidecar file when set —
    /// only the "original" call should pass it to avoid duplicate path files.
    /// </summary>
    public async Task DumpAsync(
        string label,
        JsonNode? node,
        byte[]? rawBytes,
        string? requestPath,
        CancellationToken ct)
    {
        var opts = options.CurrentValue;
        if (!opts.Enabled) return;

        // Cap is on requests, not files. "original" counts; "mutated" rides
        // along with whichever original it was paired with — if the original
        // was within budget, its mutated counterpart is too.
        if (label == "original")
        {
            if (_count >= opts.MaxRequests) return;
            Interlocked.Increment(ref _count);
        }

        try
        {
            Directory.CreateDirectory(opts.OutputPath);
            var ts = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}";
            var bodyFile = Path.Combine(opts.OutputPath, $"{ts}-{label}.json");

            if (node is not null)
                await File.WriteAllTextAsync(bodyFile, node.ToJsonString(Pretty), ct);
            else if (rawBytes is not null)
                await File.WriteAllBytesAsync(bodyFile, rawBytes, ct);
            else
                return;

            if (requestPath is not null)
            {
                var pathFile = Path.Combine(opts.OutputPath, $"{ts}-path.txt");
                await File.WriteAllTextAsync(pathFile, requestPath, ct);
            }

            logger.LogDebug("Dumped {Label} to {File}", label, bodyFile);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to write dump ({Label})", label);
        }
    }
}
