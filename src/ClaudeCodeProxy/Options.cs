namespace ClaudeCodeProxy;

public sealed class ProxyOptions
{
    // When false, disables JSON parsing and prompt rewriting; the service still proxies requests verbatim.
    public bool Enabled { get; set; } = true;

    // If empty or the file is missing, requests pass through unmodified.
    public string PromptFilePath { get; set; } = string.Empty;

    // e.g. "https://aiplatform.googleapis.com/v1" — the incoming request path is appended verbatim.
    public string VertexBaseUrl { get; set; } = "https://global-aiplatform.googleapis.com/v1";

    // {{key}} substitutions applied to the prompt file at request time.
    public Dictionary<string, string> Variables { get; set; } = new();
}

public sealed class DumpOptions
{
    public bool Enabled { get; set; } = false;
    public int MaxRequests { get; set; } = 5;

    // Use Windows-style paths — WSL paths are misinterpreted. Default is a placeholder; install.ps1 rewrites this on first install.
    public string OutputPath { get; set; } = "<set-to-a-writable-directory>";
}

public sealed class StatsOptions
{
    // One file per local day: stats-YYYYMMDD.json.
    public string OutputPath { get; set; } = "./stats";
}
