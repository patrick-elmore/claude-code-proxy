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

    // Cached for 1 minute so prompt edits apply without a restart while avoiding per-request disk reads.
    private static readonly TimeSpan PromptCacheTtl = TimeSpan.FromMinutes(1);
    private CachedPrompt? _cachedPrompt;

    private sealed record CachedPrompt(string Path, string Content, DateTime LoadedAtUtc);

    public async Task<RewriteOutcome> RewriteAsync(JsonNode? root)
    {
        // Not a message-shaped request — rewriting was never applicable, nothing to track.
        if (root is not JsonObject body) return RewriteOutcome.NotApplicable;
        if (body["system"] is not JsonArray system || system.Count == 0) return RewriteOutcome.NotApplicable;
        // max_tokens:1 is Claude Code's model-availability probe — nothing to inject.
        if (body["max_tokens"]?.GetValue<int>() == 1) return RewriteOutcome.NotApplicable;

        var (bulkBlock, identityFound, bulkIndex) = FindBulkBlock(system);
        if (!identityFound)
        {
            logger.LogWarning(
                "No known Claude Code identity block found in system[] — passing through unchanged. The CLI may have updated its identity strings; update KnownIdentityPrefixes in PromptRewriter.cs if needed.");
            return RewriteOutcome.Failure(PromptFailureMode.IdentityNotRecognized);
        }

        var promptPath = options.CurrentValue.PromptFilePath;
        if (string.IsNullOrWhiteSpace(promptPath))
        {
            logger.LogDebug("No PromptFilePath configured — passthrough");
            return RewriteOutcome.Failure(PromptFailureMode.PromptNotConfigured);
        }
        var cached = _cachedPrompt;
        if (cached is not null && (cached.Path != promptPath || DateTime.UtcNow - cached.LoadedAtUtc >= PromptCacheTtl))
            cached = null;
        if (cached is null && !File.Exists(promptPath))
        {
            logger.LogWarning("Prompt file not found at {Path} — passthrough", promptPath);
            return RewriteOutcome.Failure(PromptFailureMode.PromptFileNotFound);
        }

        if (bulkBlock is null)
        {
            // Known identity block(s) only — agent SDK sub-agent with no behavioral prompt to replace.
            logger.LogDebug("No bulk system block found but identity recognized — agent SDK sub-agent, skipping injection");
            return RewriteOutcome.NotApplicable;
        }

        var bulkText = bulkBlock["text"]?.GetValue<string>();
        if (bulkText is null)
        {
            logger.LogDebug("Bulk block has no text field — passthrough");
            return RewriteOutcome.Failure(PromptFailureMode.NoBulkBlock);
        }

        // Read and prepare the custom prompt — shared by both injection strategies below.
        string customPrompt;
        if (cached is not null)
        {
            customPrompt = cached.Content;
        }
        else
        {
            try
            {
                customPrompt = await File.ReadAllTextAsync(promptPath);
                _cachedPrompt = new CachedPrompt(promptPath, customPrompt, DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to read prompt file at {Path} — passthrough", promptPath);
                return RewriteOutcome.Failure(PromptFailureMode.PromptFileReadError);
            }
        }

        foreach (var (key, value) in options.CurrentValue.Variables)
            customPrompt = customPrompt.Replace("{{" + key + "}}", value);

        var (boundaryIndex, marker) = FindDynamicBoundary(bulkText);
        if (boundaryIndex < 0)
        {
            // Claude Code's session titler: single message + title-generation instruction. Skip injection.
            if (body["messages"] is JsonArray msgs && msgs.Count == 1 &&
                bulkText.Contains("Generate a concise, sentence-case title", StringComparison.Ordinal))
            {
                logger.LogDebug("Skipping injection — session title generation request");
                return RewriteOutcome.NotApplicable;
            }

            // Sub-agent or custom-prompted request — no boundary marker, so insert our prompt as
            // a new block before the role block rather than replacing within it.
            system.Insert(bulkIndex, new JsonObject { ["type"] = "text", ["text"] = customPrompt });
            logger.LogDebug("Injected custom prompt as new block at index {Index} (no boundary marker found)", bulkIndex);
            return RewriteOutcome.Success();
        }

        var dynamicPortion = bulkText[boundaryIndex..];
        bulkBlock["text"] = customPrompt + dynamicPortion;

        logger.LogDebug(
            "Mutated bulk block: replaced {OldStaticLen} chars before {Marker} with {NewStaticLen} chars from {Path}",
            boundaryIndex, marker.Trim(), customPrompt.Length, promptPath);
        return RewriteOutcome.Success();
    }

    private static (JsonObject? bulk, bool identityFound, int bulkIndex) FindBulkBlock(JsonArray system)
    {
        JsonObject? bulk = null;
        var bulkLen = -1;
        var identityFound = false;
        var bulkIndex = -1;
        for (var i = 0; i < system.Count; i++)
        {
            if (system[i] is not JsonObject blk) continue;
            var text = blk["text"]?.GetValue<string>();
            if (text is null) continue;
            if (text.StartsWith(AttributionPrefix, StringComparison.Ordinal)) continue;
            if (KnownIdentityPrefixes.Contains(text)) { identityFound = true; continue; }
            if (text.Length > bulkLen) { bulkLen = text.Length; bulk = blk; bulkIndex = i; }
        }
        return (bulk, identityFound, bulkIndex);
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
