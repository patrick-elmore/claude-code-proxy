using System.Text.Json.Nodes;

namespace ClaudeCodeProxy;

// Anthropic's /v1/messages rejects text content blocks whose text is empty or
// whitespace-only: "messages: text content blocks must be non-empty". Claude Code
// occasionally emits leading empty text blocks on assistant turns reconstructed
// from compacted/resumed sessions (a {"type":"text","text":""} block before the
// thinking block). This sanitization pass strips such blocks before forwarding so
// the request reaches the upstream in a shape the API accepts.
//
// Pure function on a parsed body; returns true if any block was removed.
public static class MessageSanitizer
{
    public static bool Sanitize(JsonNode? root)
    {
        if (root is not JsonObject body) return false;
        if (body["messages"] is not JsonArray messages || messages.Count == 0) return false;

        var removedAny = false;
        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i] is not JsonObject message) continue;
            // content may be a string (shorthand) or an array of typed blocks.
            if (message["content"] is not JsonArray blocks) continue;

            for (var b = blocks.Count - 1; b >= 0; b--)
            {
                if (blocks[b] is not JsonObject block) continue;
                if (block["type"]?.GetValue<string>() != "text") continue;
                var text = block["text"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(text))
                {
                    blocks.RemoveAt(b);
                    removedAny = true;
                }
            }
        }
        return removedAny;
    }
}
