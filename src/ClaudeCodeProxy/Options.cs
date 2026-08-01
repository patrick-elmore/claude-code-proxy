namespace ClaudeCodeProxy;

public sealed class ProxyOptions
{
    // When false, disables JSON parsing and prompt rewriting; the service still proxies requests verbatim.
    public bool Enabled { get; set; } = true;

    // If empty or the file is missing, requests pass through unmodified.
    public string PromptFilePath { get; set; } = string.Empty;

    // e.g. "https://aiplatform.googleapis.com/v1" — the incoming request path is appended verbatim.
    public string VertexBaseUrl { get; set; } = "https://global-aiplatform.googleapis.com/v1";

    // e.g. "https://api.anthropic.com" — no trailing /v1. Used for /v1/messages* paths.
    public string AnthropicBaseUrl { get; set; } = string.Empty;

    // {{key}} substitutions applied to the prompt file at request time.
    public Dictionary<string, string> Variables { get; set; } = new();
}

public sealed class DumpOptions
{
    public bool Enabled { get; set; } = false;
    public bool FailureDumpEnabled { get; set; } = false;
    public int MaxRequests { get; set; } = 5;

    // Use Windows-style paths — WSL paths are misinterpreted. Default is a placeholder; install.ps1 rewrites this on first install.
    public string OutputPath { get; set; } = "<set-to-a-writable-directory>";
}

public sealed class HeaderDumpOptions
{
    // Diagnostic only — appends every incoming request header (including auth) to C:\tmp\headers.txt.
    public bool Enabled { get; set; } = false;
}

public sealed class StatsOptions
{
    // One file per stats day: stats-YYYYMMDD.json.
    public string OutputPath { get; set; } = "./stats";

    // IANA time zone id the day boundary is evaluated in.
    public string DayBoundaryTimeZone { get; set; } = "America/New_York";

    // Wall-clock time in DayBoundaryTimeZone where a new stats day begins.
    // Midnight means each stats period runs from 00:00 to 00:00 in DayBoundaryTimeZone.
    public TimeSpan DayBoundaryTime { get; set; } = TimeSpan.Zero;
}
