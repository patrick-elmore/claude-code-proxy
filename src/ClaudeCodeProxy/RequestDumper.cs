using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace ClaudeCodeProxy;

public sealed class RequestDumper(IOptionsMonitor<DumpOptions> options, ILogger<RequestDumper> logger)
{
    private int _count;
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    public async Task DumpAsync(string label, JsonNode? node, CancellationToken ct)
    {
        var opts = options.CurrentValue;
        if (!opts.Enabled || _count >= opts.MaxRequests) return;

        if (label == "original")
            Interlocked.Increment(ref _count);

        try
        {
            Directory.CreateDirectory(opts.OutputPath);
            var file = Path.Combine(opts.OutputPath,
                $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}-{label}.json");
            var json = node?.ToJsonString(Pretty) ?? "null";
            await File.WriteAllTextAsync(file, json, ct);
            logger.LogDebug("Dumped {Label} request body to {File}", label, file);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to write debug dump ({Label})", label);
        }
    }

    /// <summary>
    /// Dumps the raw request body bytes for non-Anthropic-format paths (e.g. Vertex AI).
    /// Records the path so we can correlate dumps with their endpoint.
    /// </summary>
    public async Task DumpRawAsync(string requestPath, byte[] bytes, CancellationToken ct)
    {
        var opts = options.CurrentValue;
        if (!opts.Enabled || _count >= opts.MaxRequests) return;

        Interlocked.Increment(ref _count);

        try
        {
            Directory.CreateDirectory(opts.OutputPath);
            var ts = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}";
            var bodyFile = Path.Combine(opts.OutputPath, $"{ts}-raw.json");
            var pathFile = Path.Combine(opts.OutputPath, $"{ts}-raw.path.txt");
            await File.WriteAllBytesAsync(bodyFile, bytes, ct);
            await File.WriteAllTextAsync(pathFile, requestPath, ct);
            logger.LogDebug("Dumped raw body ({Bytes} bytes, path {Path}) to {File}",
                bytes.Length, requestPath, bodyFile);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to write raw dump");
        }
    }
}
