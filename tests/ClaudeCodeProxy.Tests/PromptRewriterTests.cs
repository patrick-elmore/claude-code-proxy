using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace ClaudeCodeProxy.Tests;

public sealed class PromptRewriterTests : IDisposable
{
    private readonly string _promptFile;

    public PromptRewriterTests()
    {
        _promptFile = Path.GetTempFileName();
    }

    public void Dispose() => File.Delete(_promptFile);

    // -----------------------------------------------------------------------
    // Not-applicable — no tracking, no mutation
    // -----------------------------------------------------------------------

    [Fact]
    public async Task NoSystemField_NotApplicable()
    {
        var rewriter = BuildRewriter("custom prompt");
        var node = JsonNode.Parse("""{"messages": []}""")!;

        var result = await rewriter.RewriteAsync(node);

        result.ShouldBe(RewriteOutcome.NotApplicable);
        node["messages"]!.ToJsonString().ShouldBe("[]");
    }

    [Fact]
    public async Task EmptySystemArray_NotApplicable()
    {
        var rewriter = BuildRewriter("custom prompt");
        var node = Parse("""{"system": []}""");

        (await rewriter.RewriteAsync(node)).ShouldBe(RewriteOutcome.NotApplicable);
    }

    // -----------------------------------------------------------------------
    // Failure modes — mutation did not complete, specific mode returned
    // -----------------------------------------------------------------------

    [Fact]
    public async Task EmptyPromptFilePath_PromptNotConfigured()
    {
        var rewriter = BuildRewriter(null, promptFilePath: "");
        var node = BulkNode("static\n# Session-specific guidance\ndynamic");

        var result = await rewriter.RewriteAsync(node);

        result.Mutated.ShouldBeFalse();
        result.FailureMode.ShouldBe(PromptFailureMode.PromptNotConfigured);
    }

    [Fact]
    public async Task MissingPromptFile_PromptFileNotFound()
    {
        var rewriter = BuildRewriter(null, promptFilePath: "/nonexistent/path/prompt.md");
        var node = BulkNode("static\n# Session-specific guidance\ndynamic");

        var result = await rewriter.RewriteAsync(node);

        result.Mutated.ShouldBeFalse();
        result.FailureMode.ShouldBe(PromptFailureMode.PromptFileNotFound);
    }

