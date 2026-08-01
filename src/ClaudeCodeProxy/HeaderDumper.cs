using System.Text;
using Microsoft.Extensions.Options;

namespace ClaudeCodeProxy;

// Diagnostic tool for verifying which headers (including auth) arrive on incoming requests.
public sealed class HeaderDumper(IOptionsMonitor<HeaderDumpOptions> options, ILogger<HeaderDumper> logger)
{
    private const string OutputPath = @"C:\tmp\headers.txt";
    private const int MaxAttempts = 20;
    private const int RetryDelayMs = 50;

    public async Task DumpAsync(HttpRequest request, CancellationToken ct)
    {
        if (!options.CurrentValue.Enabled) return;

        var sb = new StringBuilder();
        sb.AppendLine($"---- {DateTimeOffset.UtcNow:O} {request.Method} {request.Path}{request.QueryString} ----");
        foreach (var (key, values) in request.Headers)
            sb.AppendLine($"{key}: {string.Join(", ", values.ToArray())}");
        sb.AppendLine();

        var text = sb.ToString();

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(OutputPath)!);

            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                try
                {
                    await File.AppendAllTextAsync(OutputPath, text, ct);
                    return;
                }
                catch (IOException) when (attempt < MaxAttempts)
                {
                    await Task.Delay(RetryDelayMs, ct);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to write header dump after {Attempts} attempts", MaxAttempts);
        }
    }
}
