using System.Text.Json.Nodes;
using Shouldly;

namespace ClaudeCodeProxy.Tests;

public sealed class MessageSanitizerTests
{
    // -----------------------------------------------------------------------
    // Not-applicable — nothing to sanitize
    // -----------------------------------------------------------------------

    [Fact]
    public void NullRoot_False()
    {
        MessageSanitizer.Sanitize(null).ShouldBeFalse();
    }

    [Fact]
    public void NoMessagesField_False()
    {
        var node = JsonNode.Parse("""{"system": []}""")!;

        MessageSanitizer.Sanitize(node).ShouldBeFalse();
    }

    [Fact]
    public void EmptyMessagesArray_False()
    {
        var node = JsonNode.Parse("""{"messages": []}""")!;

        MessageSanitizer.Sanitize(node).ShouldBeFalse();
    }

    [Fact]
    public void NoEmptyTextBlocks_False_AndUnchanged()
    {
        var node = JsonNode.Parse("""
            {"messages": [
                {"role": "user", "content": "hello"},
                {"role": "assistant", "content": [
                    {"type": "text", "text": "real text"},
                    {"type": "tool_use", "id": "t1", "name": "foo", "input": {}}
                ]}
            ]}
            """)!;

        MessageSanitizer.Sanitize(node).ShouldBeFalse();
        node["messages"]!.AsArray()[1]!["content"]!.AsArray().Count.ShouldBe(2);
    }

    // -----------------------------------------------------------------------
    // Sanitization — empty text blocks removed
    // -----------------------------------------------------------------------

    [Fact]
    public void EmptyTextBlock_Removed_True()
    {
        // The exact shape from the failing request: a leading empty text block
        // before thinking + tool_use on a compacted/resumed assistant turn.
        var node = JsonNode.Parse("""
            {"messages": [
                {"role": "user", "content": "hello"},
                {"role": "assistant", "content": [
                    {"type": "text", "text": ""},
                    {"type": "thinking", "thinking": "reasoning", "signature": ""},
                    {"type": "text", "text": "I'll do the thing."},
                    {"type": "tool_use", "id": "t1", "name": "foo", "input": {}}
                ]}
            ]}
            """)!;

        var result = MessageSanitizer.Sanitize(node);

        result.ShouldBeTrue();
        var blocks = node["messages"]!.AsArray()[1]!["content"]!.AsArray();
        blocks.Count.ShouldBe(3);
        blocks[0]!["type"]!.GetValue<string>().ShouldBe("thinking");
        blocks[1]!["type"]!.GetValue<string>().ShouldBe("text");
        blocks[1]!["text"]!.GetValue<string>().ShouldBe("I'll do the thing.");
        blocks[2]!["type"]!.GetValue<string>().ShouldBe("tool_use");
    }

    [Fact]
    public void WhitespaceOnlyTextBlock_Removed_True()
    {
        var node = JsonNode.Parse("""
            {"messages": [
                {"role": "assistant", "content": [
                    {"type": "text", "text": "   "},
                    {"type": "text", "text": "keep"}
                ]}
            ]}
            """)!;

        MessageSanitizer.Sanitize(node).ShouldBeTrue();
        var blocks = node["messages"]!.AsArray()[0]!["content"]!.AsArray();
        blocks.Count.ShouldBe(1);
        blocks[0]!["text"]!.GetValue<string>().ShouldBe("keep");
    }

    [Fact]
    public void MultipleMessagesEachWithEmptyText_AllRemoved()
    {
        // Mirrors the real failure: four assistant turns each starting with an empty text block.
        var node = JsonNode.Parse("""
            {"messages": [
                {"role": "user", "content": "q"},
                {"role": "assistant", "content": [{"type": "text", "text": ""}, {"type": "text", "text": "a1"}]},
                {"role": "user", "content": "r"},
                {"role": "assistant", "content": [{"type": "text", "text": ""}, {"type": "text", "text": "a2"}]}
            ]}
            """)!;

        MessageSanitizer.Sanitize(node).ShouldBeTrue();
        node["messages"]!.AsArray()[1]!["content"]!.AsArray().Count.ShouldBe(1);
        node["messages"]!.AsArray()[3]!["content"]!.AsArray().Count.ShouldBe(1);
    }

    [Fact]
    public void EmptyTextBetweenNonEmpty_Removed_OthersPreserved()
    {
        var node = JsonNode.Parse("""
            {"messages": [
                {"role": "assistant", "content": [
                    {"type": "text", "text": "before"},
                    {"type": "text", "text": ""},
                    {"type": "text", "text": "after"}
                ]}
            ]}
            """)!;

        MessageSanitizer.Sanitize(node).ShouldBeTrue();
        var blocks = node["messages"]!.AsArray()[0]!["content"]!.AsArray();
        blocks.Count.ShouldBe(2);
        blocks[0]!["text"]!.GetValue<string>().ShouldBe("before");
        blocks[1]!["text"]!.GetValue<string>().ShouldBe("after");
    }

    // -----------------------------------------------------------------------
    // Edge cases — must not corrupt valid content
    // -----------------------------------------------------------------------

    [Fact]
    public void StringContentMessage_Untouched()
    {
        // content as a bare string (shorthand form) — not an array, nothing to scan.
        var node = JsonNode.Parse("""{"messages": [{"role": "user", "content": ""}]}""")!;

        MessageSanitizer.Sanitize(node).ShouldBeFalse();
        node["messages"]!.AsArray()[0]!["content"]!.GetValue<string>().ShouldBe("");
    }

    [Fact]
    public void EmptyTextBlockInSystemArray_NotTouched()
    {
        // Sanitization targets messages[] only. An empty text block in system[]
        // is outside this pass's scope (and not what Anthropic rejects here).
        var node = JsonNode.Parse("""
            {"system": [{"type": "text", "text": ""}], "messages": []}
            """)!;

        MessageSanitizer.Sanitize(node).ShouldBeFalse();
        node["system"]!.AsArray().Count.ShouldBe(1);
    }

    [Fact]
    public void ToolUseAndToolResultBlocks_Untouched()
    {
        var node = JsonNode.Parse("""
            {"messages": [
                {"role": "assistant", "content": [
                    {"type": "text", "text": ""},
                    {"type": "tool_use", "id": "t1", "name": "foo", "input": {"a": 1}}
                ]},
                {"role": "user", "content": [
                    {"type": "tool_result", "tool_use_id": "t1", "content": ""}
                ]}
            ]}
            """)!;

        MessageSanitizer.Sanitize(node).ShouldBeTrue();
        var assistant = node["messages"]!.AsArray()[0]!["content"]!.AsArray();
        assistant.Count.ShouldBe(1);
        assistant[0]!["type"]!.GetValue<string>().ShouldBe("tool_use");
        // tool_result with empty string content must be preserved — Anthropic allows that.
        var user = node["messages"]!.AsArray()[1]!["content"]!.AsArray();
        user.Count.ShouldBe(1);
        user[0]!["type"]!.GetValue<string>().ShouldBe("tool_result");
    }
}