    [Fact]
    public async Task PromptFileLockedAfterExistsCheck_PromptFileReadError()
    {
        // Simulate a file that exists but becomes unreadable — write then open exclusively.
        File.WriteAllText(_promptFile, "some content");
        using var exclusiveLock = new FileStream(_promptFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var rewriter = BuildRewriter(null); // uses _promptFile which now has an exclusive lock
        var node = BulkNode("static\n# Session-specific guidance\ndynamic");

        var result = await rewriter.RewriteAsync(node);

        result.Mutated.ShouldBeFalse();
        result.FailureMode.ShouldBe(PromptFailureMode.PromptFileReadError);
    }

    [Fact]
    public async Task OnlyIdentityBlock_NoBulkBlock()
    {
        var rewriter = BuildRewriter("custom prompt");
        var node = Parse("""
            {"system": [
                {"type": "text", "text": "x-anthropic-billing-header cc_version=test"},
                {"type": "text", "text": "You are Claude Code, Anthropic's official CLI for Claude."}
            ]}
            """);

        var result = await rewriter.RewriteAsync(node);

        result.Mutated.ShouldBeFalse();
        result.FailureMode.ShouldBeNull();
        result.IdentityNotRecognized.ShouldBeFalse();
    }

    [Fact]
    public async Task OnlyAttributionBlock_IdentityNotRecognized()
    {
        var rewriter = BuildRewriter("custom prompt");
        var node = Parse("""
            {"system": [{"type": "text", "text": "x-anthropic-billing-header cc_version=test"}]}
            """);

        var result = await rewriter.RewriteAsync(node);

        result.Mutated.ShouldBeFalse();
        result.FailureMode.ShouldBe(PromptFailureMode.IdentityNotRecognized);
    }

    [Fact]
    public async Task MissingIdentityBlock_PassesThroughUnchanged()
    {
        var rewriter = BuildRewriter("custom prompt");
        var node = Parse("""
            {"system": [
                {"type": "text", "text": "x-anthropic-billing-header cc_version=test"},
                {"type": "text", "text": "bulk content without any known boundary marker here"}
            ]}
            """);

        var result = await rewriter.RewriteAsync(node);

        result.Mutated.ShouldBeFalse();
        result.FailureMode.ShouldBe(PromptFailureMode.IdentityNotRecognized);
        var arr = node["system"]!.AsArray();
        arr.Count.ShouldBe(2);
        arr[1]!["text"]!.GetValue<string>().ShouldBe("bulk content without any known boundary marker here");
    }

    [Fact]
    public async Task NoBoundaryMarker_MultiBlock_InsertsAtCorrectPosition()
    {
        var rewriter = BuildRewriter("custom prompt");
        var node = Parse("""
            {"system": [
                {"type": "text", "text": "x-anthropic-billing-header attribution"},
                {"type": "text", "text": "You are Claude Code, Anthropic's official CLI for Claude."},
                {"type": "text", "text": "## Role\n\nYou are a specialized agent with no boundary markers."}
            ]}
            """);

        var result = await rewriter.RewriteAsync(node);

        result.Mutated.ShouldBeTrue();
        result.IdentityNotRecognized.ShouldBeFalse();
        var arr = node["system"]!.AsArray();
        arr.Count.ShouldBe(4);
        arr[0]!["text"]!.GetValue<string>().ShouldStartWith("x-anthropic-billing-header");
        arr[1]!["text"]!.GetValue<string>().ShouldBe("You are Claude Code, Anthropic's official CLI for Claude.");
        arr[2]!["text"]!.GetValue<string>().ShouldBe("custom prompt");
        arr[3]!["text"]!.GetValue<string>().ShouldStartWith("## Role");
    }

    [Fact]
    public async Task NoBoundaryMarker_RoleBlockContentUnchanged()
    {
        const string roleContent = "## Role\n\nYou are a quantitative analyst. Do not deviate from this role.";
        var rewriter = BuildRewriter("custom prompt");
        var node = new JsonObject
        {
            ["system"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = "You are Claude Code, Anthropic's official CLI for Claude." },
                new JsonObject { ["type"] = "text", ["text"] = roleContent }
            }
        };

        await rewriter.RewriteAsync(node);

        var arr = node["system"]!.AsArray();
        arr[^1]!["text"]!.GetValue<string>().ShouldBe(roleContent);
    }

