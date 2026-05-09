using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace ClaudeCodeProxy;

// Dumps: {ts}-original.json, {ts}-mutated.json (when mutated), {ts}-path.txt.
public sealed class RequestDumper(IOptionsMonitor<DumpOptions> options, ILogger<RequestDumper> logger)
{
    private int _count;
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    // Only the "original" call should pass requestPath to avoid duplicate sidecar files.
    public async Task DumpAsync(
        string label,
        JsonNode? node,
        byte[]? rawBytes,
        string? requestPath,
        CancellationToken ct)
    {
        var opts = options.CurrentValue;
        if (!opts.Enabled) return;

        // "original" counts against MaxRequests; "mutated" always accompanies its original.
        if (label == "original")
        {
            if (_count >= opts.MaxRequests) return;
            Interlocked.Increment(ref _count);
        }

        if (string.IsNullOrWhiteSpace(opts.OutputPath) || opts.OutputPath.Contains('<'))
        {
            logger.LogWarning(
                "Dump:Enabled is true but Dump:OutputPath is unset or still the placeholder ({Path}). " +
                "Set it to a writable directory in appsettings.json before enabling dumps.",
                opts.OutputPath);
            return;
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
