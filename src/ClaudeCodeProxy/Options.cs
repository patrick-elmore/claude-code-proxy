namespace ClaudeCodeProxy;

public sealed class ProxyOptions
{
    /// <summary>
    /// Path to the file containing the custom system prompt that replaces
    /// the static behavioral portion of the bulk system block. If empty
    /// or the file is missing, the proxy passes requests through unmodified.
    /// </summary>
    public string PromptFilePath { get; set; } = string.Empty;

    /// <summary>
    /// Upstream Vertex AI endpoint, including the API version segment.
    /// Example: "https://global-aiplatform.googleapis.com/v1"
    /// The incoming request path is appended verbatim.
    /// </summary>
    public string VertexBaseUrl { get; set; } = "https://global-aiplatform.googleapis.com/v1";

    /// <summary>
    /// Variables substituted into the prompt file at request time.
    /// Each "{{key}}" in the prompt is replaced with the corresponding value.
    /// Reserved for future use; empty by default.
    /// </summary>
    public Dictionary<string, string> Variables { get; set; } = new();
}

public sealed class DumpOptions
{
    public bool Enabled { get; set; } = false;
    public int MaxRequests { get; set; } = 5;

    /// <summary>
    /// Output directory for dumps. Use a Windows-style path
    /// (e.g. "C:/misc/claude-code-proxy/dumps") since the proxy
    /// runs as a Windows process — WSL paths get misinterpreted.
    /// </summary>
    public string OutputPath { get; set; } = "./dumps";
}