    [Fact]
    public async Task TitleGenerationRequest_NotApplicable()
    {
        var rewriter = BuildRewriter("custom prompt");
        var node = new JsonObject
        {
            ["system"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = "x-anthropic-billing-header attribution" },
                new JsonObject { ["type"] = "text", ["text"] = "You are Claude Code, Anthropic's official CLI for Claude." },
                new JsonObject { ["type"] = "text", ["text"] = "Generate a concise, sentence-case title (3-7 words) that captures the main topic. Return JSON with a single \"title\" field." }
            },
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = "Fix the login bug" }
            }
        };

        var result = await rewriter.RewriteAsync(node);

        result.ShouldBe(RewriteOutcome.NotApplicable);
        node["system"]!.AsArray().Count.ShouldBe(3); // unchanged
    }

    // -----------------------------------------------------------------------
    // Mutation cases
    // -----------------------------------------------------------------------

    [Fact]
    public async Task HappyPath_ReplacesStaticPortion()
    {
        var rewriter = BuildRewriter("custom prompt");
        var node = BulkNode("old static\n# Session-specific guidance\ndynamic content");

        var result = await rewriter.RewriteAsync(node);

        result.Mutated.ShouldBeTrue();
        result.IdentityNotRecognized.ShouldBeFalse();
        BulkText(node).ShouldBe("custom prompt\n# Session-specific guidance\ndynamic content");
    }

    [Fact]
    public async Task HappyPath_WithIdentityBlock_NoIdentityNotRecognized()
    {
        var rewriter = BuildRewriter("custom prompt");
        var node = Parse("""
            {"system": [
                {"type": "text", "text": "x-anthropic-billing-header cc_version=test"},
                {"type": "text", "text": "You are Claude Code, Anthropic's official CLI for Claude."},
                {"type": "text", "text": "old static\n# Session-specific guidance\ndynamic content"}
            ]}
            """);

        var result = await rewriter.RewriteAsync(node);

        result.Mutated.ShouldBeTrue();
        result.IdentityNotRecognized.ShouldBeFalse();
        result.FailureMode.ShouldBeNull();
    }

    [Fact]
    public async Task FallbackBoundary_AutoMemory()
    {
        var rewriter = BuildRewriter("custom prompt");
        var node = BulkNode("old static\n# auto memory\ndynamic content");

        var result = await rewriter.RewriteAsync(node);

        result.Mutated.ShouldBeTrue();
        BulkText(node).ShouldBe("custom prompt\n# auto memory\ndynamic content");
    }

    [Fact]
    public async Task FallbackBoundary_Environment()
    {
        var rewriter = BuildRewriter("custom prompt");
        var node = BulkNode("old static\n# Environment\ndynamic content");

        var result = await rewriter.RewriteAsync(node);

        result.Mutated.ShouldBeTrue();
        BulkText(node).ShouldBe("custom prompt\n# Environment\ndynamic content");
    }

    [Fact]
    public async Task LongestBlockChosen_MutatesOnlyBulk()
    {
        var rewriter = BuildRewriter("custom prompt");
        var node = Parse("""
            {"system": [
                {"type": "text", "text": "x-anthropic-billing-header cc_version=test"},
                {"type": "text", "text": "You are Claude Code, Anthropic's official CLI for Claude."},
                {"type": "text", "text": "short block\n# Session-specific guidance\ndynamic"},
                {"type": "text", "text": "this is the longer block that should be chosen as bulk\n# Session-specific guidance\ndynamic"}
            ]}
            """);

        var result = await rewriter.RewriteAsync(node);

        result.Mutated.ShouldBeTrue();
        var arr = node["system"]!.AsArray();
        arr[2]!["text"]!.GetValue<string>().ShouldBe("short block\n# Session-specific guidance\ndynamic");
        arr[3]!["text"]!.GetValue<string>().ShouldBe("custom prompt\n# Session-specific guidance\ndynamic");
    }

    [Fact]
    public async Task AttributionBlockSkipped()
    {
        var rewriter = BuildRewriter("custom prompt");
        var node = Parse("""
            {"system": [
                {"type": "text", "text": "x-anthropic-billing-header cc_version=test"},
                {"type": "text", "text": "You are Claude Code, Anthropic's official CLI for Claude."},
                {"type": "text", "text": "static content\n# Session-specific guidance\ndynamic"}
            ]}
            """);

        var result = await rewriter.RewriteAsync(node);

        result.Mutated.ShouldBeTrue();
        var arr = node["system"]!.AsArray();
        arr[0]!["text"]!.GetValue<string>().ShouldStartWith("x-anthropic-billing-header");
        arr[2]!["text"]!.GetValue<string>().ShouldBe("custom prompt\n# Session-specific guidance\ndynamic");
    }

    [Fact]
    public async Task VariableSubstitution()
    {
        File.WriteAllText(_promptFile, "Hello {{name}}, welcome to {{place}}.");
        var rewriter = BuildRewriter(
            promptContent: null,
            variables: new() { ["name"] = "Alice", ["place"] = "Wonderland" });
        var node = BulkNode("old\n# Session-specific guidance\ndynamic");

        var result = await rewriter.RewriteAsync(node);

        result.Mutated.ShouldBeTrue();
        BulkText(node).ShouldStartWith("Hello Alice, welcome to Wonderland.");
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private PromptRewriter BuildRewriter(
        string? promptContent,
        string? promptFilePath = null,
        Dictionary<string, string>? variables = null)
    {
        if (promptContent is not null)
            File.WriteAllText(_promptFile, promptContent);

        var opts = new ProxyOptions
        {
            PromptFilePath = promptFilePath ?? _promptFile,
            Variables = variables ?? new(),
        };
        return new PromptRewriter(new FakeOptionsMonitor<ProxyOptions>(opts), NullLogger<PromptRewriter>.Instance);
    }

    private static JsonNode Parse(string json) => JsonNode.Parse(json)!;

    private static JsonNode BulkNode(string text) =>
        new JsonObject
        {
            ["system"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = "x-anthropic-billing-header cc_version=test" },
                new JsonObject { ["type"] = "text", ["text"] = "You are Claude Code, Anthropic's official CLI for Claude." },
                new JsonObject { ["type"] = "text", ["text"] = text }
            }
        };

    // Returns the text of the last system block (the bulk block in BulkNode fixtures).
    private static string BulkText(JsonNode node) =>
        node["system"]!.AsArray()[^1]!["text"]!.GetValue<string>();
}
