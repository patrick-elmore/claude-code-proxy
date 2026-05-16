using System.Collections.Frozen;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace ClaudeCodeProxy;

// Block identification is by content, not position. Any unmet condition is a no-op.
public sealed class PromptRewriter(IOptionsMonitor<ProxyOptions> options, ILogger<PromptRewriter> logger)
{
    // From the Claude Code CLI source: src/constants/system.ts:10-12.
    private static readonly FrozenSet<string> KnownIdentityPrefixes = new[]
    {
        "You are Claude Code, Anthropic's official CLI for Claude.",
        "You are Claude Code, Anthropic's official CLI for Claude, running within the Claude Agent SDK.",
        "You are a Claude agent, built on Anthropic's Claude Agent SDK.",
    }.ToFrozenSet(StringComparer.Ordinal);

    // Each marker is newline-prefixed to avoid false matches where these heading names appear in prose.
    private static readonly string[] DynamicBoundaryMarkers =
    {
        "\n# Session-specific guidance\n",
        "\n# auto memory\n",
        "\n# Environment\n",
    };

    private const string AttributionPrefix = "x-anthropic-billing-header";

    public async Task<RewriteOutcome> RewriteAsync(JsonNode? root)
    {
        // Not a message-shaped request — rewriting was never applicable, nothing to track.
        if (root is not JsonObject body) return RewriteOutcome.NotApplicable;
        if (body["system"] is not JsonArray system || system.Count == 0) return RewriteOutcome.NotApplicable;

        var promptPath = options.CurrentValue.PromptFilePath;
        if (string.IsNullOrWhiteSpace(promptPath))
        {
            logger.LogDebug("No PromptFilePath configured — passthrough");
            return RewriteOutcome.Failure(PromptFailureMode.PromptNotConfigured);
        }
        if (!File.Exists(promptPath))
        {
            logger.LogWarning("Prompt file not found at {Path} — passthrough", promptPath);
            return RewriteOutcome.Failure(PromptFailureMode.PromptFileNotFound);
        }

        var (bulkBlock, identityFound) = FindBulkBlock(system);
        var identityNotRecognized = !identityFound;

        if (identityNotRecognized)
            logger.LogWarning(
                "No known Claude Code identity block found in system[] — the CLI may have updated its identity strings. Update KnownIdentityPrefixes in PromptRewriter.cs.");

        if (bulkBlock is null)
        {
            logger.LogDebug("No bulk system block found — passthrough (proactive mode or unusual shape)");
            return RewriteOutcome.Failure(PromptFailureMode.NoBulkBlock, identityNotRecognized);
        }

        var bulkText = bulkBlock["text"]?.GetValue<string>();
        if (bulkText is null)
        {
            logger.LogDebug("Bulk block has no text field — passthrough");
            return RewriteOutcome.Failure(PromptFailureMode.NoBulkBlock, identityNotRecognized);
        }

        var (boundaryIndex, marker) = FindDynamicBoundary(bulkText);
        if (boundaryIndex < 0)
        {
            logger.LogDebug("No dynamic boundary marker found in bulk block — passthrough");
            return RewriteOutcome.Failure(PromptFailureMode.NoBoundaryMarker, identityNotRecognized);
        }

        string customPrompt;
        try
        {
            customPrompt = await File.ReadAllTextAsync(promptPath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read prompt file at {Path} — passthrough", promptPath);
            return RewriteOutcome.Failure(PromptFailureMode.PromptFileReadError, identityNotRecognized);
        }

        foreach (var (key, value) in options.CurrentValue.Variables)
            customPrompt = customPrompt.Replace("{{" + key + "}}", value);

        var dynamicPortion = bulkText[boundaryIndex..];
        bulkBlock["text"] = customPrompt + dynamicPortion;

        logger.LogDebug(
            "Mutated bulk block: replaced {OldStaticLen} chars before {Marker} with {NewStaticLen} chars from {Path}",
            boundaryIndex, marker.Trim(), customPrompt.Length, promptPath);
        return RewriteOutcome.Success(identityNotRecognized);
    }

    private static (JsonObject? bulk, bool identityFound) FindBulkBlock(JsonArray system)
    {
        JsonObject? bulk = null;
        var bulkLen = -1;
        var identityFound = false;
        foreach (var node in system)
        {
            if (node is not JsonObject blk) continue;
            var text = blk["text"]?.GetValue<string>();
            if (text is null) continue;
            if (text.StartsWith(AttributionPrefix, StringComparison.Ordinal)) continue;
            if (KnownIdentityPrefixes.Contains(text)) { identityFound = true; continue; }
            if (text.Length > bulkLen)
            {
                bulkLen = text.Length;
                bulk = blk;
            }
        }
        return (bulk, identityFound);
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
