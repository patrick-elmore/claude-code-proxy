using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace ClaudeCodeProxy;

/// <summary>
/// Mutates Claude Code's system prompt by replacing the static behavioral
/// portion of the bulk block while preserving the attribution header,
/// the identity-prefix block, and the dynamic content (env, memory, gitStatus).
///
/// Block identification is by content (not position):
/// - Attribution header: text starts with "x-anthropic-billing-header"
/// - Identity prefix: text exact-matches one of the three known CLI prefixes
/// - Bulk: the longest remaining block
///
/// Within the bulk block, the static-to-dynamic boundary is found by
/// searching for known section markers in priority order (Session-specific
/// guidance first, then auto memory, then Environment).
///
/// Any unmet condition results in a no-op (returns false). Caller forwards
/// the original body unchanged.
/// </summary>
public sealed class PromptRewriter(IOptionsMonitor<ProxyOptions> options, ILogger<PromptRewriter> logger)
{
    // From leaked Claude Code source: src/constants/system.ts:10-12
    private static readonly HashSet<string> KnownIdentityPrefixes = new(StringComparer.Ordinal)
    {
        "You are Claude Code, Anthropic's official CLI for Claude.",
        "You are Claude Code, Anthropic's official CLI for Claude, running within the Claude Agent SDK.",
        "You are a Claude agent, built on Anthropic's Claude Agent SDK.",
    };

    // Priority-ordered markers. The first one found in the bulk text is the
    // static-to-dynamic boundary. Each is preceded by a newline to avoid
    // false matches on header text mentioned inside other sections.
    private static readonly string[] DynamicBoundaryMarkers =
    {
        "\n# Session-specific guidance\n",
        "\n# auto memory\n",
        "\n# Environment\n",
    };

    private const string AttributionPrefix = "x-anthropic-billing-header";

    /// <summary>
    /// Returns true if the body was mutated, false if it was passed through unchanged.
    /// </summary>
    public async Task<bool> RewriteAsync(JsonNode? root)
    {
        if (root is not JsonObject body) return false;
        if (body["system"] is not JsonArray system || system.Count == 0) return false;

        var promptPath = options.CurrentValue.PromptFilePath;
        if (string.IsNullOrWhiteSpace(promptPath))
        {
            logger.LogDebug("No PromptFilePath configured — passthrough");
            return false;
        }
        if (!File.Exists(promptPath))
        {
            logger.LogWarning("Prompt file not found at {Path} — passthrough", promptPath);
            return false;
        }

        var bulkBlock = FindBulkBlock(system);
        if (bulkBlock is null)
        {
            logger.LogDebug("No bulk system block found — passthrough (proactive mode or unusual shape)");
            return false;
        }

        var bulkText = bulkBlock["text"]!.GetValue<string>();
        var (boundaryIndex, marker) = FindDynamicBoundary(bulkText);
        if (boundaryIndex < 0)
        {
            logger.LogDebug("No dynamic boundary marker found in bulk block — passthrough");
            return false;
        }

        var dynamicPortion = bulkText[boundaryIndex..];
        var customPrompt = await File.ReadAllTextAsync(promptPath);
        foreach (var (key, value) in options.CurrentValue.Variables)
            customPrompt = customPrompt.Replace("{{" + key + "}}", value);

        bulkBlock["text"] = customPrompt + dynamicPortion;

        logger.LogDebug(
            "Mutated bulk block: replaced {OldStaticLen} chars before {Marker} with {NewStaticLen} chars from {Path}",
            boundaryIndex, marker.Trim(), customPrompt.Length, promptPath);
        return true;
    }

    private static JsonObject? FindBulkBlock(JsonArray system)
    {
        JsonObject? bulk = null;
        var bulkLen = -1;
        foreach (var node in system)
        {
            if (node is not JsonObject blk) continue;
            var text = blk["text"]?.GetValue<string>();
            if (text is null) continue;
            if (text.StartsWith(AttributionPrefix, StringComparison.Ordinal)) continue;
            if (KnownIdentityPrefixes.Contains(text)) continue;
            if (text.Length > bulkLen)
            {
                bulkLen = text.Length;
                bulk = blk;
            }
        }
        return bulk;
    }

    private static (int index, string marker) FindDynamicBoundary(string text)
    {
        foreach (var m in DynamicBoundaryMarkers)
        {
            var idx = text.IndexOf(m, StringComparison.Ordinal);
            if (idx >= 0) return (idx, m);
        }
        return (-1, "");
    }
}
