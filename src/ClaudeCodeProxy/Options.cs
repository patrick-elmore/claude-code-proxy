namespace ClaudeCodeProxy;

public sealed class ProxyOptions
{
    /// <summary>
    /// Master switch for system-prompt rewriting. When false, the proxy
    /// forwards every request byte-for-byte to the upstream — no JSON
    /// parse, no rewriter call, no mutated-body dump. The service still
    /// listens and forwards; only the mutation step is skipped.
    /// </summary>
    public bool Enabled { get; set; } = true;

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

public sealed class StatsOptions
{
    /// <summary>
    /// Directory where daily stats JSON files are written.
    /// One file per local day: stats-YYYYMMDD.json.
    /// </summary>
    public string OutputPath { get; set; } = "./stats";
}
