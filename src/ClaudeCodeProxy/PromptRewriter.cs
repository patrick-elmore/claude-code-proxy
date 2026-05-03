using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace ClaudeCodeProxy;

public sealed class PromptRewriter(IOptionsMonitor<ProxyOptions> options, ILogger<PromptRewriter> logger)
{
    public async Task RewriteAsync(JsonNode? root)
    {
        if (root is not JsonObject body) return;
        if (body["system"] is not JsonArray system || system.Count < 2) return;
        if (system[1] is not JsonObject second) return;

        var text = second["text"]?.GetValue<string>();
        if (text is null || !text.Contains("<env>")) return;

        var promptPath = options.CurrentValue.PromptFilePath;
        if (string.IsNullOrWhiteSpace(promptPath))
        {
            logger.LogDebug("No PromptFilePath configured — passing through unmodified");
            return;
        }

        if (!File.Exists(promptPath))
        {
            logger.LogWarning("Prompt file not found at {Path} — passing through unmodified", promptPath);
            return;
        }

        var customPrompt = await File.ReadAllTextAsync(promptPath);

        foreach (var (key, value) in options.CurrentValue.Variables)
            customPrompt = customPrompt.Replace("{{" + key + "}}", value);

        var envAndAfter = text[text.LastIndexOf("<env>")..];
        second["text"] = customPrompt + envAndAfter;

        logger.LogDebug("System prompt rewritten from {Path}", promptPath);
    }
}
